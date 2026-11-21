using System.Security.Cryptography;
using System.Text.Json;
using SpeechAnalyzer.Core;
using SpeechAnalyzer.Testing;

if (args.Length != 5 || Hash(typeof(SessionJournal).Assembly.Location) != args[3] ||
    args[4].Length != 40 || !args[4].All(Uri.IsHexDigit))
    throw new ArgumentException("Expected offline-no-audio --output-root <fresh directory> <Core SHA-256> <source commit>.");
TestRunOutput.Start(args[..3], "offline-no-audio");
var passed = new List<string>();
var versions = new ModelOperationVersions("context", "attachments", "settings", "low");

foreach (var item in new (string Label, string Instruction, string? Channel, AudioChannel[] Expected)[]
{
    ("other", "Какой код назвали в приложении?", "Other", [AudioChannel.Other, AudioChannel.Other]),
    ("you", "Какой код я назвал в микрофон?", "You", [AudioChannel.You]),
    ("both", "Сравни коды из двух каналов.", null, [AudioChannel.You, AudioChannel.Other, AudioChannel.Other]),
    ("absent", "В приложении называли код «Север»?", "Other", [])
})
{
    var (journal, request, _, other) = Fixture(item.Label, item.Instruction, spoken: item.Label == "other");
    string search = item.Label == "absent" ? "Север" : "Маркер:";
    var client = new ScriptedClient(Query(request, item.Channel, search),
        item.Label == "other" ? Clarify(request, other) : Unusable(request));
    var result = await Process(ApplicationRetrievalBackend.CreateDesktop(client, journal), journal, request);
    var saved = journal.ReadRetrieval(request.OperationId)!;
    var batch = saved.Batches.Single();
    Check(batch.Passages.Select(p => p.Segment.Channel).SequenceEqual(item.Expected) &&
        batch.Passages.All(p => p.Segment.EndMs < request.Previous.ProcessedThroughMs) &&
        batch.Status == (item.Expected.Length == 0 ? "no_matches" : "complete"), item.Label);
    Check(client.Prompts[0].Contains("You — источник речи пользователя") &&
        client.Prompts[0].Contains("канал не устанавливает личность") &&
        client.Prompts[0].Contains("даже если userInstruction пуст") &&
        client.Prompts[0].Contains("Если выбор источника меняет ответ") &&
        client.Prompts[0].Contains("Не подменяй слова запрошенного канала") &&
        client.Schemas[0].GetProperty("properties").GetProperty("retrievalRequest")
            .GetProperty("properties").GetProperty("channel").GetProperty("enum").GetRawText() ==
                """["You","Other",null]""", "channel guidance and schema");
    if (item.Label == "other")
    {
        Check(request.UserInstruction == "" && request.Segments.Single().Channel == AudioChannel.You &&
            request.Segments.Single().Text == item.Instruction, "spoken request and target source differ");
        using var payload = JsonDocument.Parse(client.Prompts[1].Split('\n').Last());
        var supplied = payload.RootElement.GetProperty("suppliedOriginals")[0].GetProperty("Passages")[0].GetProperty("Segment");
        Check(supplied.GetProperty("Channel").GetString() == "Other" && supplied.GetProperty("StartMs").GetInt64() == other.StartMs &&
            supplied.GetProperty("EndMs").GetInt64() == other.EndMs &&
            string.Concat(supplied.GetProperty("Text").Deserialize<string[]>()!) == other.Text &&
            batch.Passages[0].Sha256 == OperationVersionHash.Of(other.Text), "original metadata");
        var evidence = result.QuestionReferences!.Single().Evidence!.Single();
        Check(result.ResultContractVersion == 5 && evidence.FragmentId == 3 && evidence.SourceSegmentId == other.Id &&
            evidence.Text == " Нужна сверка?", "v5 evidence");
        var reopened = new SessionJournal(journal.DirectoryPath);
        var noRemote = new ScriptedClient();
        var cached = await Process(ApplicationRetrievalBackend.CreateDesktop(noRemote, reopened), reopened, request);
        Check(saved.Version == ApplicationRetrievalBackend.SpokenChannelWorkflowVersion && noRemote.Prompts.Count == 0 &&
            JsonSerializer.Serialize(result) == JsonSerializer.Serialize(cached), "cached v8 replay");
        Pass("retrieved Other fragment preserves source metadata and replays both cached turns");
    }
    Pass("deterministic source and cutoff filter: " + item.Label);
}
{
    var (journal, request, _, other) = Fixture("deferred-normal", "Что было слышно в приложении?");
    var client = new ScriptedClient(Query(request, "Other", "Маркер:"), Clarify(request, other))
    {
        BeforeSend = () => throw new IOException("Synthetic transport unavailable.")
    };
    var source = new IdleSource(journal.Transcript, request.BoundaryMs);
    var clock = new TestClock();
    await using var engine = new ConversationEngine(clock, source, ApplicationRetrievalBackend.CreateDesktop(client, journal), journal,
        deferRecoveredOperations: true);
    engine.ResumeRecoveredOperations();
    await engine.WhenIdleAsync();
    Check(source.Starts == 0 && client.Prompts.Count == 0, "normal reopen is idle");
    engine.Retry();
    await engine.WhenIdleAsync();
    Check(client.Prompts.Count == 1 && engine.Snapshot().Error?.StartsWith("Локальная ошибка хранения или связи") == true &&
        journal.Read().State.ProcessedThroughMs == 0, "local error leaves the boundary intact");
    engine.Start();
    for (int i = 0; i < 3; i++)
    {
        clock.Now += TimeSpan.FromMinutes(1);
        engine.Pulse();
    }
    Check(!engine.Answer("New request") && client.Prompts.Count == 1, "timer and new click do not retry failed work");
    client.BeforeSend = null;
    engine.Retry();
    await engine.WhenIdleAsync();
    Check(client.Sends == 2 && journal.ModelOperations.Single().Phase == ModelOperationPhase.Completed &&
        journal.Read().Messages.Count == 1, "explicit retry resumes the known-unsent operation");
    Pass("normal reopen is idle and known-unsent transport failure requires an explicit retry");
}

