using System.Text.Json;
using SpeechAnalyzer.Core;

bool before = SpeechAnalyzer.Testing.TestRunOutput.Start(args, "before", "candidate") == "before";
var root = SpeechAnalyzer.Testing.TestRunOutput.Current.Root;
await Schedule(before, root);
Console.WriteLine(before ? "PASS pre-fix rejection strands C; cached retry does not invoke remote again" : "PASS valid A invalid B accepted C; boundaries and publication");
if (!before) await Extra.Run(root);

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static Task Idle(ConversationEngine engine) => engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
static async Task Schedule(bool before, string root)
{
    var source = new Source(); var journal = new SessionJournal(Path.Combine(root, "schedule"));
    var client = new Client(journal);
    await using var engine = new ConversationEngine(new Clock(), source, client.CreateBackend(), journal);
    var published = new HashSet<Guid>(); int added = 0;
    engine.Changed += () => { foreach (var message in journal.Read().Messages) if (published.Add(message.OperationId)) added++; };
    source.Add(journal, 1000, "A original", AudioChannel.You);
    Check(engine.Answer(), "A rejected"); await Idle(engine);
    Check(journal.Read().State.ProcessedThroughMs == 1000 && journal.Read().Messages.Count == 1, "A not committed");
    source.Add(journal, 2000, "B ambiguous original", AudioChannel.Other);
    Check(engine.Answer(), "B rejected"); await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    source.Add(journal, 3000, "C original", AudioChannel.You);
    Check(engine.Answer(), "C not accepted");
    var c = journal.ModelOperations.Single(o => o.Phase == ModelOperationPhase.Accepted);
    source.Add(journal, 4000, "FUTURE", AudioChannel.Other);
    byte[] originals = File.ReadAllBytes(Path.Combine(journal.DirectoryPath, "transcript.jsonl"));
    client.Release.SetResult(); await Idle(engine);
    var reopened = new SessionJournal(journal.DirectoryPath);
    Check(File.ReadAllBytes(Path.Combine(journal.DirectoryPath, "transcript.jsonl")).SequenceEqual(originals), "Original changed");
    if (before)
    {
        Check(client.Calls == 2 && engine.Snapshot().PendingOperations == 1 && !engine.Snapshot().IsBusy, "Expected stranded pending work");
        Check(reopened.ModelOperations.Single(o => o.Request.BoundaryMs == 2000).Phase == ModelOperationPhase.Prepared, "B not Prepared");
        Check(reopened.ModelOperations.Single(o => o.OperationId == c.OperationId).Phase == ModelOperationPhase.Accepted, "C lost");
        Check(engine.Snapshot().Error == "Неопределённая интерпретация не может закрыть вопрос.", "Wrong rejection branch");
        engine.Retry(); await Idle(engine);
        Check(client.Calls == 2 && engine.Snapshot().PendingOperations == 1, "Cached response retry changed outcome");
        Check(journal.Read().State.ProcessedThroughMs == 1000 && added == 1, "Invalid response published/advanced");
    }
    else
    {
        Check(client.Calls == 3 && engine.Snapshot().PendingOperations == 0 && !engine.Snapshot().IsBusy, "C stranded");
        Check(reopened.ModelOperations.Single(o => o.Request.BoundaryMs == 2000).Phase.ToString() == "ValidationRejected", "B not terminal rejected");
        var request = reopened.ModelOperations.Single(o => o.OperationId == c.OperationId).Request;
        Check(request.BoundaryMs == 3000 && request.Previous.ProcessedThroughMs == 1000, "Wrong C boundary");
        Check(request.Segments.Select(s => s.Text).SequenceEqual(new[] { "B ambiguous original", "C original" }), "Missing/unbounded evidence");
        Check(journal.Read().State.ProcessedThroughMs == 3000 && added == 2 && journal.Read().Messages.Count == 2, "Publication not exactly once");
        Check(journal.Read().State.Questions.Single().Status == QuestionStatus.Open, "Invalid B closed question");
        engine.Retry(); await Idle(engine); Check(client.Calls == 3, "Rejected remote turn repeated");
    }
    File.WriteAllText(Path.Combine(root, "schedule-summary.json"), JsonSerializer.Serialize(new {
        mode = before ? "before" : "after", client.Calls, added, engine.Snapshot().PendingOperations,
        processed = journal.Read().State.ProcessedThroughMs,
        operations = reopened.ModelOperations.Select(o => new { boundary = o.Request.BoundaryMs, phase = o.Phase.ToString() })
    }, new JsonSerializerOptions { WriteIndented = true }));
}

