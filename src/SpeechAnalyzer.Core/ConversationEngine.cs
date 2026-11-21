namespace SpeechAnalyzer.Core;

// Один worker меняет состояние; входящие события занимают не более двух слотов.
// Источник аудио, модель и хранилище выполняют долгие операции вне gate.
public sealed class ConversationEngine : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IClock clock;
    private readonly ITranscriptSource source;
    private readonly IModelBackend model;
    private readonly IConversationStore store;
    private readonly string modelName;
    private readonly Func<ModelOperationVersions> operationVersions;
    private readonly CancellationTokenSource lifetime = new();
    private AnalysisMode mode;
    private TimeSpan? nextAnalysis;
    private readonly RollingContextConfiguration? rollingContext;
    private long cycleOriginMs, evaluatedTargetMs;
    public int? RollingContextSeconds => rollingContext?.Seconds;
    private Work? pendingAnswer;
    private Work? activeWork;
    private Work? pendingBackground;
    private Work? failed;
    private readonly Queue<Work> recovered = new();
    private Guid? activeArchiveOperation;
    private TranscriptQueries? activeArchive;
    private Task? worker;
    private bool disposed;
    private bool reconciliationRequired;
    private bool recoveryAwaitingUser;
    private string activity = "Ожидание старта";
    private string? error;
    private string context = "";
    private string fallback = "Кратко подведи итоги разговора и перечисли следующие действия.";
    public event Action? Changed;

    private sealed class Work
    {
        public Guid Id { get; }
        public OperationKind Kind { get; }
        public long Boundary;
        public string Instruction { get; }
        public string Fallback { get; }
        public string Context { get; }
        public ModelOperationVersions Versions { get; }
        public int? RollingContextSeconds;
        public bool DurablePrepared;
        public bool RestoredAwaitingFinalization;
        public IReadOnlyList<Guid> RequiredFinalSegmentIds = [];
        public ProcessingRequest? Request;
        public ProcessingResult? Result;
        public ChatMessage? Message;
        public Work(OperationKind kind, long boundary, string instruction, string fallback, string context, ModelOperationVersions versions)
            => (Id, Kind, Boundary, Instruction, Fallback, Context, Versions) = (Guid.NewGuid(), kind, boundary, instruction, fallback, context, versions);
        public Work(DurableModelOperation operation)
        {
            Id = operation.OperationId; Kind = operation.Request.Kind; Boundary = operation.Request.BoundaryMs;
            Instruction = operation.Request.UserInstruction; Fallback = operation.Request.FallbackInstruction;
            Context = operation.Request.Context; Versions = operation.Versions;
            RollingContextSeconds = operation.Request.RollingContextSeconds;
            Request = operation.Phase == ModelOperationPhase.Accepted ? null : operation.Request;
            RestoredAwaitingFinalization = operation.Phase == ModelOperationPhase.Accepted;
            RequiredFinalSegmentIds = operation.Request.Segments.Where(segment => !segment.IsFinal).Select(segment => segment.Id).ToArray();
            Result = operation.Result; DurablePrepared = operation.Phase != ModelOperationPhase.Accepted;
        }
    }

    public ConversationEngine(IClock clock, ITranscriptSource source, IModelBackend model, IConversationStore store,
        Func<ModelOperationVersions>? operationVersions = null, string modelName = "имитатор",
        RollingContextConfiguration? rollingContext = null, bool deferRecoveredOperations = false)
    {
        (this.clock, this.source, this.model, this.store, this.modelName) = (clock, source, model, store, modelName);
        this.operationVersions = operationVersions ?? (() => new(OperationVersionHash.Of(context), OperationVersionHash.Of(""),
            OperationVersionHash.Of(mode.ToString()), null));
        this.rollingContext = rollingContext;
        if (rollingContext is not null)
        {
            if (store is IModelOperationStore operations)
            {
                var accepted = operations.ModelOperations.Where(operation => operation.Request.RollingContextSeconds.HasValue).ToArray();
                if (accepted.Any(operation => operation.Request.RollingContextSeconds != rollingContext.Seconds))
                    throw new IOException("Rolling context configuration differs from the saved model operations.");
                cycleOriginMs = accepted.Where(operation => operation.Request.Kind == OperationKind.Answer)
                    .Select(operation => operation.Request.BoundaryMs).DefaultIfEmpty().Max();
            }
            evaluatedTargetMs = cycleOriginMs;
            SessionEventLog.Current?.Write("rolling_context_configured", new { version = RollingContextConfiguration.ScheduleVersion, seconds = rollingContext.Seconds, cycleOriginMs });
        }
        RecoverDurableOperations();
        recoveryAwaitingUser = deferRecoveredOperations && recovered.Count > 0;
        if (recoveryAwaitingUser)
            error ??= "Сохранённая операция ожидает явного повтора. Открытие разговора не отправляет запросы.";
    }

    private void RecoverDurableOperations()
    {
        if (store is not IModelOperationStore operationStore) return;
        foreach (var operation in operationStore.ModelOperations)
        {
            if (operation.Phase is ModelOperationPhase.RequestSentUnknown or ModelOperationPhase.RemoteAccepted or ModelOperationPhase.ReconciliationRequired)
            {
                reconciliationRequired = true;
                error ??= "Есть модельная операция с неопределённым удалённым исходом; требуется сверка перед повтором.";
                if (operation.Phase != ModelOperationPhase.ReconciliationRequired)
                    try { operationStore.AdvanceModelOperation(operation.OperationId, ModelOperationPhase.ReconciliationRequired); }
                    catch (Exception failure) { error += " Фаза сверки не сохранена: " + failure.Message; }
                continue;
            }
            if (operation.Phase is not (ModelOperationPhase.Accepted or ModelOperationPhase.Prepared or ModelOperationPhase.ResultReceived)) continue;
            var work = new Work(operation);
            recovered.Enqueue(work);
        }
    }

    public void ResumeRecoveredOperations()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (recovered.Count > 0 || pendingAnswer is not null || pendingBackground is not null) EnsureWorker();
        }
        Notify();
    }

    private bool IsRollingManual => rollingContext is not null && mode == AnalysisMode.Manual;
    private TimeSpan Interval => TimeSpan.FromSeconds(mode == AnalysisMode.Manual ? 60 : 5);

    public void Configure(string userContext, string fallbackInstruction)
    {
        lock (gate) { context = userContext; fallback = fallbackInstruction; }
    }

    public void SetMode(AnalysisMode value)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            mode = value;
            if (pendingBackground is { DurablePrepared: false }) pendingBackground = null;
            nextAnalysis = source.Status == CaptureStatus.Capturing && !IsRollingManual ? clock.Now + Interval : null;
        }
        Notify();
    }

    public void Start()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (source.Status != CaptureStatus.Stopped) return;
            source.Start();
            nextAnalysis = IsRollingManual ? null : clock.Now + Interval;
            if (worker is null && failed is null) activity = "Захват идёт";
        }
        Notify();
    }

    public void TogglePause()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (source.Status == CaptureStatus.Capturing) { source.Pause(); nextAnalysis = null; }
            else if (source.Status == CaptureStatus.Paused) { source.Resume(); nextAnalysis = IsRollingManual ? null : clock.Now + Interval; }
            if (worker is null && failed is null) activity = source.Status == CaptureStatus.Paused ? "Пауза" : "Захват идёт";
        }
        Notify();
    }

    public void Stop()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            source.Stop();
            nextAnalysis = null;
            pendingBackground = null;
            if (worker is null && failed is null) activity = "Захват остановлен";
        }
        Notify();
    }

    // Presentation pulse — только будит движок. Сроки и политика таймеров принадлежат движку.
    public void Pulse()
    {
        lock (gate)
        {
            if (disposed) return;
            source.Advance();
            if (IsRollingManual) QueueRollingBackground();
            else if (!reconciliationRequired && !recoveryAwaitingUser && failed is null &&
                source.Status == CaptureStatus.Capturing && nextAnalysis is { } due && clock.Now >= due)
            {
                nextAnalysis = clock.Now + Interval;
                long boundary = (source as BufferedTranscriptSource)?.EvidenceThroughMs ?? source.FinalizedThroughMs;
                bool hasNewSpeech = source.Segments.Any(s => s.IsFinal && NewEvidence(s, store.Read().State) && s.EndMs <= boundary);
                if (hasNewSpeech)
                    pendingBackground = new(mode == AnalysisMode.Manual ? OperationKind.Background : OperationKind.Automatic,
                        boundary, "", fallback, context, operationVersions());
                if (pendingBackground is not null) EnsureWorker();
            }
        }
        Notify();
    }

    private void QueueRollingBackground()
    {
        if (reconciliationRequired || recoveryAwaitingUser || failed is not null || source.Status != CaptureStatus.Capturing) return;
        long unit = rollingContext!.UnitMs;
        long elapsed = source.CaptureBoundaryMs - cycleOriginMs;
        long evidence = (source as BufferedTranscriptSource)?.EvidenceThroughMs ?? source.FinalizedThroughMs;
        long releasedUnits = Math.Min(elapsed / unit - (RollingContextConfiguration.FirstEligibilityUnits - 1),
            (evidence - cycleOriginMs) / unit);
        if (releasedUnits <= 0) return;
        long target = cycleOriginMs + releasedUnits * unit;
        if (target <= evaluatedTargetMs) return;

        var previous = store.Read().State;
        if (source.Segments.Any(segment => segment.IsFinal && segment.EndMs <= target && NewEvidence(segment, previous)))
        {
            pendingBackground = new(OperationKind.Background, target, "", fallback, context, operationVersions())
            { RollingContextSeconds = rollingContext.Seconds };
            EnsureWorker();
        }
        evaluatedTargetMs = target;
    }

    private TimeSpan? TimeUntilAnalysis()
    {
        if (IsRollingManual && source.Status == CaptureStatus.Capturing)
        {
            long unit = rollingContext!.UnitMs;
            long elapsed = source.CaptureBoundaryMs - cycleOriginMs;
            long nextTargetMs = evaluatedTargetMs + RollingContextConfiguration.FirstEligibilityUnits * unit - cycleOriginMs;
            return TimeSpan.FromMilliseconds(Math.Max(0, nextTargetMs - elapsed));
        }
        return nextAnalysis is { } due ? TimeSpan.FromTicks(Math.Max(0, (due - clock.Now).Ticks)) : null;
    }

    // false оставляет набранный запрос в UI, чтобы пользователь не потерял текст.
    public bool Answer(string instruction = "")
    {
        bool accepted;
        lock (gate)
        {
            ThrowIfDisposed();
            if (reconciliationRequired || recovered.Count > 0 || failed is not null) return false;
            if (rollingContext is not null && pendingAnswer is { } pending)
                return instruction.Length == 0 || pending.Instruction == instruction;
            if (rollingContext is null) source.Advance();
            long boundary = source.SnapshotBoundary();
            FunctionalRunTrace.Write("answer_cutoff", boundaryMs: boundary);
            if (rollingContext is null)
            {
                if (source.Status == CaptureStatus.Capturing) nextAnalysis = clock.Now + Interval;
                pendingBackground = null;
            }
            if (activeWork is { Kind: OperationKind.Answer } active && active.Boundary == boundary
                && active.Instruction == instruction && active.Context == context && active.Fallback == fallback)
                accepted = true;
            else if (pendingAnswer is not null && instruction.Length > 0 && pendingAnswer.Instruction != instruction)
                accepted = false;
            else
            {
                if (pendingAnswer is null)
                {
                    var acceptedWork = new Work(OperationKind.Answer, boundary, instruction, fallback, context, operationVersions())
                    { RollingContextSeconds = rollingContext?.Seconds };
                    if (store is IModelOperationStore operationStore)
                    {
                        var previous = store.Read().State;
                        var segments = source.Segments.Where(segment => NewEvidence(segment, previous) && segment.StartMs < boundary).ToArray();
                        var acceptedRequest = new ProcessingRequest(acceptedWork.Id, OperationKind.Answer, boundary,
                            previous, segments, instruction, fallback, context) { RollingContextSeconds = acceptedWork.RollingContextSeconds };
                        // Подтверждаем приём лишь после сохранения инструкции и границы, до ожидания STT/модели.
                        operationStore.PrepareModelOperation(new(acceptedWork.Id, acceptedRequest, acceptedWork.Versions,
                            ModelOperationPhase.Accepted, null, null, clock.UtcNow));
                        acceptedWork.RequiredFinalSegmentIds = segments.Where(segment => !segment.IsFinal).Select(segment => segment.Id).ToArray();
                    }
                    FunctionalRunTrace.Write("answer_accepted", acceptedWork.Id, boundary);
                    pendingAnswer = acceptedWork;
                    if (rollingContext is not null)
                    {
                        cycleOriginMs = evaluatedTargetMs = boundary;
                        pendingBackground = null;
                        if (source.Status == CaptureStatus.Capturing && !IsRollingManual) nextAnalysis = clock.Now + Interval;
                        SessionEventLog.Current?.Write("rolling_context_reset", new { version = RollingContextConfiguration.ScheduleVersion, seconds = rollingContext.Seconds, cycleOriginMs });
                    }
                }
                // Повторный клик объединяется с ожидающим запросом, не меняя принятую границу.
                EnsureWorker();
                accepted = true;
            }
        }
        Notify();
        return accepted;
    }

    public void Retry()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (reconciliationRequired || !recoveryAwaitingUser && failed is null) return;
            recoveryAwaitingUser = false;
            EnsureWorker(explicitRetry: true);
        }
        Notify();
    }

    private static bool NewEvidence(TranscriptSegment segment, ConversationState state)
    {
        if (state.ConsumedEvidence is not { } cursor) return segment.EndMs > state.ProcessedThroughMs;
        long end = segment.EndSample ?? segment.EndMs * 16;
        return end > (segment.Channel == AudioChannel.You ? cursor.YouTextEnd : cursor.OtherTextEnd);
    }

    private static EvidenceCursor Consume(ProcessingRequest request)
    {
        var previous = request.Previous.ConsumedEvidence;
        long Text(AudioChannel channel, long? last) => request.Segments.Where(s => s.Channel == channel)
            .Select(s => s.EndSample ?? s.EndMs * 16).Append(last ?? request.Previous.ProcessedThroughMs * 16).Max();
        long Gap(AudioChannel channel, long? last) => (request.CaptureGaps ?? []).Where(g => g.Channel == channel && g.Resume.HasValue)
            .Select(g => g.Number).Append(last ?? 0).Max();
        return new(Text(AudioChannel.You, previous?.YouTextEnd), Text(AudioChannel.Other, previous?.OtherTextEnd),
            Gap(AudioChannel.You, previous?.YouGap), Gap(AudioChannel.Other, previous?.OtherGap));
    }

    private bool CanAbandonFailedRequest()
        => worker is null && !reconciliationRequired && failed is { Result: null }
            && (store is not IModelOperationStore operations
                || operations.ModelOperations.FirstOrDefault(item => item.OperationId == failed.Id)?.Phase is ModelOperationPhase.Prepared or ModelOperationPhase.Abandoned);

    public void AbandonFailedRequest()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!CanAbandonFailedRequest())
                throw new InvalidOperationException("Отмена недоступна: дождитесь обработки, сохраните готовый результат или сначала сверьте неизвестный исход.");
            // Сначала долговечное решение. При отказе записи запрос остаётся доступным для повтора/отмены.
            if (store is IModelOperationStore operations)
                operations.AdvanceModelOperation(failed!.Id, ModelOperationPhase.Abandoned);
            failed = null;
            error = null;
            activity = "Неудачный запрос отменён; расшифровка сохранена. Можно отправить новый запрос.";
        }
        Notify();
    }

    public Task ReconcileAsync(Guid operationId, Func<DurableModelOperation, CancellationToken, Task<ProcessingResult>> readRemote)
        => ReconcileCoreAsync(operationId, async (operation, token) => await readRemote(operation, token).ConfigureAwait(false));
    public Task ReconcileIntermediateAsync(Guid operationId, Func<DurableModelOperation, CancellationToken, Task> readRemote)
        => ReconcileCoreAsync(operationId, async (operation, token) => { await readRemote(operation, token).ConfigureAwait(false); return null; });
    private Task ReconcileCoreAsync(Guid operationId, Func<DurableModelOperation, CancellationToken, Task<ProcessingResult?>> readRemote)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (worker is not null) throw new InvalidOperationException("Дождитесь завершения текущей операции.");
            if (store is not IModelOperationStore operations) throw new InvalidOperationException("У сессии нет долговечного журнала.");
            var operation = operations.ModelOperations.Single(item => item.OperationId == operationId);
            if (operation.Phase is not (ModelOperationPhase.ReconciliationRequired or ModelOperationPhase.RequestSentUnknown or ModelOperationPhase.RemoteAccepted))
                throw new InvalidOperationException("Операция не требует сверки.");
            worker = Task.Run(async () =>
            {
                try
                {
                    var result = await readRemote(operation, lifetime.Token).ConfigureAwait(false);
                    lifetime.Token.ThrowIfCancellationRequested();
                    bool rejected = false;
                    if (result is not null)
                        try { Validate(operation.Request, result); }
                        catch (Exception validationError) when (validationError is InvalidOperationException or InvalidDataException)
                        {
                            operations.AdvanceModelOperation(operationId, ModelOperationPhase.ValidationRejected, result: result);
                            rejected = true;
                        }
                    if (!rejected)
                        operations.AdvanceModelOperation(operationId, result is null ? ModelOperationPhase.Prepared : ModelOperationPhase.ResultReceived, result: result);
                    lock (gate)
                    {
                        var waiting = recovered.ToArray(); recovered.Clear();
                        if (!rejected) recovered.Enqueue(new(operations.ModelOperations.Single(item => item.OperationId == operationId)));
                        foreach (var item in waiting) recovered.Enqueue(item);
                        reconciliationRequired = operations.ModelOperations.Any(item => item.Phase is ModelOperationPhase.RequestSentUnknown
                            or ModelOperationPhase.RemoteAccepted or ModelOperationPhase.ReconciliationRequired);
                        error = reconciliationRequired ? "Другие операции ещё требуют сверки." : null;
                        worker = null;
                        if (!reconciliationRequired) EnsureWorker();
                    }
                }
                catch (Exception failure)
                {
                    lock (gate) { worker = null; reconciliationRequired = true; error = "Сверка не завершена: " + failure.Message; activity = "Нужна сверка результата модели"; }
                }
                Notify();
            });
            return worker;
        }
    }

    public TranscriptPage ReadModelOriginals(Guid operationId, TranscriptQuery query, int limit = 100, string? cursor = null)
    {
        lock (gate)
        {
            if (worker is null || activeArchiveOperation != operationId || activeArchive is null || store is not IModelOperationStore operations)
                throw new InvalidOperationException("Нет активного запроса модели с таким ID.");
            var page = activeArchive.Read(query, limit, cursor);
            // Оригиналы подтверждены на диске до того, как ответ инструмента увидит модель.
            operations.RecordSourceRead(operationId, page.Segments.Select(s => s.Id).ToArray());
            return page;
        }
    }

    // Вызывается только под gate. Task.Run не выполняет модельную логику на UI-потоке.
    private void EnsureWorker(bool explicitRetry = false)
    {
        if (!reconciliationRequired && !recoveryAwaitingUser && (failed is null || explicitRetry) && worker is null)
            worker = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            Work? work;
            lock (gate)
            {
                if (disposed || reconciliationRequired) { worker = null; return; }
                work = failed ?? (recovered.Count > 0 ? recovered.Dequeue() : pendingAnswer ?? pendingBackground);
                if (work is null)
                {
                    worker = null;
                    activity = source.Status switch { CaptureStatus.Capturing => "Захват идёт", CaptureStatus.Paused => "Пауза", _ => "Ожидание старта / остановлено" };
                }
                else
                {
                    if (ReferenceEquals(work, failed)) failed = null;
                    if (ReferenceEquals(work, pendingAnswer)) pendingAnswer = null;
                    if (ReferenceEquals(work, pendingBackground)) pendingBackground = null;
                    activeWork = work;
                    error = null;
                    activity = work.Kind == OperationKind.Answer ? "Ожидание финализации обоих каналов" : $"Фоновый анализ ({modelName})";
                }
            }
            Notify();
            if (work is null) return;
            FunctionalRunTrace.Write("operation_dequeued", work.Id, work.Boundary);
            try
            {
                // Решение отмены могло сохраниться перед потерей подтверждения записи.
                if (store is IModelOperationStore savedOperations && savedOperations.ModelOperations
                    .Any(item => item.OperationId == work.Id && item.Phase is ModelOperationPhase.Abandoned or ModelOperationPhase.ValidationRejected)) continue;
                if (work.Request is null)
                {
                    if (work.Kind == OperationKind.Answer)
                    {
                        if (work.RestoredAwaitingFinalization && work.RequiredFinalSegmentIds.Except(source.Segments.Select(segment => segment.Id)).Any())
                        {
                            if (source is BufferedTranscriptSource { DrainEnabled: true } restoredSource)
                            {
                                if (!restoredSource.HasDurableRestoredCoverage(work.Boundary))
                                    throw new SttCoverageException("Принятый запрос потерял незавершённый PCM при перезапуске.");
                                restoredSource.ValidateAnswerCoverage(work.Boundary);
                            }
                            else throw new IOException("Принятый запрос сохранён, но незавершённая расшифровка утрачена при перезапуске. Автоматическая подстановка тишины запрещена.");
                        }
                        await source.WaitForFinalizationAsync(work.Boundary, lifetime.Token).ConfigureAwait(false);
                        FunctionalRunTrace.Write("cutoff_text_ready", work.Id, work.Boundary);
                    }
                    var previous = store.Read().State;
                    var segments = source.Segments.Where(s => s.IsFinal && NewEvidence(s, previous) && s.EndMs <= work.Boundary)
                        .OrderBy(s => s.StartMs).ThenBy(s => s.Channel).ThenBy(s => s.Id).ToArray();
                    // Тик, накопившийся во время предыдущей операции, мог уже устареть.
                    if (work.Kind != OperationKind.Answer && segments.Length == 0) continue;
                    work.Request = new(work.Id, work.Kind, Math.Max(work.Boundary, previous.ProcessedThroughMs), previous,
                        segments, work.Instruction, work.Fallback, work.Context) { RollingContextSeconds = work.RollingContextSeconds };
                    if (source is BufferedTranscriptSource buffered)
                    {
                        var gaps = buffered.ReadEvidenceGaps(previous.ProcessedThroughMs, work.Boundary, previous.ConsumedEvidence);
                        if (gaps.Count > 0) work.Request = work.Request with { CaptureGaps = gaps };
                    }
                }
                var operationStore = store as IModelOperationStore;
                if (operationStore is not null && !work.DurablePrepared)
                {
                    operationStore.PrepareModelOperation(new(work.Id, work.Request, work.Versions, ModelOperationPhase.Prepared,
                        null, null, clock.UtcNow));
                    work.DurablePrepared = true;
                }
                lock (gate) activity = work.Result is null ? $"Обработка: {modelName}" : "Повтор сохранения результата";
                Notify();
                if (source is BufferedTranscriptSource { DrainEnabled: true } checkedSource) checkedSource.ValidateAnswerCoverage(work.Request.BoundaryMs);
                if (work.Result is null)
                {
                    var originals = source.Segments.Where(segment => segment.IsFinal && segment.EndMs <= work.Request.BoundaryMs).ToArray();
                    lock (gate)
                    {
                        activeArchiveOperation = work.Id;
                        activeArchive = new(() => originals, clock);
                    }
                    try
                    {
                        if (operationStore is not null && model is IDurableModelBackend durable)
                            work.Result = await durable.ProcessDurableAsync(work.Request, work.Versions,
                                (phase, remoteId) =>
                                {
                                    operationStore.AdvanceModelOperation(work.Id, phase, remoteId);
                                    return Task.CompletedTask;
                                }, lifetime.Token).ConfigureAwait(false);
                        else work.Result = await model.ProcessAsync(work.Request, lifetime.Token).ConfigureAwait(false);
                    }
                    catch (SavedFinalResultException rejection)
                    {
                        RejectResult(work, operationStore, rejection.Message);
                        continue;
                    }
                }
                if (source is BufferedTranscriptSource { GapContinuationEnabled: true })
                    work.Result = work.Result with { State = work.Result.State with { ConsumedEvidence = Consume(work.Request) } };
                lifetime.Token.ThrowIfCancellationRequested();
                FunctionalRunTrace.Write("result_validation_started", work.Id, work.Boundary);
                try { Validate(work.Request, work.Result); }
                catch (Exception validationError) when (validationError is InvalidOperationException or InvalidDataException)
                {
                    // A completed response is not a failed remote call. Close its local outcome
                    // durably before continuing accepted work with the unconsumed evidence.
                    // If this write fails, keep Result for local retry; never resend the turn.
                    RejectResult(work, operationStore, validationError.Message);
                    continue;
                }
                FunctionalRunTrace.Write("result_validated", work.Id, work.Boundary);
                if (operationStore is not null)
                {
                    var operation = operationStore.ModelOperations.Single(item => item.OperationId == work.Id);
                    if (operation.Phase != ModelOperationPhase.ResultReceived)
                        operationStore.AdvanceModelOperation(work.Id, ModelOperationPhase.ResultReceived, result: work.Result);
                }
                // Сохраняем исходный результат, а актуальность показа проверяем заново при каждом commit/восстановлении.
                var commitResult = work.Kind == OperationKind.Automatic ? RemoveResolvedAutomaticAnswer(work.Request, work.Result) : work.Result;
                string? publicationKey = UnresolvedPublicationKey(commitResult);
                if (work.Kind == OperationKind.Automatic && publicationKey is not null &&
                    store.Read().Messages.Any(message => message.OperationId != work.Id &&
                        (message.UnresolvedPublicationKey == publicationKey ||
                         publicationKey.EndsWith('/') && message.UnresolvedPublicationKey?.StartsWith(publicationKey, StringComparison.Ordinal) == true)))
                    commitResult = commitResult with { DisplayText = null };
                work.Message = commitResult.DisplayText is { Length: > 0 } text
                    ? new ChatMessage(work.Id, clock.UtcNow, WithLimitations(text, commitResult.Limitations),
                        work.Instruction.Length == 0 ? null : work.Instruction) { UnresolvedPublicationKey = publicationKey }
                    : null;
                if (source is BufferedTranscriptSource { DrainEnabled: true } committingSource) committingSource.ValidateAnswerCoverage(work.Request.BoundaryMs);
                FunctionalRunTrace.Write("result_commit_started", work.Id, work.Boundary);
                await store.CommitAsync(new(work.Id, commitResult, work.Message), lifetime.Token).ConfigureAwait(false);
                FunctionalRunTrace.Write("result_durably_committed", work.Id, work.Boundary);
                if (work.Message is not null) FunctionalRunTrace.Write("result_available_for_display", work.Id, work.Boundary);
                if (operationStore is not null && operationStore.ModelOperations.Single(item => item.OperationId == work.Id).Phase != ModelOperationPhase.Completed)
                    operationStore.AdvanceModelOperation(work.Id, ModelOperationPhase.Completed);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                lock (gate) { worker = null; activity = "Завершено"; }
                Notify();
                return;
            }
            catch (Exception exception)
            {
                var operationStore = store as IModelOperationStore;
                var operation = operationStore?.ModelOperations.FirstOrDefault(item => item.OperationId == work.Id);
                bool terminalCoverage = exception is SttCoverageException;
                if (terminalCoverage && operation?.Phase is ModelOperationPhase.Accepted or ModelOperationPhase.Prepared)
                    try { operationStore!.AdvanceModelOperation(work.Id, ModelOperationPhase.Abandoned); }
                    catch (Exception saveError) { exception = new IOException(exception.Message + " Не сохранён terminal отказ: " + saveError.Message, exception); }
                bool uncertain = operation?.Phase is ModelOperationPhase.RequestSentUnknown or ModelOperationPhase.RemoteAccepted
                    || exception is RetrievalOutcomeUncertainException;
                if (uncertain)
                {
                    // Барьер выставляется даже при устойчивом отказе диска; на диске остаётся неизвестная фаза.
                    lock (gate) reconciliationRequired = true;
                    try { operationStore!.AdvanceModelOperation(work.Id, ModelOperationPhase.ReconciliationRequired); }
                    catch (Exception failure) { exception = new IOException(exception.Message + " Фаза сверки не сохранена: " + failure.Message, exception); }
                    string reason = exception is CodexRequestException
                        ? "Повтор модельного запроса заблокирован до сверки. "
                        : "Удалённый исход модели неизвестен; повтор заблокирован до сверки. ";
                    exception = new IOException(reason + exception.Message, exception);
                }
                lock (gate)
                {
                    failed = uncertain || terminalCoverage ? null : work;
                    worker = null;
                    error = exception is IOException and not CodexRequestException && !uncertain && !terminalCoverage
                        ? "Локальная ошибка хранения или связи: " + exception.Message : exception.Message;
                    activity = terminalCoverage ? "Ответ завершён отказом: неполное покрытие" : uncertain ? "Нужна сверка результата модели" : "Ошибка — данные сохранены для повтора";
                }
                Notify();
                return;
            }
            finally { lock (gate) { if (ReferenceEquals(activeWork, work)) activeWork = null; } }
        }
    }

    private void RejectResult(Work work, IModelOperationStore? operationStore, string reason)
    {
        operationStore?.AdvanceModelOperation(work.Id, ModelOperationPhase.ValidationRejected, result: work.Result);
        FunctionalRunTrace.Write("result_rejected", work.Id, work.Boundary);
        lock (gate)
        {
            error = reason;
            activity = "Результат модели отклонён; расшифровка сохранена. Можно отправить новый запрос.";
        }
        Notify();
    }

    private void Validate(ProcessingRequest request, ProcessingResult result)
    {
        if (result.State.ProcessedThroughMs != request.BoundaryMs)
            throw new InvalidOperationException("Модель вернула неверную границу обработки.");
        if (request.Kind == OperationKind.Background && (result.DisplayText is not null
            || (result.DisplayedQuestionIds?.Count ?? 0) != 0
            || result.State.Questions.Any(question => question.Status == QuestionStatus.Displayed
                && !request.Previous.Questions.Any(previous => previous.Id == question.Id && previous.Status == QuestionStatus.Displayed))))
            throw new InvalidOperationException("Фоновый ручной анализ не должен создавать видимый ответ.");
        if (result.State.Questions.Select(q => q.Id).Distinct().Count() != result.State.Questions.Count)
            throw new InvalidOperationException("Модель вернула повторяющиеся ID вопросов.");
        var segmentIds = request.Segments.Select(segment => segment.Id).ToHashSet();
        if (store is IModelOperationStore operations)
            foreach (var id in operations.ModelOperations.FirstOrDefault(item => item.OperationId == request.OperationId)?.RetrievedSourceIds ?? [])
                segmentIds.Add(id);
        if ((result.SourceReferences ?? []).SelectMany(reference => reference.SourceSegmentIds).Any(id => !segmentIds.Contains(id)) ||
            (result.QuestionReferences ?? []).SelectMany(reference => reference.SourceSegmentIds).Any(id => !segmentIds.Contains(id)))
            throw new InvalidOperationException("Модель сослалась на сегмент, которого нет во входном срезе.");
        var questionIds = result.State.Questions.Select(question => question.Id).ToHashSet(StringComparer.Ordinal);
        if ((result.DisplayedQuestionIds ?? []).Any(id => !questionIds.Contains(id)))
            throw new InvalidOperationException("Модель пометила несуществующий вопрос как показанный.");
        InterpretationValidation.Validate(request, result, request.Segments.Concat(source.Segments
            .Where(s => segmentIds.Contains(s.Id) && !request.Segments.Any(input => input.Id == s.Id))).ToArray());
    }

    private ProcessingResult RemoveResolvedAutomaticAnswer(ProcessingRequest request, ProcessingResult result)
    {
        if (result.DisplayText is null) return result;
        var displayedIds = (result.DisplayedQuestionIds ?? []).Concat(result.State.Questions
            .Where(question => question.Status == QuestionStatus.Displayed
                && !request.Previous.Questions.Any(previous => previous.Id == question.Id && previous.Status == QuestionStatus.Displayed))
            .Select(question => question.Id)).ToHashSet(StringComparer.Ordinal);
        var later = source.Segments.Where(segment => segment.IsFinal && segment.EndMs > request.BoundaryMs).ToArray();
        var relevantIds = displayedIds.Concat(result.OpenQuestionIds ?? []).ToHashSet(StringComparer.Ordinal);
        var resolved = later.Where(segment => segment.QuestionId is not null && relevantIds.Contains(segment.QuestionId)
                && segment.Meaning is SegmentMeaning.ParticipantAnswer or SegmentMeaning.ObsoleteQuestion)
            .GroupBy(segment => segment.QuestionId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Meaning, StringComparer.Ordinal);
        // Реальный STT не размечает смысл и ID вопросов. При новом неразмеченном тексте откладываем
        // показ до следующего анализа остатка; не пытаемся угадать закрытие по ключевым словам.
        if (resolved.Count == 0 && !later.Any(segment => segment.QuestionId is null)) return result;
        var questions = result.State.Questions.Select(question => resolved.TryGetValue(question.Id, out var meaning)
            ? question with { Status = meaning == SegmentMeaning.ParticipantAnswer ? QuestionStatus.AnsweredByParticipant : QuestionStatus.Obsolete }
            : displayedIds.Contains(question.Id) ? question with { Status = QuestionStatus.Open } : question).ToArray();
        var refs = result.QuestionReferences?.Where(reference => !resolved.ContainsKey(reference.QuestionId)).ToArray();
        return result with { State = result.State with { Questions = questions }, DisplayText = null,
            QuestionReferences = refs, DisplayedQuestionIds = [] };
    }

    private static string WithLimitations(string text, string? limitations) =>
        string.IsNullOrWhiteSpace(limitations) || text.Contains(limitations, StringComparison.Ordinal)
            ? text : text + "\n\n" + limitations;

    private static string? UnresolvedPublicationKey(ProcessingResult result)
    {
        if (result.Outcome is not (ResultOutcome.PartialAnswer or ResultOutcome.Clarification) || result.DisplayText is null ||
            (result.DisplayedQuestionIds?.Count ?? 0) != 0)
            return null;
        var open = (result.OpenQuestionIds ?? []).Order(StringComparer.Ordinal).ToArray();
        var evidence = (result.QuestionReferences ?? []).Where(reference => open.Contains(reference.QuestionId))
            .SelectMany(reference => reference.SourceSegmentIds).Distinct().Order().ToArray();
        string topic = OperationVersionHash.Of(System.Text.Json.JsonSerializer.Serialize(new
        {
            result.Outcome, text = open.Length != 0 ? null :
                string.Join(' ', result.DisplayText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            open
        }));
        return topic + "/" + (evidence.Length == 0 ? "" :
            OperationVersionHash.Of(System.Text.Json.JsonSerializer.Serialize(evidence)));
    }

    public EngineSnapshot Snapshot()
    {
        lock (gate)
        {
            var saved = store.Read();
            return new(source.Status, mode, source.CaptureBoundaryMs, source.FinalizedThroughMs,
                saved.State.ProcessedThroughMs, TimeUntilAnalysis(),
                worker is not null, (source as BufferedTranscriptSource)?.ContinuityMessage ?? activity, error,
                !reconciliationRequired && (failed is not null || recoveryAwaitingUser),
                recovered.Count + (pendingAnswer is null ? 0 : 1) + (pendingBackground is null ? 0 : 1), saved, CanAbandonFailedRequest());
        }
    }

    public Task WhenIdleAsync() { lock (gate) return worker ?? Task.CompletedTask; }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private void Notify() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pendingAnswer = pendingBackground = null;
            nextAnalysis = null;
            lifetime.Cancel();
            // Ошибка остановки аудио не должна оставлять worker модели неотменённым.
            try { source.Stop(); }
            catch (Exception failure) { error = "Источник не подтвердил завершение: " + failure.Message; }
            running = worker;
        }
        if (running is not null) await running.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