{
    var (journal, request, you, _) = Fixture("id-conflict", "Что говорили в приложении?");
    var client = new ScriptedClient(Query(request, "Other", "", you.Id), Unusable(request));
    await Process(ApplicationRetrievalBackend.CreateDesktop(client, journal), journal, request);
    Check(journal.ReadRetrieval(request.OperationId)!.Batches.Single() is { Status: "no_matches", Passages.Count: 0 },
        "a microphone segment ID cannot cross an Other filter");
    Pass("conflicting source and exact ID yields no matches");
}
{
    var (journal, request, _, _) = Fixture("ambiguous", "Какой код они имели в виду?");
    var client = new ScriptedClient(Clarify(request, request.Segments.Single(), 0, "Уточните, о каком источнике речь."));
    var result = await Process(ApplicationRetrievalBackend.CreateDesktop(client, journal), journal, request);
    Check(client.Sends == 1 && journal.ReadRetrieval(request.OperationId)!.Batches.Count == 0 &&
        result.DisplayText == "Уточните, о каком источнике речь.", "ambiguous model can ask");
    Pass("scripted ambiguity asks for clarification without assigning a channel");
}
{
    var (journal, request, _, _) = Fixture("round-limit", "Какой код назвали?");
    string query = Query(request, "Other", "Код");
    var client = new ScriptedClient(query, query, query);
    var source = new IdleSource(journal.Transcript, request.BoundaryMs);
    await using var engine = new ConversationEngine(new TestClock(), source, ApplicationRetrievalBackend.CreateDesktop(client, journal), journal);
    engine.ResumeRecoveredOperations();
    await engine.WhenIdleAsync();
    Check(client.Sends == 3 && journal.ReadRetrieval(request.OperationId)!.Batches.Count == 2 &&
        journal.ModelOperations.Single().Phase == ModelOperationPhase.ValidationRejected &&
        journal.Read().State.ProcessedThroughMs == 0 && !engine.Snapshot().CanRetry, "retrieval limit is terminal");
    engine.Retry();
    await engine.WhenIdleAsync();
    Check(client.Sends == 3, "terminal retrieval cannot be resent");
    Pass("retrieval is bounded with no durable progress or automatic retry");
}
{
    var (journal, request, you, _) = Fixture("historical", "Which source mentioned a code?");
    string oldQuery = JsonSerializer.Serialize(new
    {
        retrievalRequest = new { operationId = request.OperationId, text = "Север", segmentId = you.Id },
        finalResult = (object?)null
    });
    var historical = new ScriptedClient(oldQuery);
    try
    {
        await Process(new(historical, journal, ApplicationRetrievalBackend.AttributionWorkflowVersion), journal, request);
        throw new Exception("The historical continuation was expected to stop.");
    }
    catch (ExperimentAdmissionException error) when (error.Reason == ExperimentAdmissionRefusal.TokenCeiling) { }
    string workflowPath = Path.Combine(journal.DirectoryPath, "retrieval", request.OperationId.ToString("N") + ".json");
    string originalHash = Hash(workflowPath);
    var reopened = new SessionJournal(journal.DirectoryPath);
    var client = new ScriptedClient();
    var source = new IdleSource(reopened.Transcript, request.BoundaryMs);
    var clock = new TestClock();
    await using var engine = new ConversationEngine(clock, source, ApplicationRetrievalBackend.CreateDesktop(client, reopened), reopened,
        deferRecoveredOperations: true);
    engine.ResumeRecoveredOperations();
    clock.Now = TimeSpan.FromMinutes(5);
    engine.Pulse();
    await engine.WhenIdleAsync();
    Check(source.Starts == 0 && client.Prompts.Count == 0 && engine.Snapshot().CanRetry && Hash(workflowPath) == originalHash,
        "reopen is idle and keeps the historical workflow");
    engine.Retry();
    await engine.WhenIdleAsync();
    Check(client.Prompts.Count == 0 && Hash(workflowPath) == originalHash && reopened.ModelOperations.Single().Phase == ModelOperationPhase.Prepared &&
        engine.Snapshot().CanAbandonFailedRequest && reopened.Read().State.ProcessedThroughMs == 0, "legacy step stays frozen even on retry");
    Pass("headless reopen and explicit retry preserve the frozen experimental continuation");
}
{
    var folder = TestRunOutput.Current.NewDirectory("observations");
    var observer = new CodexUsageObserver();
    observer.Bind("synthetic-thread", true);
    int cumulative = 0;
    var turnIds = new HashSet<string>();
    var scopes = new List<TokenUsageScope>();
    var (journal, first, _, other) = Fixture("observed", "Что было слышно в приложении?");
    for (int i = 1; i <= 8; i++)
    {
        var request = i == 1 ? first : first with { OperationId = Guid.NewGuid(), Previous = journal.Read().State };
        if (i > 1)
            journal.PrepareModelOperation(new(request.OperationId, request, versions,
                ModelOperationPhase.Prepared, null, null, DateTimeOffset.UnixEpoch));
        var client = new ScriptedClient(Query(request, "Other", "Маркер:"), Clarify(request, other));
        client.OnSend = (scope, _) =>
        {
            int index = scopes.Count + 1;
            string id = $"turn-{index}";
            Check(turnIds.Add(id), "unique turn ID");
            scopes.Add(scope);
            observer.Begin();
            observer.Register("synthetic-thread", id, scope);
            observer.Observe("turn/completed", JsonSerializer.SerializeToElement(new
                { threadId = "synthetic-thread", turn = new { id, status = "completed" } }));
            if (index != 15)
            {
                cumulative += 25_000;
                var updated = Usage(id, cumulative, 25_000);
                observer.Observe("thread/tokenUsage/updated", updated);
                observer.Observe("thread/tokenUsage/updated", updated);
            }
            observer.End();
        };
        var source = new IdleSource(journal.Transcript, request.BoundaryMs);
        await using var engine = new ConversationEngine(new TestClock(), source,
            ApplicationRetrievalBackend.CreateDesktop(client, journal), journal);
        engine.ResumeRecoveredOperations();
        await engine.WhenIdleAsync();
        Check(journal.Read().State.ProcessedThroughMs == request.BoundaryMs && journal.Read().Messages.Count == i &&
            journal.ModelOperations.Last().Phase == ModelOperationPhase.Completed, "valid final result is durable");
        Check(client.Sends == 2 && client.Scopes[0].OperationId == client.Scopes[1].OperationId &&
            !client.Scopes[0].Retrieval && client.Scopes[1].Retrieval, "per-step observational identity");
    }
    var receipts = scopes.Select(ReadReceipt).ToArray();
    Check(receipts.Length == 16 && receipts[14] is { Observed: null, Completeness: "unavailable", TurnStatus: "completed" } &&
        receipts.Where((_, i) => i != 14).All(r => r is { Observed.Total: 25_000, Completeness: "provisional" }) &&
        receipts.Sum(r => r.Observed?.Total) == 375_000 && receipts.Select(r => r.StepId).Distinct().Count() == 16,
        "observations are deduplicated, missing stays unknown, and receipts do not gate later steps");
    Check(scopes.All(scope => !Directory.Exists(Path.Combine(scope.Directory, "budget"))), "desktop started no budget ledger");
    Pass("16 simulated starts include over-ceiling and missing-usage continuations with accurate receipts");

    foreach (var reason in Enum.GetValues<ExperimentAdmissionRefusal>())
    {
        var scope = new TokenUsageScope(folder, Guid.NewGuid(), Guid.NewGuid(), OperationKind.Answer, false);
        var budget = new ExperimentBudget(Path.Combine(folder, reason.ToString()),
            new(reason == ExperimentAdmissionRefusal.CallCap ? 1 : 6, 150_000, 30_000));
        budget.Reserve(scope);
        if (reason == ExperimentAdmissionRefusal.TokenCeiling)
        {
            var experimentObserver = new CodexUsageObserver();
            experimentObserver.Bind("synthetic-thread", true);
            experimentObserver.Register("synthetic-thread", "exp", scope);
            experimentObserver.Observe("turn/completed", JsonSerializer.SerializeToElement(new
                { threadId = "synthetic-thread", turn = new { id = "exp", status = "completed" } }));
            experimentObserver.Observe("thread/tokenUsage/updated", Usage("exp", 125_000, 125_000));
        }
        try
        {
            budget.Reserve(scope with { StepId = Guid.NewGuid() });
            throw new Exception("Explicit experiment did not enforce its limits.");
        }
        catch (ExperimentAdmissionException error) when (error.Reason == reason)
        {
            Check(budget.Snapshot().Calls == 1, "refusal did not create an admission");
            Pass("explicit experiment refusal: " + reason);
        }
    }
}

