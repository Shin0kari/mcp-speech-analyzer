using SpeechAnalyzer.Core;

static class SessionCoordinatorTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("Нарушен жизненный цикл сессии"); }
    public static Task TranscriptRows()
    {
        var projection = new TranscriptProjection();
        var segment = new TranscriptSegment(Guid.NewGuid(), AudioChannel.Other, 0, 1000, DateTimeOffset.UnixEpoch, "Ожидает распознавания", false);
        projection.Synchronize([segment]); var row = projection.Rows.Single();
        var notices = new List<string?>(); row.PropertyChanged += (_, e) => notices.Add(e.PropertyName);
        projection.Synchronize([segment with { Text = new string('я', 50000), IsFinal = true }]);
        Check(ReferenceEquals(row, projection.Rows.Single()) && row.Text.Length == 50000 && row.IsFinal);
        Check(notices.Contains("Text") && notices.Contains("Header") && notices.Contains("IsFinal"));
        projection.Synchronize([]); Check(projection.Rows.Count == 0);
        projection.Synchronize([segment with { Text = " " }]); Check(projection.Rows.Count == 0);
        return Task.CompletedTask;
    }
    public static async Task Transitions()
    {
        await using var f = new Fixture(); var driver = new Driver();
        await using var session = new SessionCoordinator(f.Engine, f.Source, driver);
        Check(driver.Starts == 0 && session.Phase == SessionPhase.Stopped);
        await Task.WhenAll(session.StartAsync(), session.StartAsync()); Check(driver.Starts == 1);
        await session.PauseAsync(); await session.PauseAsync(); Check(driver.Stops == 1 && f.Source.Status == CaptureStatus.Paused);
        await session.ResumeAsync(); await session.ResumeAsync(); Check(driver.Starts == 2);
        await session.StopAsync(); await session.StopAsync(); Check(driver.Stops == 2 && session.Phase == SessionPhase.Stopped);
    }
    public static async Task PartialStart()
    {
        await using var f = new Fixture(); var driver = new Driver { FailStart = true };
        await using var session = new SessionCoordinator(f.Engine, f.Source, driver);
        try { await session.StartAsync(); throw new Exception("Ожидался сбой второго источника"); } catch (IOException) { }
        Check(session.Phase == SessionPhase.Faulted && driver.Stops == 1 && !driver.Running);
        Check(f.Source.Status == CaptureStatus.Stopped);
        await session.StopAsync(); driver.FailStart = false; await session.StartAsync(); Check(driver.Running);
    }
    public static async Task StopTail()
    {
        await using var f = new Fixture(); var driver = new Driver();
        await using var session = new SessionCoordinator(f.Engine, f.Source, driver);
        await session.StartAsync();
        driver.Tail = () => f.Source.Inject("Последний пакет", AudioChannel.You, delay: TimeSpan.FromSeconds(1));
        var stop = session.StopAsync();
        Check(!stop.IsCompleted && f.Source.Segments.Single().Text == "Последний пакет");
        f.Clock.Advance(2); await stop;
        Check(session.Phase == SessionPhase.Stopped && f.Source.Segments.All(s => s.IsFinal));
        driver.Tail = null;
    }
    public static async Task ClosePending()
    {
        await using var f = new Fixture(); var driver = new Driver();
        var session = new SessionCoordinator(f.Engine, f.Source, driver, TimeSpan.FromMilliseconds(50));
        await session.StartAsync(); f.Source.Inject("Незавершённая речь", AudioChannel.Other, delay: TimeSpan.FromHours(1));
        await session.AnswerAsync();
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(session.Phase == SessionPhase.Closed && session.Error is not null && driver.Disposed);
        Check(f.Store.Read().State.ProcessedThroughMs == 0);
        await session.DisposeAsync();
    }
    public static async Task CloseDuringModel()
    {
        await using var f = new Fixture(); var driver = new Driver();
        var session = new SessionCoordinator(f.Engine, f.Source, driver);
        await session.StartAsync(); f.Add("Вопрос", SegmentMeaning.Question); f.Backend.HoldNext = new();
        await session.AnswerAsync();
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(driver.Disposed && session.Phase == SessionPhase.Closed && f.Store.Read().Messages.Count == 0);
    }
    public static async Task SourceFailure()
    {
        await using var f = new Fixture(); var driver = new Driver();
        await using var session = new SessionCoordinator(f.Engine, f.Source, driver);
        await session.StartAsync(); await session.ReportFailureAsync("Пропал микрофон");
        Check(!driver.Running && session.Phase == SessionPhase.Faulted && session.Error == "Пропал микрофон");
        await session.StopAsync(); Check(session.Phase == SessionPhase.Stopped);
    }
    public static async Task CloseDuringSave()
    {
        var store = new HeldStore();
        await using var f = new Fixture(store);
        var driver = new Driver();
        var session = new SessionCoordinator(f.Engine, f.Source, driver);
        await session.StartAsync(); f.Add("Факт для сохранения"); await session.AnswerAsync();
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(session.Phase == SessionPhase.Closed && driver.Disposed && store.Read().State.ProcessedThroughMs == 0);
    }
    public static async Task HungSourceStop()
    {
        await using var f = new Fixture();
        var driver = new Driver { HoldStop = true };
        var session = new SessionCoordinator(f.Engine, f.Source, driver, TimeSpan.FromMilliseconds(30));
        await session.StartAsync();
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Check(session.Phase == SessionPhase.Closed && session.Error is not null && driver.Disposed);
    }
    private sealed class HeldStore : IConversationStore
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StoreSnapshot Read() => new(ConversationState.Empty, []);
        public async Task CommitAsync(ProcessingCommit commit, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
    private sealed class Driver : ICaptureLifecycle
    {
        public int Starts, Stops; public bool FailStart, Running, Disposed, HoldStop;
        public Action? Tail;
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Starts++; Running = true;
            if (FailStart) throw new IOException("Второй источник не запустился");
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stops++; Tail?.Invoke(); Running = false;
            return HoldStop ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken) : Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