sealed class Clock : IClock { public TimeSpan Now => TimeSpan.Zero; public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
sealed class Source : ITranscriptSource
{
    private readonly List<TranscriptSegment> segments = [];
    public CaptureStatus Status { get; private set; } = CaptureStatus.Stopped;
    public long CaptureBoundaryMs { get; private set; }
    public long FinalizedThroughMs => CaptureBoundaryMs;
    public IReadOnlyList<TranscriptSegment> Segments => segments.ToArray();
    public void Start() => Status = CaptureStatus.Capturing;
    public void Stop() => Status = CaptureStatus.Stopped;
    public void Pause() => Status = CaptureStatus.Paused;
    public void Resume() => Status = CaptureStatus.Capturing;
    public void Advance() { }
    public long SnapshotBoundary() => CaptureBoundaryMs;
    public Task WaitForFinalizationAsync(long boundary, CancellationToken token) => Task.CompletedTask;
    public void Add(SessionJournal journal, long end, string text, AudioChannel channel)
    {
        var segment = new TranscriptSegment(Guid.NewGuid(), channel, CaptureBoundaryMs, end, DateTimeOffset.UnixEpoch, text, true);
        journal.AppendTranscript(segment); segments.Add(segment); CaptureBoundaryMs = end;
    }
    public void Restore(SessionJournal journal, long boundary) { segments.AddRange(journal.Transcript); CaptureBoundaryMs = boundary; }
}
sealed class Client(SessionJournal journal) : IRetrievalModelClient
{
    public ApplicationRetrievalBackend CreateBackend() => new(this, journal, newWorkflowVersion: 3);

