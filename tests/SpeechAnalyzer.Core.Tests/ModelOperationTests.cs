using SpeechAnalyzer.Core;

internal static class ModelOperationTests
{
    private static void Check(bool value, string message = "Сценарий модельной операции не пройден")
    { if (!value) throw new Exception(message); }
    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), "speech-operations-" + Guid.NewGuid().ToString("N"));
    private static (ProcessingRequest Request, ModelOperationVersions Versions) Operation(OperationKind kind = OperationKind.Answer)
    {
        var segment = new TranscriptSegment(Guid.NewGuid(), AudioChannel.Other, 0, 10, DateTimeOffset.UtcNow, "Что по срокам?", true, SegmentMeaning.Question, "date");
        var request = new ProcessingRequest(Guid.NewGuid(), kind, 10, ConversationState.Empty, [segment], "", "Кратко подведи итоги", "проект: пилот");
        return (request, new(OperationVersionHash.Of("проект: пилот"), OperationVersionHash.Of("files-hash"), OperationVersionHash.Of("settings-hash"), "high"));
    }
    private static DurableModelOperation Prepared(ProcessingRequest request, ModelOperationVersions versions)
        => new(request.OperationId, request, versions, ModelOperationPhase.Prepared, null, null, DateTimeOffset.UtcNow);

    public static async Task ResumePreparedAndResult()
    {
        string directory = TempDirectory(); Directory.CreateDirectory(directory);
        try
        {
            var (request, versions) = Operation();
            var source = new SimulatedTranscriptSource(new ManualClock()) { DemoSpeechEnabled = false };
            var backend = new CountingBackend();
            SessionJournal journal = new(directory); journal.PrepareModelOperation(Prepared(request, versions));
            var engine = new ConversationEngine(new ManualClock(), source, backend, journal);
            engine.ResumeRecoveredOperations(); await engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check(backend.Calls == 1 && journal.Read().Messages.Count == 1);
            Check(journal.ModelOperations.Single().Phase == ModelOperationPhase.Completed);
            await engine.DisposeAsync();

            var (resultRequest, resultVersions) = Operation();
            var result = SimulatedModelBackend.Evaluate(resultRequest);
            var nextDirectory = Path.Combine(directory, "result-received"); Directory.CreateDirectory(nextDirectory);
            var resultJournal = new SessionJournal(nextDirectory); resultJournal.PrepareModelOperation(Prepared(resultRequest, resultVersions));
            resultJournal.AdvanceModelOperation(resultRequest.OperationId, ModelOperationPhase.ResultReceived, result: result);
            var noCall = new CountingBackend();
            var recovered = new ConversationEngine(new ManualClock(), source, noCall, resultJournal);
            recovered.ResumeRecoveredOperations(); await recovered.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check(noCall.Calls == 0 && resultJournal.Read().Messages.Count == 1);
            Check(resultJournal.ModelOperations.Single().Phase == ModelOperationPhase.Completed);
            await recovered.DisposeAsync();
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task UncertainOutcomeNeedsReconciliation()
    {
        string directory = TempDirectory(); Directory.CreateDirectory(directory);
        try
        {
            var (request, versions) = Operation();
            var journal = new SessionJournal(directory); journal.PrepareModelOperation(Prepared(request, versions));
            journal.AdvanceModelOperation(request.OperationId, ModelOperationPhase.RequestSentUnknown);
            var backend = new CountingBackend();
            var engine = new ConversationEngine(new ManualClock(), new SimulatedTranscriptSource(new ManualClock()), backend, new SessionJournal(directory));
            Check(engine.Snapshot().Error?.Contains("неопределённым", StringComparison.Ordinal) == true);
            Check(new SessionJournal(directory).ModelOperations.Single().Phase == ModelOperationPhase.ReconciliationRequired);
            engine.ResumeRecoveredOperations(); await engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Check(backend.Calls == 0 && engine.Snapshot().CanRetry == false);
            await engine.DisposeAsync();
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task DurableTransitionsAndRetry()
    {
        string directory = TempDirectory(); Directory.CreateDirectory(directory);
        try
        {
            var (_, versions) = Operation();
            var source = new SimulatedTranscriptSource(new ManualClock()) { DemoSpeechEnabled = false, FinalizationDelay = TimeSpan.Zero };
            var journal = new SessionJournal(directory);
            var engine = new ConversationEngine(new ManualClock(), source, new PhaseBackend(), journal, () => versions);
            engine.Start(); source.Inject("Нужно начать пилот.", AudioChannel.Other);
            engine.Answer(); await engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var persisted = journal.ModelOperations.Single();
            Check(persisted.Phase == ModelOperationPhase.Completed && persisted.RemoteOperationId == "turn-phase-test");
            Check(persisted.Request.BoundaryMs == 1 && persisted.Request.Segments.Single().Text == "Нужно начать пилот.");
            Check(persisted.Versions.ReasoningEffort == "high" && journal.Read().State.ProcessedThroughMs == 1);
            await engine.DisposeAsync();
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task TransitionFailures()
    {
        foreach (var phase in new[] { ModelOperationPhase.Prepared, ModelOperationPhase.RequestSentUnknown,
            ModelOperationPhase.RemoteAccepted, ModelOperationPhase.ResultReceived, ModelOperationPhase.Completed })
        {
            var source = new SimulatedTranscriptSource(new ManualClock()) { DemoSpeechEnabled = false, FinalizationDelay = TimeSpan.Zero };
            var store = new FailingOperationStore(phase);
            var backend = new PhaseBackend();
            var engine = new ConversationEngine(new ManualClock(), source, backend, store);
            engine.Start(); source.Inject("Проверить отказ записи.", AudioChannel.Other); engine.Answer();
            await engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (phase is ModelOperationPhase.RemoteAccepted or ModelOperationPhase.ResultReceived)
            {
                Check(store.ModelOperations.Single().Phase == ModelOperationPhase.ReconciliationRequired,
                    $"Сбой {phase} не заблокировал неопределённый повтор.");
                Check(engine.Snapshot().CanRetry == false && store.Read().Messages.Count == 0);
            }
            else
            {
                Check(engine.Snapshot().CanRetry, $"Сбой {phase} не сохранил безопасный повтор.");
                engine.Retry(); await engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Check(!engine.Snapshot().CanRetry && store.Read().Messages.Count == 1,
                    $"Повтор после сбоя {phase} не завершил commit ровно один раз.");
                if (phase == ModelOperationPhase.Completed) Check(backend.Calls == 1, "Результат модели запрошен повторно после commit.");
            }
            await engine.DisposeAsync();
        }
    }

    private sealed class CountingBackend : IModelBackend
    {
        public int Calls;
        public Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Task.FromResult(SimulatedModelBackend.Evaluate(request)); }
    }
    private sealed class PhaseBackend : IDurableModelBackend
    {
        public int Calls;
        public Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SimulatedModelBackend.Evaluate(request));
        public async Task<ProcessingResult> ProcessDurableAsync(ProcessingRequest request, ModelOperationVersions versions,
            Func<ModelOperationPhase, string?, Task> persistPhase, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await persistPhase(ModelOperationPhase.RequestSentUnknown, null);
            await persistPhase(ModelOperationPhase.RemoteAccepted, "turn-phase-test");
            return SimulatedModelBackend.Evaluate(request);
        }
    }
    private sealed class FailingOperationStore(ModelOperationPhase failAt) : IConversationStore, IModelOperationStore
    {
        private readonly MemoryConversationStore conversation = new();
        private readonly Dictionary<Guid, DurableModelOperation> operations = [];
        private bool failed;
        public StoreSnapshot Read() => conversation.Read();
        public Task CommitAsync(ProcessingCommit commit, CancellationToken cancellationToken) => conversation.CommitAsync(commit, cancellationToken);
        public IReadOnlyList<DurableModelOperation> ModelOperations => operations.Values.ToArray();
        public void PrepareModelOperation(DurableModelOperation operation)
        {
            Fail(operation.Phase);
            operations[operation.OperationId] = operation;
        }
        public void AdvanceModelOperation(Guid operationId, ModelOperationPhase phase, string? remoteOperationId = null, ProcessingResult? result = null)
        {
            Fail(phase);
            var previous = operations[operationId];
            operations[operationId] = previous with { Phase = phase, RemoteOperationId = remoteOperationId ?? previous.RemoteOperationId,
                Result = result ?? previous.Result };
        }
        private void Fail(ModelOperationPhase phase)
        {
            if (!failed && phase == failAt) { failed = true; throw new IOException("Тестовый отказ записи операции: " + phase); }
        }
    }
    private sealed class ManualClock : IClock
    {
        public TimeSpan Now => TimeSpan.Zero;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
