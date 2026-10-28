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
    private readonly CancellationTokenSource lifetime = new();
    private AnalysisMode mode;
    private TimeSpan? nextAnalysis;
    private Work? pendingAnswer;
    private Work? pendingBackground;
    private Work? failed;
    private Task? worker;
    private bool disposed;
    private string activity = "Ожидание старта";
    private string? error;
    private string context = "";
    private string fallback = "Кратко подведи итоги разговора и перечисли следующие действия.";
    public event Action? Changed;

    private sealed class Work(OperationKind kind, long boundary, string instruction, string fallback, string context)
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly OperationKind Kind = kind;
        public long Boundary = boundary;
        public readonly string Instruction = instruction;
        public readonly string Fallback = fallback;
        public readonly string Context = context;
        public ProcessingRequest? Request;
        public ProcessingResult? Result;
        public ChatMessage? Message;
    }

    public ConversationEngine(IClock clock, ITranscriptSource source, IModelBackend model, IConversationStore store)
        => (this.clock, this.source, this.model, this.store) = (clock, source, model, store);

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
            pendingBackground = null;
            nextAnalysis = source.Status == CaptureStatus.Capturing ? clock.Now + Interval : null;
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
            nextAnalysis = clock.Now + Interval;
            if (worker is null && failed is null) activity = "Симуляция идёт";
        }
        Notify();
    }

    public void TogglePause()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (source.Status == CaptureStatus.Capturing) { source.Pause(); nextAnalysis = null; }
            else if (source.Status == CaptureStatus.Paused) { source.Resume(); nextAnalysis = clock.Now + Interval; }
            if (worker is null && failed is null) activity = source.Status == CaptureStatus.Paused ? "Пауза" : "Симуляция идёт";
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
            if (worker is null && failed is null) activity = "Симуляция остановлена";
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
            if (source.Status == CaptureStatus.Capturing && nextAnalysis is { } due && clock.Now >= due)
            {
                nextAnalysis = clock.Now + Interval;
                long boundary = source.FinalizedThroughMs;
                bool hasNewSpeech = source.Segments.Any(s => s.IsFinal && s.EndMs > store.Read().State.ProcessedThroughMs && s.EndMs <= boundary);
                if (hasNewSpeech)
                    pendingBackground = new(mode == AnalysisMode.Manual ? OperationKind.Background : OperationKind.Automatic,
                        boundary, "", fallback, context);
                if (pendingBackground is not null || failed is not null) EnsureWorker();
            }
        }
        Notify();
    }

    // false оставляет набранный запрос в UI, чтобы пользователь не потерял текст.
    public bool Answer(string instruction = "")
    {
        bool accepted;
        lock (gate)
        {
            ThrowIfDisposed();
            source.Advance();
            long boundary = source.SnapshotBoundary();
            if (source.Status == CaptureStatus.Capturing) nextAnalysis = clock.Now + Interval;
            pendingBackground = null;
            if (pendingAnswer is not null && instruction.Length > 0 && pendingAnswer.Instruction != instruction)
                accepted = false;
            else
            {
                if (pendingAnswer is null) pendingAnswer = new(OperationKind.Answer, boundary, instruction, fallback, context);
                else pendingAnswer.Boundary = boundary;
                EnsureWorker();
                accepted = true;
            }
        }
        Notify();
        return accepted;
    }

    public void Retry()
    {
        lock (gate) { ThrowIfDisposed(); if (failed is not null) EnsureWorker(); }
        Notify();
    }

    // Вызывается только под gate. Task.Run не выполняет модельную логику на UI-потоке.
    private void EnsureWorker()
    {
        if (worker is null) worker = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            Work? work;
            lock (gate)
            {
                if (disposed) { worker = null; return; }
                work = failed ?? pendingAnswer ?? pendingBackground;
                if (work is null)
                {
                    worker = null;
                    activity = source.Status switch { CaptureStatus.Capturing => "Симуляция идёт", CaptureStatus.Paused => "Пауза", _ => "Ожидание старта / остановлено" };
                }
                else
                {
                    if (ReferenceEquals(work, failed)) failed = null;
                    if (ReferenceEquals(work, pendingAnswer)) pendingAnswer = null;
                    if (ReferenceEquals(work, pendingBackground)) pendingBackground = null;
                    error = null;
                    activity = work.Kind == OperationKind.Answer ? "Ожидание финализации обоих каналов" : "Фоновый анализ (симуляция)";
                }
            }
            Notify();
            if (work is null) return;
            try
            {
                if (work.Request is null)
                {
                    if (work.Kind == OperationKind.Answer)
                        await source.WaitForFinalizationAsync(work.Boundary, lifetime.Token).ConfigureAwait(false);
                    var previous = store.Read().State;
                    var segments = source.Segments.Where(s => s.IsFinal && s.EndMs > previous.ProcessedThroughMs && s.EndMs <= work.Boundary)
                        .OrderBy(s => s.StartMs).ThenBy(s => s.Channel).ThenBy(s => s.Id).ToArray();
                    // Тик, накопившийся во время предыдущей операции, мог уже устареть.
                    if (work.Kind != OperationKind.Answer && segments.Length == 0) continue;
                    work.Request = new(work.Id, work.Kind, Math.Max(work.Boundary, previous.ProcessedThroughMs), previous,
                        segments, work.Instruction, work.Fallback, work.Context);
                }
                lock (gate) activity = work.Result is null ? "Обработка моделью-имитатором" : "Повтор сохранения результата";
                Notify();
                work.Result ??= await model.ProcessAsync(work.Request, lifetime.Token).ConfigureAwait(false);
                try { Validate(work.Request, work.Result); }
                catch { work.Result = null; throw; }
                work.Message ??= work.Result.DisplayText is { Length: > 0 } text
                    ? new(work.Id, clock.UtcNow, text, work.Instruction.Length == 0 ? null : work.Instruction) : null;
                await store.CommitAsync(new(work.Id, work.Result, work.Message), lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                lock (gate) { worker = null; activity = "Завершено"; }
                Notify();
                return;
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    failed = work;
                    worker = null;
                    error = exception.Message;
                    activity = "Ошибка — данные сохранены в памяти для повтора";
                }
                Notify();
                return;
            }
        }
    }

    private static void Validate(ProcessingRequest request, ProcessingResult result)
    {
        if (result.State.ProcessedThroughMs != request.BoundaryMs)
            throw new InvalidOperationException("Модель вернула неверную границу обработки.");
        if (request.Kind == OperationKind.Background && result.DisplayText is not null)
            throw new InvalidOperationException("Фоновый ручной анализ не должен создавать видимый ответ.");
        if (result.State.Questions.Select(q => q.Id).Distinct().Count() != result.State.Questions.Count)
            throw new InvalidOperationException("Модель вернула повторяющиеся ID вопросов.");
    }

    public EngineSnapshot Snapshot()
    {
        lock (gate)
        {
            var saved = store.Read();
            return new(source.Status, mode, source.CaptureBoundaryMs, source.FinalizedThroughMs,
                saved.State.ProcessedThroughMs, nextAnalysis is { } due ? TimeSpan.FromTicks(Math.Max(0, (due - clock.Now).Ticks)) : null,
                worker is not null, activity, error, failed is not null,
                (pendingAnswer is null ? 0 : 1) + (pendingBackground is null ? 0 : 1), saved);
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
            source.Stop();
            pendingAnswer = pendingBackground = null;
            nextAnalysis = null;
            lifetime.Cancel();
            running = worker;
        }
        if (running is not null) await running.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