    public int Calls;
    public bool Hold = true, TransportFailure;
    public long InvalidBoundary = 2000;
    public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<string> SendAsync(string prompt, JsonElement schema, string? effort,
        Func<ModelOperationPhase, string?, Task> phase, CancellationToken token)
    {
        Calls++;
        using var input = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
        var operation = journal.ModelOperations.Single(o => o.OperationId == input.RootElement.GetProperty("parentOperationId").GetGuid());
        var request = operation.Request;
        await phase(ModelOperationPhase.RequestSentUnknown, null); await phase(ModelOperationPhase.RemoteAccepted, "fake-" + Calls);
        if (TransportFailure) throw new IOException("Synthetic transport failure");
        bool invalid = request.BoundaryMs == InvalidBoundary;
        if (invalid && Hold) { Started.TrySetResult(); await Release.Task; }
        var quote = request.Segments.First();
        return JsonSerializer.Serialize(new { retrievalRequest = (object?)null, finalResult = new {
            processedThroughMs = request.BoundaryMs, facts = Array.Empty<string>(), commitments = Array.Empty<string>(),
            questions = new[] { new { id = "q", text = "Question", status = invalid ? "Displayed" : "Open" } },
            sourceReferences = Array.Empty<object>(),
            questionReferences = new[] { new { questionId = "q", sourceSegmentIds = new[] { quote.Id },
                evidence = new[] { new InterpretationQuote(quote.Id, quote.Text) } } },
            displayedQuestionIds = Array.Empty<string>(),
            outcome = "FullAnswer", summary = (string?)null, limitations = (string?)null, openQuestionIds = Array.Empty<string>(),
            displayText = invalid ? "INVALID MUST NOT DISPLAY" : "Valid answer " + request.BoundaryMs,
            interpretations = invalid ? new[] { new ContextualInterpretation("Possible meaning", "Ambiguous", [new(quote.Id, quote.Text)], [], "Unknown", true, ["q"]) } : []
        }});
    }
    public Task<string> ReadAsync(Guid id, string? remote, CancellationToken token) => throw new Exception("Unexpected remote read");
}

static class Extra
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static Task Idle(ConversationEngine engine) => engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    static void Pass(string name) => Console.WriteLine("PASS " + name);
    public static async Task Run(string root)
    {
        await NoPending(root);
        await Repeated(root);
        await LegacyRecovery(root);
        foreach (bool after in new[] { false, true }) await SaveFailure(root, after);
        await Transport(root);
        await Cancellation(root);
        await Stop(root);
        foreach (string phase in new[] { "Accepted", "Prepared", "ResultReceived", "ValidationRejected", "ReconciliationRequired" }) await Recovery(root, phase);
        foreach (bool after in new[] { false, true }) await RejectionWriteFailure(root, after);
    }
    sealed class Fixture : IAsyncDisposable
    {
        public readonly Source Source = new();
        public readonly SessionJournal Journal;
        public readonly Client Client;
        public readonly ConversationEngine Engine;
        public readonly SessionCoordinator Coordinator;
        public Fixture(string root, string name, Func<SessionJournal, IConversationStore>? wrap = null)
        {
            Journal = new(Path.Combine(root, name)); Client = new(Journal);
            Engine = new(new Clock(), Source, Client.CreateBackend(), wrap?.Invoke(Journal) ?? Journal);
            Coordinator = new(Engine, Source);
        }
        public void Add(long end) => Source.Add(Journal, end, "Original " + end, end % 2000 == 0 ? AudioChannel.Other : AudioChannel.You);
        public async ValueTask DisposeAsync() => await Coordinator.DisposeAsync();
    }
    static async Task NoPending(string root)
    {
        await using var f = new Fixture(root, "no-pending"); f.Client.Hold = false;
        f.Add(2000); Check(await f.Coordinator.AnswerAsync(), "B refused"); await Idle(f.Engine);
        var rejected = new SessionJournal(f.Journal.DirectoryPath).ModelOperations.Single();
        Check(rejected.Phase.ToString() == "ValidationRejected" && rejected.Result is not null, "Missing rejection payload/stage");
        Check(f.Journal.Read().State.ProcessedThroughMs == 0 && f.Journal.Read().Messages.Count == 0 && !f.Engine.Snapshot().CanRetry, "Invalid applied");
        await f.Coordinator.RetryAsync(); await Idle(f.Engine); Check(f.Client.Calls == 1, "Retry reinvoked rejection");
        f.Add(3000); Check(await f.Coordinator.AnswerAsync(), "New click refused"); await Idle(f.Engine);
        Check(f.Client.Calls == 2 && f.Journal.Read().Messages.Count == 1, "New request did not succeed");
        Check(f.Journal.ModelOperations.Last().Request.Segments.Count == 2, "Rejected evidence lost");
        Pass("invalid without pending; explicit Retry never reinvokes; later new click consumes original evidence");
    }
    static async Task Repeated(string root)
    {
        await using var f = new Fixture(root, "repeated");
        f.Add(2000); f.Engine.Answer(); await f.Client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Add(3000); f.Engine.Answer();
        for (int i = 0; i < 100; i++) { f.Add(3001 + i); Check(f.Engine.Answer(), "Coalesced click refused"); }
        Check(f.Engine.Snapshot().PendingOperations == 1 && f.Journal.ModelOperations.Count == 2, "Unbounded clicks");
        f.Client.Release.SetResult(); await Idle(f.Engine);
        Check(f.Client.Calls == 2 && f.Journal.Read().State.ProcessedThroughMs == 3000, "Coalescing changed accepted boundary");
        Check(f.Journal.ModelOperations.Last().Request.Segments.All(s => s.EndMs <= 3000), "Later evidence leaked");
        Pass("100 repeated clicks coalesce to first accepted boundary; no later evidence contamination");
    }
    static async Task LegacyRecovery(string root)
    {
        string source = Environment.GetEnvironmentVariable("REQUEST_PREFIX_JOURNAL")
            ?? Path.Combine(root, "..", "..", "before", "scenarios", "schedule");
        string target = Path.Combine(root, "legacy-recovery"); Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        { string dest = Path.Combine(target, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest); }
        var journal = new SessionJournal(target); var transcript = File.ReadAllBytes(Path.Combine(target, "transcript.jsonl"));
        var sourceAdapter = new Source(); sourceAdapter.Restore(journal, 4000); var client = new Client(journal) { Hold = false };
        await using var engine = new ConversationEngine(new Clock(), sourceAdapter, client.CreateBackend(), journal);
        engine.ResumeRecoveredOperations(); await Idle(engine);
        Check(client.Calls == 1 && journal.Read().State.ProcessedThroughMs == 3000 && journal.Read().Messages.Count == 2, "Old cached B/accepted C recovery failed");
        Check(journal.ModelOperations.Single(o => o.Request.BoundaryMs == 2000).Phase.ToString() == "ValidationRejected", "Legacy rejection missing");
        Check(File.ReadAllBytes(Path.Combine(target, "transcript.jsonl")).SequenceEqual(transcript), "Recovery altered transcript");
        Pass("restart from exact pre-fix synthetic journal: cached B validated locally; C proceeds once");
    }
    sealed class FailingStore(SessionJournal journal, bool after, bool rejection = false) : IConversationStore, IModelOperationStore
    {
        bool fail = true;
        public StoreSnapshot Read() => journal.Read();
        public IReadOnlyList<DurableModelOperation> ModelOperations => journal.ModelOperations;
        public void PrepareModelOperation(DurableModelOperation operation) => journal.PrepareModelOperation(operation);
        public void RecordSourceRead(Guid id, IReadOnlyList<Guid> ids) => journal.RecordSourceRead(id, ids);
        public void AdvanceModelOperation(Guid id, ModelOperationPhase phase, string? remote = null, ProcessingResult? result = null)
        {
            if (rejection && fail && phase.ToString() == "ValidationRejected")
            { fail = false; if (after) journal.AdvanceModelOperation(id, phase, remote, result); throw new IOException("Synthetic rejection write failure"); }
            journal.AdvanceModelOperation(id, phase, remote, result);
        }
        public async Task CommitAsync(ProcessingCommit commit, CancellationToken token)
        {
            if (!rejection && fail) { fail = false; if (after) await journal.CommitAsync(commit, token); throw new IOException("Synthetic save failure"); }
            await journal.CommitAsync(commit, token);
        }
    }
    static async Task SaveFailure(string root, bool after)
    {
        await using var f = new Fixture(root, "save-" + after, j => new FailingStore(j, after)); f.Client.Hold = false;
        f.Add(1000); f.Engine.Answer(); await Idle(f.Engine);
        Check(f.Client.Calls == 1 && f.Engine.Snapshot().CanRetry, "Save failure not retryable");
        Check(f.Journal.Read().State.ProcessedThroughMs == (after ? 1000 : 0), "Save boundary invalid");
        f.Engine.Retry(); await Idle(f.Engine); f.Engine.Retry(); await Idle(f.Engine);
        Check(f.Client.Calls == 1 && f.Journal.Read().Messages.Count == 1 && f.Journal.ModelOperations.Single().Phase == ModelOperationPhase.Completed, "Save retry duplicated");
        Pass("local save failure " + (after ? "after durable commit acknowledgment loss" : "before commit") + "; cached retry exactly once");
    }
    static async Task Transport(string root)
    {
        await using var f = new Fixture(root, "transport"); f.Client.TransportFailure = true;
        f.Add(1000); f.Engine.Answer(); await Idle(f.Engine);
        Check(f.Journal.ModelOperations.Single().Phase == ModelOperationPhase.ReconciliationRequired && !f.Engine.Snapshot().CanRetry, "Unknown turn retried");
        f.Engine.Retry(); await Idle(f.Engine); Check(f.Client.Calls == 1 && f.Journal.Read().State.ProcessedThroughMs == 0, "Transport failure applied");
        Check(!f.Engine.Answer(), "Reconciliation barrier bypassed");
        Pass("transport failure preserves unknown-outcome barrier and zero successful boundary");
    }
    static async Task Cancellation(string root)
    {
        await using var f = new Fixture(root, "cancel");
        f.Add(2000); f.Engine.Answer(); await f.Client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = f.Engine.DisposeAsync().AsTask(); f.Client.Release.SetResult(); await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Client.Calls == 1 && f.Journal.Read().Messages.Count == 0 && f.Journal.Read().State.ProcessedThroughMs == 0, "Late canceled result applied");
        var operation = f.Journal.ModelOperations.Single();
        Check(operation.Phase == ModelOperationPhase.Prepared && f.Journal.ReadRetrieval(operation.OperationId)?.Steps.Last().Response is not null,
            "Completed late response was not cached for local recovery");
        Pass("cancellation with token-ignoring late response cannot validate/commit/publish");
    }
    static async Task Stop(string root)
    {
        await using var f = new Fixture(root, "stop"); await f.Coordinator.StartAsync();
        f.Add(2000); f.Engine.Answer(); await f.Client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Add(3000); f.Engine.Answer(); await f.Coordinator.StopAsync();
        f.Client.Release.SetResult(); await Idle(f.Engine);
        Check(f.Coordinator.Phase == SessionPhase.Stopped && f.Journal.Read().Messages.Count == 1 && f.Journal.Read().State.ProcessedThroughMs == 3000, "Stop stranded accepted work");
        Pass("Stop retains accepted work; late invalid B rejected; accepted C completes once");
    }
    static ProcessingResult Invalid(ProcessingRequest r) => new(new(r.BoundaryMs, [], [], [new("q", "Question", QuestionStatus.Displayed)]), "INVALID",
        Interpretations: [new("Meaning", "Reason", [new(r.Segments[0].Id, r.Segments[0].Text)], [], "Unknown", true, ["q"])]);
    static async Task Recovery(string root, string phase)
    {
        await using var f = new Fixture(root, "recovery-" + phase); f.Client.Hold = false;
        f.Add(2000);
        var r = new ProcessingRequest(Guid.NewGuid(), OperationKind.Answer, 2000, ConversationState.Empty, f.Source.Segments, "", "", "");
        var versions = new ModelOperationVersions("c", "a", "s", null);
        var initial = phase == "Accepted" ? ModelOperationPhase.Accepted : ModelOperationPhase.Prepared;
        f.Journal.PrepareModelOperation(new(r.OperationId, r, versions, initial, null, null, DateTimeOffset.UnixEpoch));
        if (phase is "ResultReceived" or "ValidationRejected") f.Journal.AdvanceModelOperation(r.OperationId, Enum.Parse<ModelOperationPhase>(phase), result: Invalid(r));
        if (phase == "ReconciliationRequired") f.Journal.AdvanceModelOperation(r.OperationId, ModelOperationPhase.ReconciliationRequired);
        f.Add(3000);
        var c = r with { OperationId = Guid.NewGuid(), BoundaryMs = 3000, Segments = f.Source.Segments };
        f.Journal.PrepareModelOperation(new(c.OperationId, c, versions, ModelOperationPhase.Accepted, null, null, DateTimeOffset.UnixEpoch));
        var reopened = new SessionJournal(f.Journal.DirectoryPath); var client = new Client(reopened) { Hold = false };
        await using var engine = new ConversationEngine(new Clock(), f.Source, client.CreateBackend(), reopened);
        if (phase == "ReconciliationRequired") await engine.ReconcileAsync(r.OperationId, (_, _) => Task.FromResult(Invalid(r)));
        else engine.ResumeRecoveredOperations();
        await Idle(engine);
        Check(client.Calls == (phase is "Accepted" or "Prepared" ? 2 : 1), "Wrong recovery remote count: " + phase);
        Check(reopened.ModelOperations.First().Phase.ToString() == "ValidationRejected" && reopened.Read().State.ProcessedThroughMs == 3000 && reopened.Read().Messages.Count == 1, "Recovery failed: " + phase);
        Pass("recovery from " + phase + "; rejection skipped or validated once; C correct");
    }
    static async Task RejectionWriteFailure(string root, bool after)
    {
        await using var f = new Fixture(root, "reject-write-" + after, j => new FailingStore(j, after, true)); f.Client.Hold = false;
        f.Add(2000); f.Engine.Answer(); await Idle(f.Engine);
        Check(f.Engine.Snapshot().CanRetry && f.Client.Calls == 1 && f.Journal.Read().Messages.Count == 0, "Rejected write failure unsafe");
        f.Engine.Retry(); await Idle(f.Engine);
        Check(f.Client.Calls == 1 && f.Journal.ModelOperations.Single().Phase.ToString() == "ValidationRejected" && f.Journal.Read().State.ProcessedThroughMs == 0, "Rejected write retry resubmitted");
        Pass("rejection journal failure " + (after ? "after append" : "before append") + "; local retry never resends");
    }
}
