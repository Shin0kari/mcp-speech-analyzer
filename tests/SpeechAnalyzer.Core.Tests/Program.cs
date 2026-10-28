using SpeechAnalyzer.Core;

// Автономный сценарный runner: не требует NuGet/test SDK и не вызывает реальную модель.
var tests = new (string Name, Func<Task> Run)[]
{
    ("Старт окна не запускает захват", InitialState),
    ("Тишина: фон не вызывает модель", Silence),
    ("Ручной фон через 60 секунд невидим", ManualBackground),
    ("Клик на 80-й секунде: остаток и срок 140", ClickAtEighty),
    ("Клик ждёт оба канала и исключает позднюю речь", BothChannelsAndCut),
    ("Клик во время фона использует успешный результат", ClickDuringBackground),
    ("Повторные клики и тики ограничены двумя слотами", BoundedEvents),
    ("Сбой модели не продвигает границу; повтор не теряет текст", ModelFailure),
    ("Сбой сохранения повторяет commit без нового вызова модели", SaveFailure),
    ("Потерянное подтверждение commit не дублирует сообщение", LostCommitAcknowledgment),
    ("Автоматический режим и подавление дублей", AutomaticMode),
    ("Ответ участника и устаревший вопрос исключаются", ClosedQuestions),
    ("Явный запрос и fallback передаются при отсутствии нового текста", ExplicitAndFallback),
    ("Разные пользовательские запросы не теряются молча", ExplicitRequests),
    ("Пауза и остановка не создают новые события", PauseAndStop),
    ("Сегменты передаются по времени с правильными каналами", Chronology),
    ("Commit с тем же ID идемпотентен", IdempotentCommit),
    ("Неверная граница модели отклоняется и допускает исправленный повтор", InvalidModelResult),
    ("Завершение отменяет ожидание pending-транскрипции", Shutdown),
    ("Новый предварительный текст не откатывает готовность источника", MonotonicReadiness)
};
int failures = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {error}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message = "Условие не выполнено")
{
    if (!condition) throw new Exception(message);
}
static async Task Until(Func<bool> condition)
{
    for (int i = 0; i < 2000; i++) { if (condition()) return; await Task.Delay(1); }
    throw new TimeoutException("Управляемая операция не достигла ожидаемого состояния");
}
static TaskCompletionSource Block() => new(TaskCreationOptions.RunContinuationsAsynchronously);

