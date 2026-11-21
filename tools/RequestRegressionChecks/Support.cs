using SpeechAnalyzer.Core;

// Same in-memory fixture contract used by the existing coordinator tests.
sealed class ManualClock : IClock
{
    private long ticks;
    public TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref ticks));
    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Now;
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
        Store = store ?? new MemoryConversationStore(); Engine = new(Clock, Source, Backend, Store);
    }
    public void Advance(double seconds) { Clock.Advance(seconds); Engine.Pulse(); }
    public void Add(string text, SegmentMeaning meaning = SegmentMeaning.Fact, string? id = null) => Source.Inject(text, AudioChannel.Other, meaning, id);
    public Task Idle() => Engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}
sealed class ControlledBackend : IModelBackend
{
    readonly List<ProcessingRequest> requests = [];
    public ProcessingRequest[] Requests { get { lock(requests) return requests.ToArray(); } }
    public TaskCompletionSource? HoldNext;
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool FailNext;
    int concurrent;
    public int MaxConcurrent;
    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken token)
    {
        int active = Interlocked.Increment(ref concurrent); MaxConcurrent = Math.Max(MaxConcurrent, active);
        lock(requests) requests.Add(request); var hold = Interlocked.Exchange(ref HoldNext, null);
        Entered.TrySetResult();
        try
        {
            if(hold is not null) await hold.Task.WaitAsync(token);
            if(FailNext) { FailNext = false; throw new IOException("Synthetic transport failure"); }
            return SimulatedModelBackend.Evaluate(request);
        }
        finally { Interlocked.Decrement(ref concurrent); }
    }
}
