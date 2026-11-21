using System.Text.Json;
using SpeechAnalyzer.Core;

static class ApplicationRetrievalTests
{
    private const string Control = "Точный контрольный код: TEST-739-CONTROL, ёж 🦊.\nВторая строка.";
    private static void Check(bool value, string message = "Нарушен поиск оригиналов приложением") { if (!value) throw new Exception(message); }
    private sealed class Clock : IClock { public TimeSpan Now => TimeSpan.Zero; public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
    private sealed class Source(IReadOnlyList<TranscriptSegment> segments) : ITranscriptSource
    {
        public CaptureStatus Status => CaptureStatus.Stopped;
        public long CaptureBoundaryMs => 3000;
        public long FinalizedThroughMs => 3000;
        public IReadOnlyList<TranscriptSegment> Segments => segments;
        public void Start() { } public void Pause() { } public void Resume() { } public void Stop() { } public void Advance() { }
        public long SnapshotBoundary() => CaptureBoundaryMs;
        public Task WaitForFinalizationAsync(long boundaryMs, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class Fixture : IDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "speech-retrieval-" + Guid.NewGuid().ToString("N"));
        public SessionJournal Journal;
        public readonly ProcessingRequest Request;
        public readonly ModelOperationVersions Versions = new("context", "files", "application-v1", "high");
        public readonly TranscriptSegment Original;
        public Fixture(string original = Control)
        {
            Journal = new(DirectoryPath);
            Original = new(Guid.NewGuid(), AudioChannel.Other, 0, 100, DateTimeOffset.UnixEpoch, original, true);
            var current = Original with { Id = Guid.NewGuid(), Channel = AudioChannel.You, StartMs = 1001, EndMs = 2000, Text = "Продолжаем проект." };
            var future = Original with { Id = Guid.NewGuid(), StartMs = 2001, EndMs = 3000, Text = "Точный контрольный код: FUTURE-MUST-NOT-LEAK" };
            foreach (var segment in new[] { Original, current, future }) Journal.AppendTranscript(segment);
            Request = new(Guid.NewGuid(), OperationKind.Answer, 2000, new(1000, [], [], []), [current], "Найди прежний контрольный код.", "", "Синтетика");
            Journal.PrepareModelOperation(new(Request.OperationId, Request, Versions, ModelOperationPhase.Prepared, null, null, DateTimeOffset.UnixEpoch));
        }
        public ConversationEngine Engine(IRetrievalModelClient client, FailureStore? store = null)
        {
            var persistence = store ?? new FailureStore(Journal);
            if (client is Client fake) fake.ReadStored ??= id => Journal.ReadRetrieval(id);
            return new(new Clock(), new Source(Journal.Transcript), new ApplicationRetrievalBackend(client, persistence, newWorkflowVersion: 4), persistence);
        }
        public void Reopen() => Journal = new(DirectoryPath);
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
    private sealed class Client : IRetrievalModelClient
    {
        public int Calls, Reads;
        public bool LoseResponse, AlwaysRetrieve, WrongOperation, InventCitation, Malformed;
        public Guid? RequestedSegment { get; set; }
        public Func<Guid, RetrievalWorkflow?>? ReadStored;
        public readonly List<string> Prompts = [];
        private readonly Dictionary<Guid, string> responses = [];
        public int AcceptedSteps { get; private set; }
        public async Task<string> SendAsync(string prompt, JsonElement schema, string? effort,
            Func<ModelOperationPhase, string?, Task> persistPhase, CancellationToken cancellationToken)
        {
            Calls++; Prompts.Add(prompt);
            using var input = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
            var root = input.RootElement;
            Guid stepId = root.GetProperty("operationId").GetGuid();
            Guid parentId = root.GetProperty("parentOperationId").GetGuid();
            if (ReadStored is not null)
                Check(ReadStored(parentId)?.Steps.Last() is { Phase: ModelOperationPhase.Prepared } saved && saved.Id == stepId && saved.Prompt == prompt,
                    "Prompt и выдача не сохранены до отправки модели");
            Check(root.GetProperty("transcriptBoundaryMs").GetInt64() == 2000);
            var batches = root.GetProperty("suppliedOriginals").EnumerateArray().ToArray();
            string response;
            if (batches.Length == 0 || AlwaysRetrieve)
                response = JsonSerializer.Serialize(new { retrievalRequest = new { operationId = WrongOperation ? Guid.NewGuid() : parentId, text = "Точный контрольный код", segmentId = RequestedSegment }, finalResult = (object?)null });
            else
            {
                var passages = batches.SelectMany(b => b.GetProperty("Passages").EnumerateArray()).ToArray();
                string text = passages.Length == 0 ? "Оригинал недоступен в заданных лимитах." : passages[0].GetProperty("Segment").GetProperty("Text").GetString()!;
                Guid[] ids = passages.Select(p => p.GetProperty("Segment").GetProperty("Id").GetGuid()).Distinct().ToArray();
                if (InventCitation) ids = [Guid.NewGuid()];
                response = JsonSerializer.Serialize(new { retrievalRequest = (object?)null, finalResult = new
                {
                    processedThroughMs = 2000, facts = Array.Empty<string>(), commitments = Array.Empty<string>(), questions = Array.Empty<object>(),
                    sourceReferences = ids.Length == 0 ? Array.Empty<object>() : new object[] { new { itemId = "exact", text,
                        sourceSegmentIds = ids.Take(1).ToArray(), evidence = new[] { new { sourceSegmentId = ids[0], text } } } },
                    questionReferences = Array.Empty<object>(), displayedQuestionIds = Array.Empty<string>(), displayText = text,
                    outcome = "FullAnswer", summary = (string?)null, limitations = (string?)null, openQuestionIds = Array.Empty<string>(),
                    interpretations = Array.Empty<object>()
                } });
            }
            if (Malformed) response = "{\"finalResult\":";
            await persistPhase(ModelOperationPhase.RequestSentUnknown, null);
            AcceptedSteps++;
            responses[stepId] = response; // Удалённый результат существует до возможной потери локального ответа.
            await persistPhase(ModelOperationPhase.RemoteAccepted, "turn-" + stepId.ToString("N"));
            if (LoseResponse) { LoseResponse = false; throw new IOException("Потерян ответ после удалённого принятия"); }
            return response;
        }
        public Task<string> ReadAsync(Guid stepId, string? remoteTurnId, CancellationToken cancellationToken)
        {
            Reads++;
            return responses.TryGetValue(stepId, out var response) ? Task.FromResult(response)
                : throw new IOException("Удалённый результат пока не найден.");
        }
    }
    private sealed class FailureStore(SessionJournal journal) : IApplicationRetrievalStore, IConversationStore, IModelOperationStore
    {
        public Func<RetrievalWorkflow, bool>? FailSave;
        public Func<ModelOperationPhase, bool>? FailAdvance;
        public bool FailAfterSave, FailCommit, FailAfterCommit, FailRecord, FailAfterRecord, FailAfterAdvance;
        private bool failed;
        public RetrievalWorkflow? ReadRetrieval(Guid operationId) => journal.ReadRetrieval(operationId);
        public void SaveRetrieval(RetrievalWorkflow workflow)
        {
            if (!failed && FailSave?.Invoke(workflow) == true)
            {
                failed = true;
                if (FailAfterSave) journal.SaveRetrieval(workflow);
                throw new IOException("Сбой записи промежуточного шага");
            }
            journal.SaveRetrieval(workflow);
        }
        public IReadOnlyList<TranscriptSegment> ReadOriginalTranscript() => journal.Transcript;
        public void RecordSuppliedOriginals(Guid operationId, IReadOnlyList<Guid> ids)
        {
            if (!failed && FailRecord && ids.Count > 0)
            {
                failed = true;
                if (FailAfterRecord) journal.RecordSuppliedOriginals(operationId, ids);
                throw new IOException("Сбой регистрации выданных оригиналов");
            }
            journal.RecordSuppliedOriginals(operationId, ids);
        }
        public StoreSnapshot Read() => journal.Read();
        public async Task CommitAsync(ProcessingCommit commit, CancellationToken cancellationToken)
        {
            if (!failed && FailCommit)
            {
                failed = true;
                if (FailAfterCommit) await journal.CommitAsync(commit, cancellationToken);
                throw new IOException("Сбой commit");
            }
            await journal.CommitAsync(commit, cancellationToken);
        }
        public IReadOnlyList<DurableModelOperation> ModelOperations => journal.ModelOperations;
        public void PrepareModelOperation(DurableModelOperation operation) => journal.PrepareModelOperation(operation);
        public void AdvanceModelOperation(Guid id, ModelOperationPhase phase, string? remoteOperationId = null, ProcessingResult? result = null)
        {
            if (!failed && FailAdvance?.Invoke(phase) == true)
            {
                failed = true;
                if (FailAfterAdvance) journal.AdvanceModelOperation(id, phase, remoteOperationId, result);
                throw new IOException("Сбой сохранения родительской фазы");
            }
            journal.AdvanceModelOperation(id, phase, remoteOperationId, result);
        }
        public void RecordSourceRead(Guid id, IReadOnlyList<Guid> ids) => journal.RecordSourceRead(id, ids);
    }
    private static async Task Idle(ConversationEngine engine) => await engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    public static async Task ExactPassageAndBoundary()
    {
        using var f = new Fixture(); var client = new Client();
        byte[] before = File.ReadAllBytes(Path.Combine(f.DirectoryPath, "transcript.jsonl"));
        await using var engine = f.Engine(client);
        engine.ResumeRecoveredOperations(); await Idle(engine);
        Check(engine.Snapshot().Error is null, engine.Snapshot().Error ?? "");
        Check(client.Calls == 2 && client.Reads == 0);
        Check(!client.Prompts[0].Contains("TEST-739-CONTROL") && client.Prompts[1].Contains("TEST-739-CONTROL"));
        Check(client.Prompts.All(p => !p.Contains("FUTURE-MUST-NOT-LEAK")));
        Check(f.Journal.Read().Messages.Single().Text == Control);
        Check(f.Journal.ModelOperations.Single().RetrievedSourceIds!.SequenceEqual(new[] { f.Original.Id }));
        var workflow = f.Journal.ReadRetrieval(f.Request.OperationId)!;
        Check(workflow.Steps.Count == 2 && workflow.Steps.All(s => s.Response is not null));
        Check(workflow.Batches.Single().Passages.Single().Segment == f.Original);
        Check(f.Journal.ModelOperations.Single().Result!.SourceReferences!.Single().SourceSegmentIds.Single() == f.Original.Id);
        Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(f.DirectoryPath, "transcript.jsonl")))) ;
    }
    public static async Task RemoteRecovery()
    {
        using var f = new Fixture(); var client = new Client { LoseResponse = true };
        await using (var engine = f.Engine(client))
        {
            engine.ResumeRecoveredOperations(); await Idle(engine);
            Check(engine.Snapshot().CanRetry == false && f.Journal.ModelOperations.Single().Phase == ModelOperationPhase.ReconciliationRequired);
            Check(f.Journal.Read().Messages.Count == 0 && f.Journal.Read().State.ProcessedThroughMs == 0);
        }
        f.Reopen();
        var backend = new ApplicationRetrievalBackend(client, f.Journal, newWorkflowVersion: 4);
        await using var restored = new ConversationEngine(new Clock(), new Source(f.Journal.Transcript), backend, f.Journal);
        restored.ResumeRecoveredOperations(); await Idle(restored); Check(client.Calls == 1);
        await restored.ReconcileIntermediateAsync(f.Request.OperationId, (operation, token) => backend.ReconcileStepAsync(operation.OperationId, token));
        await Idle(restored);
        Check(client.Reads == 1 && client.Calls == 2 && f.Journal.Read().Messages.Single().Text == Control);
    }
    public static async Task PersistenceRecovery()
    {
        for (int point = 0; point < 5; point++)
        {
            using var f = new Fixture(); var client = new Client();
            int failurePoint = point;
            var store = new FailureStore(f.Journal)
            {
                FailAfterSave = point is 2 or 4,
                FailSave = w => failurePoint switch
                {
                    0 => w.Steps.Count == 1 && w.Steps[0].Phase == ModelOperationPhase.Prepared,
                    1 or 2 => w.Batches.Count == 1 && w.Steps.Count == 1,
                    3 => w.Steps.Count == 1 && w.Steps[0].Response is not null,
                    4 => w.Steps.Count == 2 && w.Steps[1].Response is not null,
                    _ => false
                }
            };
            await using (var engine = f.Engine(client, store))
            { engine.ResumeRecoveredOperations(); await Idle(engine); Check(f.Journal.Read().Messages.Count == 0); }
            f.Reopen();
            var backend = new ApplicationRetrievalBackend(client, f.Journal, newWorkflowVersion: 4);
            await using var restored = new ConversationEngine(new Clock(), new Source(f.Journal.Transcript), backend, f.Journal);
            if (f.Journal.ModelOperations.Single().Phase == ModelOperationPhase.ReconciliationRequired)
                await restored.ReconcileIntermediateAsync(f.Request.OperationId, (operation, token) => backend.ReconcileStepAsync(operation.OperationId, token));
            else restored.ResumeRecoveredOperations();
            await Idle(restored);
            Check(restored.Snapshot().Error is null, "Точка " + point + ": " + restored.Snapshot().Error);
            Check(client.Calls == 2 && client.Reads == (point == 3 ? 1 : 0), "Повтор inference после сбоя " + point);
            Check(f.Journal.Read().Messages.Single().Text == Control);
        }
    }
    public static async Task CommitRecovery()
    {
        foreach (bool after in new[] { false, true })
        {
            using var f = new Fixture(); var client = new Client();
            await using (var engine = f.Engine(client, new(f.Journal) { FailCommit = true, FailAfterCommit = after }))
            { engine.ResumeRecoveredOperations(); await Idle(engine); Check(engine.Snapshot().CanRetry); }
            f.Reopen();
            await using var restored = f.Engine(client); restored.ResumeRecoveredOperations(); await Idle(restored);
            Check(client.Calls == 2 && f.Journal.Read().Messages.Count == 1);
            Check(f.Journal.ModelOperations.Single().Phase == ModelOperationPhase.Completed);
        }
    }
    public static async Task PhaseAndSourceRecovery()
    {
        // Каждая точка проверяется до записи и при потере подтверждения уже выполненной записи.
        for (int point = 0; point < 7; point++)
        foreach (bool after in new[] { false, true })
        {
            using var f = new Fixture(); var client = new Client();
            var store = new FailureStore(f.Journal)
            {
                FailAfterSave = after, FailAfterAdvance = after, FailAfterRecord = after,
                FailRecord = point == 6,
                FailSave = w => point switch
                {
                    0 => w.Steps.Last().Phase == ModelOperationPhase.RequestSentUnknown,
                    1 => w.Steps.Last().Phase == ModelOperationPhase.RemoteAccepted,
                    _ => false
                },
                FailAdvance = phase => point switch
                {
                    2 => phase == ModelOperationPhase.RequestSentUnknown,
                    3 => phase == ModelOperationPhase.RemoteAccepted,
                    4 => phase == ModelOperationPhase.Prepared,
                    5 => phase == ModelOperationPhase.ResultReceived,
                    _ => false
                }
            };
            byte[] transcript = File.ReadAllBytes(Path.Combine(f.DirectoryPath, "transcript.jsonl"));
            await using (var engine = f.Engine(client, store))
            {
                engine.ResumeRecoveredOperations(); await Idle(engine);
                Check(f.Journal.Read().Messages.Count == 0 && f.Journal.Read().State.ProcessedThroughMs == 0);
            }
            f.Reopen();
            var backend = new ApplicationRetrievalBackend(client, f.Journal, newWorkflowVersion: 4);
            await using var restored = new ConversationEngine(new Clock(), new Source(f.Journal.Transcript), backend, f.Journal);
            if (f.Journal.ModelOperations.Single().Phase == ModelOperationPhase.ReconciliationRequired)
                await restored.ReconcileIntermediateAsync(f.Request.OperationId, (operation, token) => backend.ReconcileStepAsync(operation.OperationId, token));
            else restored.ResumeRecoveredOperations();
            await Idle(restored);
            if (point == 0 && after)
            {
                // Намерение отправки сохранено, но результата нет. Отсутствие результата
                // не доказывает, что запрос не дошёл: безопасное восстановление остаётся заблокированным.
                Check(restored.Snapshot().Error?.Contains("не найден") == true && !restored.Snapshot().CanRetry);
                Check(client.AcceptedSteps == 0 && f.Journal.Read().Messages.Count == 0);
                Check(f.Journal.Read().State.ProcessedThroughMs == 0);
                restored.ResumeRecoveredOperations(); await Idle(restored);
                Check(client.Calls == 1);
                Check(transcript.SequenceEqual(File.ReadAllBytes(Path.Combine(f.DirectoryPath, "transcript.jsonl"))));
                continue;
            }
            Check(restored.Snapshot().Error is null, $"Точка фаз {point}/{after}: {restored.Snapshot().Error}");
            Check(client.AcceptedSteps == 2, $"Повтор принятого inference: {point}/{after}");
            Check(f.Journal.Read().Messages.Single().Text == Control);
            Check(f.Journal.ModelOperations.Single().RetrievedSourceIds!.SequenceEqual(new[] { f.Original.Id }));
            Check(transcript.SequenceEqual(File.ReadAllBytes(Path.Combine(f.DirectoryPath, "transcript.jsonl"))));
        }
    }
    public static async Task Limits()
    {
        using (var f = new Fixture())
        {
            var client = new Client { AlwaysRetrieve = true };
            await using var engine = f.Engine(client); engine.ResumeRecoveredOperations(); await Idle(engine);
            Check(client.Calls == 3 && f.Journal.Read().Messages.Count == 0);
            Check(f.Journal.ReadRetrieval(f.Request.OperationId)!.Batches.Count == 2);
            Check(engine.Snapshot().Error?.Contains("два раунда") == true);
        }
        using (var f = new Fixture("Точный контрольный код: " + new string('я', 9000)))
        {
            var client = new Client(); await using var engine = f.Engine(client); engine.ResumeRecoveredOperations(); await Idle(engine);
            var batch = f.Journal.ReadRetrieval(f.Request.OperationId)!.Batches.Single();
            Check(batch.Status == "segment_too_large" && batch.Passages.Count == 0);
            Check(f.Journal.Read().Messages.Single().Text.Contains("недоступен"));
        }
        using (var f = new Fixture())
        {
            for (int i = 0; i < 10; i++) f.Journal.AppendTranscript(f.Original with { Id = Guid.NewGuid(), StartMs = 110 + i, EndMs = 111 + i });
            var client = new Client(); await using var engine = f.Engine(client); engine.ResumeRecoveredOperations(); await Idle(engine);
            var batch = f.Journal.ReadRetrieval(f.Request.OperationId)!.Batches.Single();
            Check(batch.Status == "more_matches" && batch.Passages.Count == 8);
        }
        using (var f = new Fixture())
        {
            var client = new Client { RequestedSegment = f.Journal.Transcript.Single(s => s.EndMs == 3000).Id };
            await using var engine = f.Engine(client); engine.ResumeRecoveredOperations(); await Idle(engine);
            Check(f.Journal.ReadRetrieval(f.Request.OperationId)!.Batches.Single().Passages.Count == 0);
            Check(client.Prompts.All(p => !p.Contains("FUTURE-MUST-NOT-LEAK")));
        }
    }
    public static async Task InvalidModelData()
    {
        using (var f = new Fixture())
        {
            var client = new Client { WrongOperation = true };
            await using var engine = f.Engine(client); engine.ResumeRecoveredOperations(); await Idle(engine);
            Check(client.Calls == 1 && f.Journal.Read().State.ProcessedThroughMs == 0 && f.Journal.Read().Messages.Count == 0);
            Check(f.Journal.ReadRetrieval(f.Request.OperationId)!.Batches.Count == 0);
        }
        using (var f = new Fixture())
        {
            var client = new Client { InventCitation = true };
            await using var engine = f.Engine(client); engine.ResumeRecoveredOperations(); await Idle(engine);
            Check(client.Calls == 2 && f.Journal.Read().State.ProcessedThroughMs == 0 && f.Journal.Read().Messages.Count == 0);
            Check(engine.Snapshot().Error?.Contains("сегмент") == true);
        }
    }

    private sealed class FreshBackend : IModelBackend
    {
        public ProcessingRequest? Request;
        public Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new ProcessingResult(new(request.BoundaryMs, [], [], []), "Новый ответ"));
        }
    }
    public static async Task AbandonInvalidResponse()
    {
        for (int invalid = 0; invalid < 4; invalid++)
        {
            using var f = new Fixture();
            var client = new Client { Malformed = invalid == 0, WrongOperation = invalid == 1, InventCitation = invalid == 2, AlwaysRetrieve = invalid == 3 };
            byte[] original = File.ReadAllBytes(Path.Combine(f.DirectoryPath, "transcript.jsonl"));
            await using (var engine = f.Engine(client))
            {
                engine.ResumeRecoveredOperations(); await Idle(engine);
                Check(engine.Snapshot().Error is not null && (invalid == 2
                    ? f.Journal.ModelOperations.Single().Phase == ModelOperationPhase.ValidationRejected && !engine.Snapshot().CanRetry
                    : engine.Snapshot().CanAbandonFailedRequest));
                int calls = client.Calls;
                engine.Retry(); await Idle(engine);
                Check(client.Calls == calls, "Повтор сохранённого неверного ответа вызвал модель");
                string sidecar = File.ReadAllText(Path.Combine(f.DirectoryPath, "retrieval", f.Request.OperationId.ToString("N") + ".json"));
                if (invalid != 2) engine.AbandonFailedRequest();
                Check(!engine.Snapshot().CanRetry && !engine.Snapshot().CanAbandonFailedRequest);
                Check(f.Journal.ModelOperations.Single().Phase == (invalid == 2 ? ModelOperationPhase.ValidationRejected : ModelOperationPhase.Abandoned));
                Check(f.Journal.Read().State.ProcessedThroughMs == 0 && f.Journal.Read().Messages.Count == 0);
                Check(sidecar == File.ReadAllText(Path.Combine(f.DirectoryPath, "retrieval", f.Request.OperationId.ToString("N") + ".json")));
            }
            f.Reopen();
            var backend = new FreshBackend();
            await using var restored = new ConversationEngine(new Clock(), new Source(f.Journal.Transcript), backend, f.Journal);
            restored.ResumeRecoveredOperations(); await Idle(restored);
            Check(backend.Request is null, "Отменённый запрос восстановился автоматически");
            Check(restored.Answer("Уточнённая инструкция")); await Idle(restored);
            Check(backend.Request?.OperationId != f.Request.OperationId && backend.Request?.BoundaryMs == 3000);
            Check(backend.Request?.Segments.Count == 3 && backend.Request.UserInstruction == "Уточнённая инструкция");
            Check(f.Journal.Read().Messages.Single().Text == "Новый ответ");
            Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(f.DirectoryPath, "transcript.jsonl"))));
        }
    }
    public static async Task AbandonPersistenceAndBarrier()
    {
        foreach (bool after in new[] { false, true })
        {
            using var f = new Fixture(); var client = new Client { Malformed = true };
            var store = new FailureStore(f.Journal) { FailAdvance = p => p == ModelOperationPhase.Abandoned, FailAfterAdvance = after };
            await using (var engine = f.Engine(client, store))
            {
                engine.ResumeRecoveredOperations(); await Idle(engine);
                try { engine.AbandonFailedRequest(); throw new Exception("Ожидался отказ записи отмены"); }
                catch (IOException) { }
                Check(engine.Snapshot().CanAbandonFailedRequest);
                Check(f.Journal.ModelOperations.Single().Phase == (after ? ModelOperationPhase.Abandoned : ModelOperationPhase.Prepared));
                if (after)
                {
                    engine.Retry(); await Idle(engine);
                    Check(client.Calls == 1 && !engine.Snapshot().CanRetry);
                }
                else engine.AbandonFailedRequest();
            }
            f.Reopen();
            await using var restored = f.Engine(client);
            restored.ResumeRecoveredOperations(); await Idle(restored);
            Check(client.Calls == 1 && f.Journal.Read().Messages.Count == 0 && f.Journal.Read().State.ProcessedThroughMs == 0);
            bool rejected = false;
            try { f.Journal.AdvanceModelOperation(f.Request.OperationId, ModelOperationPhase.Prepared); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Отменённую операцию удалось возобновить");
        }
        using (var f = new Fixture())
        {
            await using var engine = f.Engine(new Client { LoseResponse = true });
            engine.ResumeRecoveredOperations(); await Idle(engine);
            Check(!engine.Snapshot().CanAbandonFailedRequest);
            bool rejected = false;
            try { engine.AbandonFailedRequest(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && f.Journal.ModelOperations.Single().Phase == ModelOperationPhase.ReconciliationRequired);
        }
        using (var f = new Fixture())
        {
            await using var engine = f.Engine(new Client(), new(f.Journal) { FailCommit = true });
            engine.ResumeRecoveredOperations(); await Idle(engine);
            Check(engine.Snapshot().CanRetry && !engine.Snapshot().CanAbandonFailedRequest,
                "Готовый результат должен сохраняться, а не отменяться как неверный ответ");
        }
    }
}