static async Task InitialState()
{
    await using var f = new Fixture();
    f.Advance(100);
    Check(f.Engine.Snapshot().Capture == CaptureStatus.Stopped);
    Check(f.Source.CaptureBoundaryMs == 0 && f.Backend.Requests.Length == 0);
    Check(f.Engine.Snapshot().NextAnalysisIn is null);
}
static async Task Silence()
{
    await using var f = new Fixture(); f.Engine.Start();
    for (int i = 0; i < 5; i++) { f.Advance(60); await f.Idle(); }
    Check(f.Backend.Requests.Length == 0);
    f.Engine.SetMode(AnalysisMode.Automatic); f.Advance(5); await f.Idle();
    Check(f.Backend.Requests.Length == 0);
}
static async Task ManualBackground()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Add("Факт");
    f.Advance(59); await f.Idle(); Check(f.Backend.Requests.Length == 0);
    f.Advance(1); await f.Idle();
    Check(f.Backend.Requests.Length == 1);
    Check(f.Backend.Requests[0].Kind == OperationKind.Background);
    Check(f.Store.Read().Messages.Count == 0);
    Check(f.Store.Read().State.ProcessedThroughMs == f.Source.CaptureBoundaryMs);
}
static async Task ClickAtEighty()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Add("Первый факт");
    f.Advance(60); await f.Idle(); long oldBoundary = f.Store.Read().State.ProcessedThroughMs;
    f.Advance(10); f.Add("Остаток"); f.Advance(10);
    long click = f.Source.CaptureBoundaryMs;
    Check(f.Engine.Answer()); await f.Idle();
    var request = f.Backend.Requests[1];
    Check(request.Previous.ProcessedThroughMs == oldBoundary);
    Check(request.Segments.Count == 1 && request.Segments[0].Text == "Остаток");
    Check(request.BoundaryMs == click);
    Check(f.Engine.Snapshot().NextAnalysisIn == TimeSpan.FromSeconds(60));
    f.Advance(59); await f.Idle(); Check(f.Backend.Requests.Length == 2);
    f.Add("Следующая речь"); f.Advance(1); await f.Idle();
    Check(f.Backend.Requests.Length == 3 && f.Backend.Requests[2].Kind == OperationKind.Background);
    Check(f.Clock.Now == TimeSpan.FromSeconds(140));
}
static async Task BothChannelsAndCut()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Advance(1);
    f.Source.Inject("Вы до клика", AudioChannel.You, delay: TimeSpan.FromSeconds(1));
    f.Source.Inject("Собеседник до клика", AudioChannel.Other, delay: TimeSpan.FromSeconds(3));
    f.Engine.Answer(); long cut = f.Source.CaptureBoundaryMs;
    await Until(() => f.Engine.Snapshot().IsBusy);
    f.Advance(1); await Task.Delay(10); Check(f.Backend.Requests.Length == 0);
    f.Source.Inject("После клика", AudioChannel.Other, delay: TimeSpan.FromSeconds(10));
    f.Advance(2); await f.Idle();
    Check(f.Backend.Requests.Length == 1);
    Check(f.Backend.Requests[0].BoundaryMs == cut);
    Check(f.Backend.Requests[0].Segments.Count == 2);
    Check(f.Backend.Requests[0].Segments.All(s => s.EndMs <= cut && s.IsFinal));
    Check(f.Source.Segments.Single(s => s.Text == "После клика").StartMs >= cut);
    Check(f.Engine.Snapshot().NextAnalysisIn == TimeSpan.FromSeconds(57));
}
static async Task ClickDuringBackground()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Add("Фон");
    var block = Block(); f.Backend.HoldNext = block;
    f.Advance(60); await Until(() => f.Backend.Requests.Length == 1);
    f.Advance(20); f.Add("Остаток после фона"); f.Engine.Answer();
    f.Advance(4); block.SetResult(); await f.Idle();
    Check(f.Backend.MaxConcurrent == 1 && f.Backend.Requests.Length == 2);
    Check(f.Backend.Requests[1].Previous.ProcessedThroughMs == f.Backend.Requests[0].BoundaryMs);
    Check(f.Backend.Requests[1].Segments.Single().Text == "Остаток после фона");
    Check(f.Engine.Snapshot().NextAnalysisIn == TimeSpan.FromSeconds(56), "Таймер сдвинут на время завершения ответа");
}
static async Task BoundedEvents()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Add("Новая речь");
    var block = Block(); f.Backend.HoldNext = block;
    f.Engine.Answer(); await Until(() => f.Backend.Requests.Length == 1);
    for (int i = 0; i < 500; i++) { f.Engine.Answer(); f.Engine.Pulse(); Check(f.Engine.Snapshot().PendingOperations <= 2); }
    f.Advance(60); Check(f.Engine.Snapshot().PendingOperations <= 2);
    block.SetResult(); await f.Idle();
    Check(f.Backend.Requests.Length == 2 && f.Backend.MaxConcurrent == 1);
}
static async Task ModelFailure()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Add("Не потерять");
    f.Backend.FailNext = true; f.Engine.Answer(); await f.Idle();
    Check(f.Engine.Snapshot().CanRetry && f.Store.Read().State.ProcessedThroughMs == 0);
    Check(f.Store.Read().Messages.Count == 0);
    f.Engine.Retry(); await f.Idle();
    Check(!f.Engine.Snapshot().CanRetry && f.Store.Read().Messages.Count == 1);
    Check(f.Backend.Requests[1].Segments.Single().Text == "Не потерять");
    Check(f.Backend.Requests[0].OperationId == f.Backend.Requests[1].OperationId);
}
static async Task SaveFailure()
{
    var broken = new FailingStore();
    await using var f = new Fixture(broken); f.Engine.Start(); f.Add("Важный факт");
    f.Engine.Answer(); await f.Idle();
    Check(f.Engine.Snapshot().CanRetry && f.Store.Read().State.ProcessedThroughMs == 0);
    f.Engine.Retry(); await f.Idle();
    Check(f.Backend.Requests.Length == 1, "Модель вызвана повторно после сбоя commit");
    Check(f.Store.Read().Messages.Count == 1 && f.Store.Read().State.Facts.Contains("Важный факт"));
}
static async Task AutomaticMode()
{
    await using var f = new Fixture(); f.Engine.SetMode(AnalysisMode.Automatic); f.Engine.Start();
    f.Add("Нужно понять срок запуска", SegmentMeaning.Question, "launch");
    f.Advance(4); await f.Idle(); Check(f.Backend.Requests.Length == 0);
    f.Advance(1); await f.Idle(); Check(f.Store.Read().Messages.Count == 1);
    f.Add("Ещё один факт"); f.Advance(5); await f.Idle();
    Check(f.Backend.Requests.Length == 2 && f.Store.Read().Messages.Count == 1);
    f.Advance(5); await f.Idle(); Check(f.Backend.Requests.Length == 2);
    Check(f.Store.Read().State.Questions.Single().Status == QuestionStatus.Displayed);
}
static async Task LostCommitAcknowledgment()
{
    await using var f = new Fixture(new LostAcknowledgmentStore()); f.Engine.Start(); f.Add("Не дублировать");
    f.Engine.Answer(); await f.Idle();
    Check(f.Engine.Snapshot().CanRetry && f.Store.Read().Messages.Count == 1);
    f.Engine.Retry(); await f.Idle();
    Check(!f.Engine.Snapshot().CanRetry && f.Store.Read().Messages.Count == 1);
    Check(f.Backend.Requests.Length == 1);
}
static async Task ClosedQuestions()
{
    await using var f = new Fixture(); f.Engine.Start();
    f.Add("Хотелось бы понять сроки", SegmentMeaning.Question, "date");
    f.Advance(1); f.Add("Начинаем 15 ноября", SegmentMeaning.ParticipantAnswer, "date");
    f.Advance(1); f.Add("Нужна оценка старого плана", SegmentMeaning.Question, "old");
    f.Advance(1); f.Add("Старый план отменён", SegmentMeaning.ObsoleteQuestion, "old");
    f.Engine.Answer(); await f.Idle();
    var questions = f.Store.Read().State.Questions;
    Check(questions.Single(q => q.Id == "date").Status == QuestionStatus.AnsweredByParticipant);
    Check(questions.Single(q => q.Id == "old").Status == QuestionStatus.Obsolete);
    Check(f.Store.Read().Messages.Single().Text.Contains("Резервная инструкция"));
}
static async Task ExplicitAndFallback()
{
    await using var f = new Fixture(); f.Engine.Start();
    f.Engine.Configure("Контекст конференции", "Моя инструкция для итога");
    f.Engine.Answer(); await f.Idle();
    Check(f.Store.Read().Messages.Single().Text.Contains("Моя инструкция для итога"));
    f.Engine.Answer("Ответь ещё раз на старый вопрос"); await f.Idle();
    Check(f.Backend.Requests[1].Segments.Count == 0);
    Check(f.Backend.Requests[1].Context == "Контекст конференции");
    Check(f.Store.Read().Messages[1].UserInstruction == "Ответь ещё раз на старый вопрос");
}
static async Task ExplicitRequests()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Add("Контекст");
    var block = Block(); f.Backend.HoldNext = block;
    Check(f.Engine.Answer("Первый")); await Until(() => f.Backend.Requests.Length == 1);
    Check(f.Engine.Answer("Второй"));
    Check(!f.Engine.Answer("Третий"), "Третий запрос молча перезаписал второй");
    Check(f.Engine.Answer()); block.SetResult(); await f.Idle();
    Check(f.Backend.Requests.Length == 2 && f.Backend.Requests[1].UserInstruction == "Второй");
}
static async Task PauseAndStop()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Add("До паузы"); f.Advance(10);
    long before = f.Source.CaptureBoundaryMs; f.Engine.TogglePause(); f.Advance(120);
    Check(f.Source.CaptureBoundaryMs == before && f.Backend.Requests.Length == 0);
    f.Engine.TogglePause(); Check(f.Engine.Snapshot().NextAnalysisIn == TimeSpan.FromSeconds(60));
    f.Advance(60); await f.Idle(); Check(f.Backend.Requests.Length == 1);
    f.Engine.Stop(); before = f.Source.CaptureBoundaryMs; f.Advance(120); await f.Idle();
    Check(f.Source.CaptureBoundaryMs == before && f.Backend.Requests.Length == 1);
}
static async Task Chronology()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Advance(2);
    f.Source.Inject("Первый канал", AudioChannel.You, delay: TimeSpan.Zero);
    f.Advance(2); f.Source.Inject("Второй канал", AudioChannel.Other, delay: TimeSpan.Zero);
    f.Engine.Answer(); await f.Idle();
    var segments = f.Backend.Requests.Single().Segments;
    Check(segments.Count == 2 && segments[0].Channel == AudioChannel.You && segments[1].Channel == AudioChannel.Other);
    Check(segments[0].StartMs <= segments[1].StartMs);
}
static async Task IdempotentCommit()
{
    var store = new MemoryConversationStore(); var id = Guid.NewGuid();
    var result = new ProcessingResult(new(100, ["Факт"], [], []), "Ответ");
    var commit = new ProcessingCommit(id, result, new(id, DateTimeOffset.UtcNow, "Ответ", null));
    await store.CommitAsync(commit, default); await store.CommitAsync(commit, default);
    Check(store.Read().Messages.Count == 1);
}
static async Task InvalidModelResult()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Add("Факт");
    f.Backend.InvalidNext = true; f.Engine.Answer(); await f.Idle();
    Check(f.Engine.Snapshot().CanRetry && f.Store.Read().State.ProcessedThroughMs == 0);
    f.Engine.Retry(); await f.Idle();
    Check(f.Backend.Requests.Length == 2 && f.Store.Read().Messages.Count == 1);
}
static async Task Shutdown()
{
    var f = new Fixture(); f.Engine.Start();
    f.Source.Inject("Pending", AudioChannel.Other, delay: TimeSpan.FromHours(1)); f.Engine.Answer();
    await Until(() => f.Engine.Snapshot().IsBusy);
    await f.DisposeAsync(); Check(f.Backend.Requests.Length == 0);
}
static async Task MonotonicReadiness()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Advance(10);
    long ready = f.Source.FinalizedThroughMs;
    f.Source.Inject("Новая реплика", AudioChannel.Other, delay: TimeSpan.FromSeconds(2));
    Check(f.Source.FinalizedThroughMs >= ready);
    f.Advance(3); ready = f.Source.FinalizedThroughMs;
    f.Source.DemoSpeechEnabled = true; f.Advance(5);
    Check(f.Source.FinalizedThroughMs >= ready);
}

