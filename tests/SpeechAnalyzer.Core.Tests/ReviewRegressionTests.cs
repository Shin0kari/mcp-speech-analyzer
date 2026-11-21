using SpeechAnalyzer.Core;

// Сценарии с управляемыми границами/отказами. Сеть, Codex и аудиоустройства не используются.
internal static class ReviewRegressionTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), "speech-review-" + Guid.NewGuid().ToString("N"));
    private static async Task Idle(ConversationEngine engine) => await engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));

    public static async Task AcceptedBeforeFinalization()
    {
        string directory = TempDirectory();
        try
        {
            var clock = new Clock(); var source = new Source { Pending = true };
            source.Items[0] = source.Items[0] with { IsFinal = false };
            var journal = new SessionJournal(directory); var backend = new Backend();
            var versions = new ModelOperationVersions("context-v1", "attachments-v1", "settings-v1", "high");
            await using (var engine = new ConversationEngine(clock, source, backend, journal, () => versions))
            {
                engine.Configure("Принятый контекст", "Принятая инструкция"); engine.Start();
                Check(engine.Answer("Вопрос пользователя"), "Запрос не принят");
                await source.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var durable = new SessionJournal(directory).ModelOperations.Single();
                Check(durable.Phase == ModelOperationPhase.Accepted && durable.Request.BoundaryMs == 10,
                    "Граница не сохранена до ожидания STT");
                Check(durable.Request.UserInstruction == "Вопрос пользователя" && durable.Versions == versions,
                    "Принятая инструкция или версии потеряны");
                Check(backend.Calls == 0, "Модель вызвана до финализации");
            }
            source = new Source(); // Те же сегменты возвращаются после финализации, без более поздней речи.
            var saved = new SessionJournal(directory).ModelOperations.Single();
            source.Items = saved.Request.Segments.Select(s => s with { IsFinal = true, Text = "Окончательный текст" }).ToList();
            source.Items.Add(new(Guid.NewGuid(), AudioChannel.You, 10, 20, clock.UtcNow, "Поздняя речь", true));
            journal = new(directory);
            await using var recovered = new ConversationEngine(clock, source, backend, journal);
            recovered.Configure("Другой контекст", "Другая инструкция");
            recovered.ResumeRecoveredOperations(); await Idle(recovered);
            Check(journal.ModelOperations.Single().Phase == ModelOperationPhase.Completed, "Accepted не восстановлена");
            var request = backend.Requests.Single();
            Check(request.OperationId == saved.OperationId && request.BoundaryMs == 10 && request.Context == "Принятый контекст"
                && request.UserInstruction == "Вопрос пользователя" && request.Segments.Single().Text == "Окончательный текст",
                "Восстановление изменило снимок или включило позднюю речь");
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task AcceptanceFailureAndMissingStt()
    {
        var clock = new Clock(); var source = new Source(); var backend = new Backend();
        var store = new Store { FailAccepted = true };
        await using (var engine = new ConversationEngine(clock, source, backend, store))
        {
            engine.Start();
            try { engine.Answer("Не очищать поле ввода"); throw new Exception("Принят несохранённый запрос"); }
            catch (IOException) { }
            Check(engine.Snapshot().PendingOperations == 0 && backend.Calls == 0, "Сбой приёма запустил обработку");
        }
        var pending = source.Items[0] with { IsFinal = false };
        var request = new ProcessingRequest(Guid.NewGuid(), OperationKind.Answer, 10, ConversationState.Empty, [pending], "Сохранить вопрос", "", "");
        store.FailAccepted = false;
        store.PrepareModelOperation(new(request.OperationId, request, new("", "", "", null), ModelOperationPhase.Accepted, null, null, clock.UtcNow));
        source.Items = [];
        await using var recovered = new ConversationEngine(clock, source, backend, store);
        recovered.ResumeRecoveredOperations(); await Idle(recovered);
        Check(backend.Calls == 0 && recovered.Snapshot().CanRetry && recovered.Snapshot().Error?.Contains("расшифровка утрачена") == true,
            "Утраченная pending-транскрипция выдана за тишину");
        Check(store.ModelOperations.Single().Request.UserInstruction == "Сохранить вопрос", "Принятый запрос утрачен");
    }

    public static async Task EmptySttDuringAcceptance()
    {
        var clock = new Clock(); var source = new Source { DropPendingOnSecondRead = true };
        source.Items[0] = source.Items[0] with { IsFinal = false };
        var backend = new Backend(); var store = new Store();
        await using var engine = new ConversationEngine(clock, source, backend, store);
        engine.Start(); engine.Answer(); await Idle(engine);
        Check(backend.Calls == 1 && backend.Requests.Single().Segments.Count == 0 && engine.Snapshot().Error is null,
            "Нормальный пустой результат STT ошибочно признан утратой при перезапуске");
        Check(store.ModelOperations.Single().Phase == ModelOperationPhase.Completed, "Пустой результат STT заблокировал запрос");
    }

    public static async Task QueuedAcceptanceRecovery()
    {
        string directory = TempDirectory();
        try
        {
            var clock = new Clock(); var source = new Source(); var backend = new Backend { BlockFirst = true };
            var journal = new SessionJournal(directory);
            await using (var engine = new ConversationEngine(clock, source, backend, journal))
            {
                engine.Start(); engine.Answer("Первый"); await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(engine.Answer("Второй"), "Ожидающий запрос не принят");
                Check(new SessionJournal(directory).ModelOperations.Count == 2, "Ожидающий запрос не сохранён");
            }
            backend = new(); journal = new(directory);
            await using var recovered = new ConversationEngine(clock, source, backend, journal);
            recovered.ResumeRecoveredOperations(); await Idle(recovered);
            Check(backend.Requests.Select(r => r.UserInstruction).SequenceEqual(new[] { "Первый", "Второй" }),
                "Восстановление потеряло или переставило операции");
            Check(journal.Read().Messages.Count == 2 && journal.ModelOperations.All(o => o.Phase == ModelOperationPhase.Completed),
                "Восстановление очереди не завершено");
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task UncertainBarrier()
    {
        string directory = TempDirectory();
        try
        {
            var clock = new Clock(); var source = new Source(); var backend = new DurableBackend { FailAfterSend = true };
            var journal = new SessionJournal(directory);
            await using (var engine = new ConversationEngine(clock, source, backend, journal))
            {
                engine.Start(); engine.Answer(); await Idle(engine);
                for (int i = 0; i < 3; i++) { clock.Now += TimeSpan.FromSeconds(60); engine.Pulse(); engine.Retry(); }
                Check(!engine.Answer("Не повторять неизвестный ход"), "Новый запрос обошёл сверку");
                engine.SetMode(AnalysisMode.Automatic); clock.Now += TimeSpan.FromSeconds(5); engine.Pulse(); await Idle(engine);
                Check(backend.Calls == 1 && journal.ModelOperations.Count == 1, "Таймер повторно отправил неизвестный ход");
            }
            backend = new();
            await using var recovered = new ConversationEngine(clock, source, backend, new SessionJournal(directory));
            recovered.Start(); recovered.ResumeRecoveredOperations(); clock.Now += TimeSpan.FromSeconds(60); recovered.Pulse();
            Check(!recovered.Answer() && recovered.Snapshot().Error is not null, "Барьер сверки утрачен после перезапуска");
            await Idle(recovered); Check(backend.Calls == 0, "Неизвестный ход повторён после восстановления");
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task PersistentJournalFailure()
    {
        var clock = new Clock(); var source = new Source(); var backend = new DurableBackend();
        var store = new Store { FailRemoteAndReconciliation = true };
        await using (var engine = new ConversationEngine(clock, source, backend, store))
        {
            engine.Start(); engine.Answer(); await Idle(engine);
            var state = engine.Snapshot();
            Check(!state.IsBusy && !state.CanRetry && state.Error?.Contains("Фаза сверки не сохранена") == true,
                "Устойчивый отказ журнала оставил worker зависшим или скрыл ошибку");
            clock.Now += TimeSpan.FromSeconds(60); engine.Pulse(); Check(!engine.Answer(), "Отказ диска обошёл сверку");
            Check(backend.Calls == 1, "Неизвестный ход повторён");
        }
        await using var recovered = new ConversationEngine(clock, source, backend, store);
        recovered.ResumeRecoveredOperations(); await Idle(recovered);
        Check(!recovered.Snapshot().IsBusy && recovered.Snapshot().Error is not null && !recovered.Answer(),
            "Восстановление при отказе записи сверки небезопасно");
    }

    public static async Task UnlabelledLateSpeech()
    {
        var clock = new Clock(); var source = new Source(); var backend = new Backend { BlockFirst = true };
        var store = new Store();
        await using var engine = new ConversationEngine(clock, source, backend, store);
        engine.SetMode(AnalysisMode.Automatic); engine.Start(); clock.Now += TimeSpan.FromSeconds(5); engine.Pulse();
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        source.Items.Add(new(Guid.NewGuid(), AudioChannel.You, 10, 20, clock.UtcNow, "Уже договорились: завтра. Вопрос закрыт.", true));
        backend.Release.TrySetResult(); await Idle(engine);
        Check(store.Read().Messages.Count == 0 && store.Read().State.Questions.Single().Status == QuestionStatus.Open,
            "Неразмеченная поздняя речь не отложила показ");
        Check(store.ModelOperations.Single().Result?.DisplayText is not null, "Исходный результат не сохранён отдельно от решения о показе");
        clock.Now += TimeSpan.FromSeconds(5); engine.Pulse(); await Idle(engine);
        Check(backend.Calls == 2 && backend.Requests[1].Segments.Single().EndMs == 20
            && store.Read().State.Questions.Single().Status == QuestionStatus.AnsweredByParticipant && store.Read().Messages.Count == 0,
            "Следующий анализ не обработал позднее закрытие");
    }

    public static async Task AutomaticCommitRetryAndRecovery()
    {
        foreach (bool restart in new[] { false, true })
        {
            var clock = new Clock(); var source = new Source(); var backend = new Backend();
            var store = new Store { FailCommitOnce = true };
            var engine = new ConversationEngine(clock, source, backend, store);
            engine.SetMode(AnalysisMode.Automatic); engine.Start(); clock.Now += TimeSpan.FromSeconds(5); engine.Pulse(); await Idle(engine);
            Check(engine.Snapshot().CanRetry && store.ModelOperations.Single().Phase == ModelOperationPhase.ResultReceived, "Не достигнут отказ commit");
            source.Items.Add(new(Guid.NewGuid(), AudioChannel.You, 10, 20, clock.UtcNow, "Ответ уже найден", true));
            if (restart) { await engine.DisposeAsync(); engine = new(clock, source, backend, store); engine.ResumeRecoveredOperations(); }
            else engine.Retry();
            await Idle(engine);
            Check(backend.Calls == 1 && store.Read().Messages.Count == 0 && store.ModelOperations.Single().Phase == ModelOperationPhase.Completed,
                "Повтор commit/восстановление показал устаревший ответ или повторил модель");
            await engine.DisposeAsync();
        }
    }

    private sealed class Clock : IClock
    {
        public TimeSpan Now { get; set; }
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Now;
    }
    private sealed class Source : ITranscriptSource
    {
        public bool Pending;
        public bool DropPendingOnSecondRead;
        private int reads;
        public TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<TranscriptSegment> Items = [new(Guid.NewGuid(), AudioChannel.Other, 0, 10, DateTimeOffset.UnixEpoch, "Когда встречаемся?", true)];
        public CaptureStatus Status { get; private set; }
        public long CaptureBoundaryMs => Items.Select(s => s.EndMs).DefaultIfEmpty(0).Max();
        public long FinalizedThroughMs => Items.Where(s => !s.IsFinal).Select(s => s.StartMs).DefaultIfEmpty(CaptureBoundaryMs).Min();
        public IReadOnlyList<TranscriptSegment> Segments
        {
            get
            {
                if (DropPendingOnSecondRead && Interlocked.Increment(ref reads) == 2) Items.Clear();
                return Items.ToArray();
            }
        }
        public void Start() => Status = CaptureStatus.Capturing;
        public void Pause() => Status = CaptureStatus.Paused;
        public void Resume() => Start();
        public void Stop() => Status = CaptureStatus.Stopped;
        public void Advance() { }
        public long SnapshotBoundary() => CaptureBoundaryMs;
        public async Task WaitForFinalizationAsync(long boundaryMs, CancellationToken token)
        { Waiting.TrySetResult(); if (Pending) await Task.Delay(Timeout.Infinite, token); }
    }
    private sealed class Store : IConversationStore, IModelOperationStore
    {
        public bool FailRemoteAndReconciliation, FailAccepted, FailCommitOnce;
        private readonly MemoryConversationStore inner = new();
        private readonly Dictionary<Guid, DurableModelOperation> operations = [];
        public StoreSnapshot Read() => inner.Read();
        public Task CommitAsync(ProcessingCommit commit, CancellationToken token)
        {
            if (FailCommitOnce) { FailCommitOnce = false; throw new IOException("Тестовый отказ commit"); }
            return inner.CommitAsync(commit, token);
        }
        public IReadOnlyList<DurableModelOperation> ModelOperations => operations.Values.ToArray();
        public void PrepareModelOperation(DurableModelOperation operation)
        {
            if (FailAccepted && operation.Phase == ModelOperationPhase.Accepted) throw new IOException("Тестовый отказ приёма");
            operations[operation.OperationId] = operation;
        }
        public void AdvanceModelOperation(Guid id, ModelOperationPhase phase, string? remoteOperationId = null, ProcessingResult? result = null)
        {
            if (FailRemoteAndReconciliation && phase is ModelOperationPhase.RemoteAccepted or ModelOperationPhase.ReconciliationRequired)
                throw new IOException("Тестовый устойчивый отказ диска");
            operations[id] = operations[id] with { Phase = phase, Result = result ?? operations[id].Result };
        }
    }
    private sealed class Backend : IModelBackend
    {
        public bool BlockFirst;
        public int Calls;
        public List<ProcessingRequest> Requests = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken token)
        {
            Calls++; Requests.Add(request); Entered.TrySetResult();
            if (BlockFirst && Calls == 1) await Release.Task.WaitAsync(token);
            bool closed = request.Segments.Any(s => s.EndMs > 10);
            bool display = request.Kind != OperationKind.Background && !closed;
            return new(new(request.BoundaryMs, [], [], [new("date", "Когда встречаемся?", closed ? QuestionStatus.AnsweredByParticipant : display ? QuestionStatus.Displayed : QuestionStatus.Open)]),
                display ? "Предлагаю завтра." : null, [], [], display ? ["date"] : []);
        }
    }
    private sealed class DurableBackend : IDurableModelBackend
    {
        public bool FailAfterSend;
        public int Calls;
        public Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken token) => throw new NotImplementedException();
        public async Task<ProcessingResult> ProcessDurableAsync(ProcessingRequest request, ModelOperationVersions versions, Func<ModelOperationPhase, string?, Task> persist, CancellationToken token)
        {
            Calls++;
            await persist(ModelOperationPhase.RequestSentUnknown, null);
            if (FailAfterSend) throw new IOException("Тестовый разрыв соединения");
            await persist(ModelOperationPhase.RemoteAccepted, "turn-test");
            return new(request.Previous with { ProcessedThroughMs = request.BoundaryMs }, null);
        }
    }
}