TestRunOutput.Current.WriteNew("receipt.json", JsonSerializer.Serialize(new
{
    sourceCommit = args[4], coreSha256 = Hash(typeof(SessionJournal).Assembly.Location),
    runnerSha256 = Hash(typeof(Program).Assembly.Location), runtimeSha256 = Hash(typeof(object).Assembly.Location),
    passed, realAudio = false, realAsr = false, realInference = false, guiStarted = false
}));

(SessionJournal Journal, ProcessingRequest Request, TranscriptSegment You, TranscriptSegment Other)
    Fixture(string label, string instruction, bool spoken = false)
{
    var journal = new SessionJournal(TestRunOutput.Current.NewDirectory(label));
    var you = new TranscriptSegment(Guid.NewGuid(), AudioChannel.You, 0, 1000, DateTimeOffset.UnixEpoch, "Маркер: Север.", true);
    var other = you with { Id = Guid.NewGuid(), Channel = AudioChannel.Other, StartMs = 1000, EndMs = 2000,
        Text = "Маркер: Восток. Нужна сверка?" };
    var anotherOther = other with { Id = Guid.NewGuid(), StartMs = 2000, EndMs = 3000,
        Text = "Маркер: Восток, подтверждаю." };
    var current = you with { Id = Guid.NewGuid(), StartMs = 4000, EndMs = 5000, Text = instruction };
    var late = other with { Id = Guid.NewGuid(), StartMs = 5000, EndMs = 6000, Text = "Маркер: Север, поправка." };
    foreach (var segment in new[] { you, other, anotherOther, current, late }) journal.AppendTranscript(segment);
    var request = new ProcessingRequest(Guid.NewGuid(), OperationKind.Answer, 5000,
        ConversationState.Empty with { ProcessedThroughMs = 4000 }, [current], spoken ? "" : instruction, "", "");
    journal.PrepareModelOperation(new(request.OperationId, request, versions, ModelOperationPhase.Prepared, null, null, DateTimeOffset.UnixEpoch));
    return (journal, request, you, other);
}

