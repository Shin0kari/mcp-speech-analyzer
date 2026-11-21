using SpeechAnalyzer.Core;
using SpeechAnalyzer.Testing;

internal static class RollingCases
{
    public static Task Configuration()
    {
        Check.Is(RollingContextConfiguration.Parse(null).Seconds == 60 && RollingContextConfiguration.Parse("47").Seconds == 47,
            "Wrong default or integer parsing.");
        foreach (var value in new[] { "29", "121", "3.5", " 60", "0", "" })
        {
            try { RollingContextConfiguration.Parse(value); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Invalid configuration accepted.");
        }
        var directory = TestRunOutput.Current.NewDirectory("legacy-schedule");
        var path = Path.Combine(directory, "rolling-context.json");
        const string saved = """{"Version":1,"Seconds":60}""";
        File.WriteAllText(path, saved);
        try { new SessionJournal(directory).BindRollingContext(() => new(30)); }
        catch (IOException error) when (error.Message.Contains("3n", StringComparison.Ordinal))
        {
            Check.Is(File.ReadAllText(path) == saved, "Legacy schedule metadata changed.");
            return Task.CompletedTask;
        }
        throw new InvalidOperationException("Legacy session was silently rebound.");
    }

    public static async Task Timeline(int n)
    {
        await using var f = new Fixture(n, "timeline-" + n);
        f.Engine.Start();
        f.Text(0, n, AudioChannel.You, "first");
        f.Text(n, 2 * n, AudioChannel.Other, "second");
        f.Text(n - 1, n + 1, AudioChannel.Other, "crosses first context cutoff");
        foreach (int t in new[] { n, 2 * n - 1 }) await f.Tick(t);
        Check.Is(f.Model.Requests.Count == 0, "Premature background.");
        await f.Tick(2 * n);
        f.Request(0, OperationKind.Background, n, "first");
        await f.Tick(3 * n);
        f.Request(1, OperationKind.Background, 2 * n, "crosses first context cutoff", "second");
        Check.Is(f.Journal.Read().Messages.Count == 0 && f.Journal.Read().State.ProcessedThroughMs == 2 * n * 1000L,
            "Silent commit failed.");
        await f.Tick(4 * n);
        Check.Is(f.Model.Requests.Count == 2, "Silence invoked the model.");
        Check.Is(f.Journal.Transcript.Select(x => x.Channel).Distinct().Count() == 2 &&
            f.Journal.Transcript.Select(x => x.Text).Distinct().Count() == 3, "Original channels or text lost.");
    }

    public static async Task WaitForEligibleText(int n)
    {
        await using var f = new Fixture(n, "pending-finalization-" + n);
        f.Engine.Start();
        var pending = f.Text(0, n, AudioChannel.You, "late final", pending: true);
        f.Text(n, 2 * n, AudioChannel.Other, "second unit");
        await f.Tick(2 * n);
        Check.Is(f.Model.Requests.Count == 0, "Background bypassed provisional text.");
        f.Source.Finalize(pending);
        await f.Tick(2 * n + 1);
        f.Request(0, OperationKind.Background, n, "late final");
        await f.Tick(3 * n);
        f.Request(1, OperationKind.Background, 2 * n, "second unit");
    }

    public static async Task EarlyAnswers(int n)
    {
        foreach (int time in new[] { n / 2, 2 * n, 5 * n / 2 })
        {
            await using var f = new Fixture(n, $"early-{n}-{time}");
            f.Engine.Start();
            f.Text(0, time / 2, AudioChannel.You, "first question");
            f.Text(time / 2, time, AudioChannel.Other, "participant reply");
            f.Clock.Milliseconds = time * 1000L;
            Check.Is(f.Model.Requests.Count == 0 && f.Engine.Answer(), "Early Answer refused.");
            await Check.Idle(f.Engine);
            f.Request(0, OperationKind.Answer, time, "first question", "participant reply");
            Check.Is(f.Engine.Snapshot().NextAnalysisIn == TimeSpan.FromSeconds(2 * n), "Cycle did not restart at Answer.");
            f.Text(time, time + 1, AudioChannel.Other, "after click");
            await f.Tick(time + 1);
            f.Engine.Stop();
            await f.Tick(time + 4 * n);
            Check.Is(f.Model.Requests.Count == 1 && f.Journal.Read().Messages.Count == 1 &&
                f.Journal.Transcript.Count == 3 && f.Engine.Snapshot().NextAnalysisIn is null, "Stop scheduled a turn or lost originals.");
        }
    }

    public static async Task Race(int n)
    {
        await using var f = new Fixture(n, "race-" + n);
        f.Engine.Start();
        f.Text(0, n, AudioChannel.You, "background");
        var provisional = f.Text(n, 2 * n, AudioChannel.Other, "needs finalization", pending: true);
        f.Model.Hold = Check.Gate();
        f.Clock.Milliseconds = 2 * n * 1000L;
        f.Engine.Pulse();
        await f.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Is(f.Engine.Answer(), "Answer refused at first eligibility.");
        f.Text(2 * n, 2 * n + 1, AudioChannel.You, "future");
        f.Clock.Milliseconds += 1000;
        for (int i = 0; i < 100; i++) Check.Is(f.Engine.Answer(), "Repeated click refused.");
        Check.Is(!f.Engine.Answer("distinct") && f.Engine.Snapshot().PendingOperations == 1, "Queue exceeded one pending click.");
        f.Model.Hold.SetResult();
        await f.Source.FinalizationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Is(f.Model.Requests.Count == 1, "Answer ran before ASR.");
        f.Source.Finalize(provisional);
        await Check.Idle(f.Engine);
        f.Request(0, OperationKind.Background, n, "background");
        f.Request(1, OperationKind.Answer, 2 * n, "needs finalization");
        Check.Is(f.Model.Requests[1].Previous.ProcessedThroughMs == n * 1000L &&
            f.Journal.Read().Messages.Count == 1 && f.Engine.Snapshot().NextAnalysisIn == TimeSpan.FromSeconds(2 * n - 1),
            "Racing cut, persisted state, or timer incorrect.");
    }

    public static async Task Restart(int n)
    {
        string name = "restart-" + n;
        await using (var f = new Fixture(n, name))
        {
            f.Engine.Start();
            f.Text(0, n, AudioChannel.Other, "unconsumed prior cycle");
            f.Clock.Milliseconds = 2 * n * 1000L;
            f.Model.Reject = true;
            Check.Is(f.Engine.Answer(), "Answer refused.");
            await Check.Idle(f.Engine);
            Check.Is(f.Journal.Read().State.ProcessedThroughMs == 0 && f.Journal.Read().Messages.Count == 0,
                "Rejected result consumed evidence.");
            f.Text(2 * n, 3 * n, AudioChannel.You, "new cycle first unit");
            f.Text(3 * n, 4 * n, AudioChannel.Other, "active window");
        }

        await using var restored = new Fixture(n, name, reopen: true);
        restored.Clock.Milliseconds = (4 * n - 1) * 1000L;
        restored.Engine.ResumeRecoveredOperations();
        restored.Engine.Start();
        await restored.Tick(4 * n - 1);
        Check.Is(restored.Model.Requests.Count == 0, "Restart lost the cycle origin.");
        await restored.Tick(4 * n);
        restored.Request(0, OperationKind.Background, 3 * n, "unconsumed prior cycle", "new cycle first unit");
        Check.Is(restored.Engine.Answer(), "Recovered Answer refused.");
        await Check.Idle(restored.Engine);
        restored.Request(1, OperationKind.Answer, 4 * n, "active window");
        Check.Is(restored.Journal.Read().Messages.Count == 1 && restored.Journal.Transcript.Count == 3, "Restart duplicated evidence.");
    }

    public static async Task SaveRetry(bool lostAck)
    {
        var clock = new ControlledClock();
        var journal = new SessionJournal(TestRunOutput.Current.NewDirectory("save-" + lostAck));
        var source = new TextSource(clock, journal);
        var model = new FakeModel();
        var store = new CommitFault(journal, lostAck);
        await using var engine = new ConversationEngine(clock, source, model, store, rollingContext: new(47));
        engine.Start();
        source.Add(0, 1000, AudioChannel.Other, "original", false);
        clock.Milliseconds = 94000;
        engine.Pulse();
        await Check.Idle(engine);
        Check.Is(engine.Snapshot().CanRetry && model.Requests.Count == 1
            && journal.Read().State.ProcessedThroughMs == (lostAck ? 47000 : 0), "Save failure advanced the wrong boundary.");
        engine.Retry();
        await Check.Idle(engine);
        Check.Is(model.Requests.Count == 1 && journal.Read().State.ProcessedThroughMs == 47000
            && journal.Read().Messages.Count == 0 && journal.Transcript.Single().Text == "original", "Retry repeated the remote turn or lost evidence.");
    }

    public static async Task Uncertainty()
    {
        await using var f = new Fixture(60, "uncertain");
        f.Engine.Start();
        f.Text(0, 60, AudioChannel.You, "first");
        f.Text(60, 120, AudioChannel.Other, "second");
        f.Model.Uncertain = true;
        await f.Tick(120);
        await f.Tick(180);
        Check.Is(f.Model.Requests.Count == 1 && !f.Engine.Answer() &&
            f.Journal.Read().State.ProcessedThroughMs == 0, "Unknown turn was repeated or consumed.");
        var pending = f.Journal.ModelOperations.Single();
        Check.Is(pending.Phase == ModelOperationPhase.ReconciliationRequired, "Unknown turn was not journaled.");
        await f.Engine.ReconcileAsync(pending.OperationId, (operation, _) => Task.FromResult(FakeModel.Result(operation.Request)));
        await Check.Idle(f.Engine);
        Check.Is(f.Journal.Read().State.ProcessedThroughMs == 60000 && f.Model.Requests.Count == 1, "Reconciliation repeated remote work.");
        Check.Is(f.Engine.Answer(), "Answer after reconciliation refused.");
        await Check.Idle(f.Engine);
        f.Request(1, OperationKind.Answer, 180, "second");
    }

    public static async Task Rejection()
    {
        await using var f = new Fixture(47, "rejected");
        f.Engine.Start();
        f.Text(0, 47, AudioChannel.You, "unacknowledged");
        f.Text(47, 94, AudioChannel.Other, "successor only");
        f.Model.Reject = true;
        f.Model.Hold = Check.Gate();
        f.Clock.Milliseconds = 94000;
        f.Engine.Pulse();
        await f.Model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Is(f.Engine.Answer(), "Successor refused.");
        f.Model.Hold.SetResult();
        await Check.Idle(f.Engine);
        f.Request(1, OperationKind.Answer, 94, "unacknowledged", "successor only");
        Check.Is(f.Journal.ModelOperations.Count(operation => operation.Phase == ModelOperationPhase.ValidationRejected) == 1
            && f.Journal.Read().Messages.Count == 1, "Rejection stranded successor or published background.");
    }

    public static async Task LongSession()
    {
        await using var f = new Fixture(30, "long");
        f.Engine.Start();
        for (int i = 1; i <= 240; i++)
        {
            f.Text((i - 1) * 30, i * 30, i % 2 == 0 ? AudioChannel.You : AudioChannel.Other, "line " + i);
            await f.Tick(i * 30);
            Check.Is(f.Engine.Snapshot().PendingOperations <= 1, "Background backlog grew.");
        }
        Check.Is(f.Engine.Answer(), "Final Answer refused.");
        await Check.Idle(f.Engine);
        var sent = f.Model.Requests.SelectMany(r => r.Segments).ToArray();
        Check.Is(sent.Length == 240 && sent.Select(s => s.Id).Distinct().Count() == 240 &&
            sent.Select(s => s.Text).SequenceEqual(f.Journal.Transcript.Select(s => s.Text)) &&
            sent.Last().EndMs == 7200000, "Long session lost, duplicated, or renumbered evidence.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly ControlledClock Clock = new();
        public readonly SessionJournal Journal;
        public readonly TextSource Source;
        public readonly FakeModel Model = new();
        public readonly ConversationEngine Engine;
        private readonly int seconds;

        public Fixture(int seconds, string name, bool reopen = false)
        {
            this.seconds = seconds;
            string path = reopen ? TestRunOutput.Current.Resolve(name) : TestRunOutput.Current.NewDirectory(name);
            Journal = new(path);
            var config = Journal.BindRollingContext(() => new(reopen ? 120 : seconds));
            Check.Is(config.Seconds == seconds, "Session n changed on reopen.");
            Source = new(Clock, Journal);
            Engine = new(Clock, Source, Model, Journal, rollingContext: config);
        }
        public Guid Text(int start, int end, AudioChannel channel, string text, bool pending = false)
            => Source.Add(start * 1000L, end * 1000L, channel, text, pending);
        public async Task Tick(int time)
        {
            Clock.Milliseconds = time * 1000L;
            Engine.Pulse();
            await Check.Idle(Engine);
        }
        public void Request(int index, OperationKind kind, int time, params string[] texts)
        {
            var request = Model.Requests[index];
            Check.Is(request.Kind == kind && request.BoundaryMs == time * 1000L && request.RollingContextSeconds == seconds
                && request.Segments.Select(s => s.Text).SequenceEqual(texts), "Incorrect request coverage.");
        }
        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private sealed class TextSource(ControlledClock clock, SessionJournal journal) : ITranscriptSource
    {
        private readonly List<TranscriptSegment> segments = journal.Transcript.ToList();
        private readonly TaskCompletionSource finalized = Check.Gate();
        public readonly TaskCompletionSource FinalizationEntered = Check.Gate();
        public CaptureStatus Status { get; private set; }
        public long CaptureBoundaryMs => clock.Milliseconds;
        public long FinalizedThroughMs => segments.Where(s => !s.IsFinal).Select(s => s.StartMs).Append(clock.Milliseconds).Min();
        public IReadOnlyList<TranscriptSegment> Segments => segments.ToArray();
        public void Start() => Status = CaptureStatus.Capturing;
        public void Pause() => Status = CaptureStatus.Paused;
        public void Resume() => Start();
        public void Stop() => Status = CaptureStatus.Stopped;
        public void Advance() { }
        public long SnapshotBoundary() => CaptureBoundaryMs;
        public Task WaitForFinalizationAsync(long boundaryMs, CancellationToken token)
        {
            if (FinalizedThroughMs >= boundaryMs) return Task.CompletedTask;
            FinalizationEntered.SetResult();
            return finalized.Task.WaitAsync(token);
        }
        public Guid Add(long start, long end, AudioChannel channel, string text, bool pending)
        {
            var item = new TranscriptSegment(Guid.NewGuid(), channel, start, end, DateTimeOffset.UnixEpoch.AddMilliseconds(start), text, !pending);
            segments.Add(item);
            if (!pending) journal.AppendTranscript(item);
            return item.Id;
        }
        public void Finalize(Guid id)
        {
            int index = segments.FindIndex(segment => segment.Id == id);
            var final = segments[index] with { IsFinal = true };
            journal.AppendTranscript(final);
            segments[index] = final;
            finalized.SetResult();
        }
    }

    private sealed class FakeModel : IDurableModelBackend
    {
        public readonly List<ProcessingRequest> Requests = [];
        public readonly TaskCompletionSource Entered = Check.Gate();
        public TaskCompletionSource? Hold;
        public bool Reject, Uncertain;
        public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken token)
        {
            Requests.Add(request);
            Entered.TrySetResult();
            if (Hold is { } gate) await gate.Task.WaitAsync(token);
            if (!Reject) return Result(request);
            Reject = false;
            return Result(request) with { State = request.Previous with { ProcessedThroughMs = -1 } };
        }
        public async Task<ProcessingResult> ProcessDurableAsync(ProcessingRequest request, ModelOperationVersions versions,
            Func<ModelOperationPhase, string?, Task> persist, CancellationToken token)
        {
            if (Uncertain)
            {
                Uncertain = false;
                Requests.Add(request);
                await persist(ModelOperationPhase.RequestSentUnknown, null);
                throw new IOException("Controlled uncertain outcome.");
            }
            return await ProcessAsync(request, token);
        }
        public static ProcessingResult Result(ProcessingRequest request) =>
            new(request.Previous with { ProcessedThroughMs = request.BoundaryMs }, request.Kind == OperationKind.Answer ? "answer" : null);
    }

    private sealed class CommitFault(SessionJournal journal, bool lostAck) : IConversationStore
    {
        private bool first = true;
        public StoreSnapshot Read() => journal.Read();
        public async Task CommitAsync(ProcessingCommit commit, CancellationToken token)
        {
            if (!first) { await journal.CommitAsync(commit, token); return; }
            first = false;
            if (lostAck) await journal.CommitAsync(commit, token);
            throw new IOException("Controlled save failure.");
        }
    }
}