sealed class ManualClock : IClock
{
    private long ticks;
    public TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref ticks));
    public DateTimeOffset UtcNow => new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero) + Now;
    public void Advance(double seconds) => Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
}
sealed class Fixture : IAsyncDisposable
{
    public readonly ManualClock Clock = new();
    public readonly SimulatedTranscriptSource Source;
    public readonly ControlledBackend Backend = new();
    public readonly IConversationStore Store;
    public readonly ConversationEngine Engine;
    public Fixture(IConversationStore? store = null)
    {
        Source = new(Clock) { DemoSpeechEnabled = false, FinalizationDelay = TimeSpan.Zero };
        Store = store ?? new MemoryConversationStore();
        Engine = new(Clock, Source, Backend, Store);
    }
    public void Advance(double seconds) { Clock.Advance(seconds); Engine.Pulse(); }
    public void Add(string text, SegmentMeaning meaning = SegmentMeaning.Fact, string? id = null)
        => Source.Inject(text, AudioChannel.Other, meaning, id);
    public Task Idle() => Engine.WhenIdleAsync();
    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}
sealed class ControlledBackend : IModelBackend
{
    private readonly object gate = new();
    private readonly List<ProcessingRequest> requests = [];
    public ProcessingRequest[] Requests { get { lock (gate) return requests.ToArray(); } }
    public TaskCompletionSource? HoldNext;
    public bool FailNext;
    public bool InvalidNext;
    private int concurrent;
    public int MaxConcurrent;
    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken cancellationToken)
    {
        int active = Interlocked.Increment(ref concurrent); MaxConcurrent = Math.Max(MaxConcurrent, active);
        var hold = Interlocked.Exchange(ref HoldNext, null);
        lock (gate) requests.Add(request);
        try
        {
            if (hold is not null) await hold.Task.WaitAsync(cancellationToken);
            if (FailNext) { FailNext = false; throw new IOException("Тестовый сбой модели"); }
            var result = SimulatedModelBackend.Evaluate(request);
            if (InvalidNext) { InvalidNext = false; return result with { State = result.State with { ProcessedThroughMs = -1 } }; }
            return result;
        }
        finally { Interlocked.Decrement(ref concurrent); }
    }
}
sealed class FailingStore : IConversationStore
{
    private readonly MemoryConversationStore inner = new();
    private bool failNext = true;
    public StoreSnapshot Read() => inner.Read();
    public Task CommitAsync(ProcessingCommit commit, CancellationToken cancellationToken)
    {
        if (failNext) { failNext = false; throw new IOException("Тестовый сбой сохранения"); }
        return inner.CommitAsync(commit, cancellationToken);
    }
}
sealed class LostAcknowledgmentStore : IConversationStore
{
    private readonly MemoryConversationStore inner = new();
    private bool failNext = true;
    public StoreSnapshot Read() => inner.Read();
    public async Task CommitAsync(ProcessingCommit commit, CancellationToken cancellationToken)
    {
        await inner.CommitAsync(commit, cancellationToken);
        if (failNext) { failNext = false; throw new IOException("Commit прошёл, подтверждение потеряно"); }
    }
}