Task<ProcessingResult> Process(ApplicationRetrievalBackend backend, SessionJournal journal, ProcessingRequest request) =>
    backend.ProcessDurableAsync(request, versions, (phase, turn) =>
    {
        if (journal.ModelOperations.Single().Phase != phase) journal.AdvanceModelOperation(request.OperationId, phase, turn);
        return Task.CompletedTask;
    }, default);

static string Query(ProcessingRequest request, string? channel, string text, Guid? id = null) => JsonSerializer.Serialize(new
{
    retrievalRequest = new { operationId = request.OperationId, text, segmentId = id, channel },
    finalResult = (object?)null
});

static string Unusable(ProcessingRequest request) => Final(new
{
    processedThroughMs = request.BoundaryMs, summary = (string?)null, facts = Array.Empty<string>(), commitments = Array.Empty<string>(),
    questions = Array.Empty<string>(), displayText = (string?)null, outcome = "Unusable", limitations = "Insufficient evidence.",
    questionReferences = Array.Empty<string>(), interpretations = Array.Empty<string>(), sourceReferences = Array.Empty<string>(),
    displayedQuestionIds = Array.Empty<string>(), openQuestionIds = Array.Empty<string>()
});

static string Clarify(ProcessingRequest request, TranscriptSegment source, int fragment = 3,
    string display = "Уточните, какую сверку имели в виду.") => Final(new
{
    processedThroughMs = request.BoundaryMs, summary = (string?)null, facts = Array.Empty<string>(), commitments = Array.Empty<string>(),
    questions = new[] { new { id = "review", text = "Which source or review?", status = "Open" } },
    displayText = display, outcome = "Clarification", limitations = "The topic is unclear.",
    interpretations = Array.Empty<string>(), sourceReferences = Array.Empty<string>(), displayedQuestionIds = Array.Empty<string>(),
    openQuestionIds = new[] { "review" },
    questionReferences = new[] { new { questionId = "review", sourceSegmentIds = new[] { source.Id },
        evidence = new[] { new { sourceSegmentId = source.Id, fragmentId = fragment } } } }
});

static string Final(object result) => JsonSerializer.Serialize(new { retrievalRequest = (object?)null, finalResult = result });
static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
static TurnUsageReceipt ReadReceipt(TokenUsageScope scope) => JsonSerializer.Deserialize<TurnUsageReceipt>(
    File.ReadAllText(Path.Combine(scope.Directory, "token-usage-v1", scope.StepId.ToString("N") + ".json")))!;
static JsonElement Usage(string id, int total, int last)
{
    static object Counts(int amount) => new
    {
        inputTokens = amount - 100, cachedInputTokens = 0, outputTokens = 100, reasoningOutputTokens = 0, totalTokens = amount
    };
    // Cumulative output grows by 100 for each synthetic response.
    int output = total / last * 100;
    return JsonSerializer.SerializeToElement(new
    {
        threadId = "synthetic-thread", turnId = id, tokenUsage = new { last = Counts(last),
            total = new { inputTokens = total - output, cachedInputTokens = 0, outputTokens = output, reasoningOutputTokens = 0, totalTokens = total } }
    });
}

void Pass(string name) { passed.Add(name); Console.WriteLine("PASS " + name); }
static void Check(bool value, string subject) { if (!value) throw new InvalidDataException(subject); }

sealed class ScriptedClient(params string[] responses) : IRetrievalModelClient
{
    public int Sends { get; private set; }
    public List<string> Prompts { get; } = [];
    public List<JsonElement> Schemas { get; } = [];
    public List<TokenUsageScope> Scopes { get; } = [];
    public Action<TokenUsageScope, int>? OnSend { get; set; }
    public Action? BeforeSend { get; set; }
    public Task<string> SendAsync(string prompt, JsonElement schema, string? effort,
        Func<ModelOperationPhase, string?, Task> phase, CancellationToken token)
        => throw new InvalidOperationException("Expected an observational scope.");
    public async Task<string> SendAsync(string prompt, JsonElement schema, string? effort,
        Func<ModelOperationPhase, string?, Task> phase, CancellationToken token, TokenUsageScope? usage)
    {
        Prompts.Add(prompt);
        Schemas.Add(schema);
        Scopes.Add(usage ?? throw new InvalidOperationException("Missing turn identity."));
        BeforeSend?.Invoke();
        if (Sends == responses.Length) throw new ExperimentAdmissionException(ExperimentAdmissionRefusal.TokenCeiling);
        string id = $"scripted-{++Sends}";
        await phase(ModelOperationPhase.RequestSentUnknown, null);
        await phase(ModelOperationPhase.RemoteAccepted, id);
        OnSend?.Invoke(usage, Sends);
        return responses[Sends - 1];
    }
    public Task<string> ReadAsync(Guid step, string? remote, CancellationToken token)
        => throw new InvalidOperationException("Unexpected remote read.");
}

sealed class TestClock : IClock
{
    public TimeSpan Now { get; set; }
    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Now;
}

sealed class IdleSource(IReadOnlyList<TranscriptSegment> segments, long boundary) : ITranscriptSource
{
    public int Starts { get; private set; }
    public CaptureStatus Status { get; private set; }
    public long CaptureBoundaryMs => boundary;
    public long FinalizedThroughMs => boundary;
    public IReadOnlyList<TranscriptSegment> Segments => segments;
    public void Start() { Starts++; Status = CaptureStatus.Capturing; }
    public void Pause() { }
    public void Resume() { }
    public void Stop() => Status = CaptureStatus.Stopped;
    public void Advance() { }
    public long SnapshotBoundary() => boundary;
    public Task WaitForFinalizationAsync(long boundaryMs, CancellationToken token) => Task.CompletedTask;
}
