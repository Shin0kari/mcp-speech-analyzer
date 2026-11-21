using System.Text.Json;
using SpeechAnalyzer.Core;

internal static class TerraContractTests
{
    public static Task Usage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "speech-analyzer-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var observer = new CodexUsageObserver();
        observer.Bind("thread", true);
        var first = new TokenUsageScope(directory, Guid.NewGuid(), Guid.NewGuid(), OperationKind.Background, false);
        var second = first with { OperationId = Guid.NewGuid(), StepId = Guid.NewGuid(), Kind = OperationKind.Answer };
        var third = second with { StepId = Guid.NewGuid(), Retrieval = true };
        var budget = new ExperimentBudget(Path.Combine(directory, "budget"), new(3, 55, 10));
        budget.Reserve(first);
        observer.Begin();
        observer.Observe("thread/tokenUsage/updated", Snapshot("thread", "first", 10, 10));
        observer.Register("thread", "first", first);
        observer.Observe("thread/tokenUsage/updated", Snapshot("thread", "first", 10, 10));
        observer.Observe("thread/tokenUsage/updated", Snapshot("other", "first", 50, 40));
        Check(Read(first).Observed is { Total: 10, CachedInput: 1, ReasoningOutput: 1 });
        Reject(() => budget.Reserve(second));
        observer.Observe("turn/completed", Completed("first"));
        budget = new ExperimentBudget(Path.Combine(directory, "budget"), new(3, 55, 10));
        budget.Reserve(second);
        observer.Register("thread", "second", second);
        observer.Observe("turn/completed", Completed("second"));
        Reject(() => budget.Reserve(third));
        observer.Observe("thread/tokenUsage/updated", Snapshot("thread", "second", 20, 10));
        observer.Observe("thread/tokenUsage/updated", Snapshot("thread", "first", 10, 10));
        Check(Read(second).Observed?.Total == 10 && budget.Snapshot().ObservedTokens == 20);
        budget.Reserve(third);
        observer.Register("thread", "third", third);
        observer.Observe("thread/tokenUsage/updated", Snapshot("thread", "third", 70, 50));
        observer.Observe("turn/completed", Completed("third"));
        Check(budget.Snapshot() is { Calls: 3, ObservedTokens: 70, ObservedOvershoot: 15 });
        Reject(() => budget.Reserve(third with { StepId = Guid.NewGuid() }));
        var resumed = new CodexUsageObserver();
        resumed.Bind("thread", false);
        var afterRestart = third with { StepId = Guid.NewGuid() };
        resumed.Register("thread", "after-restart", afterRestart);
        resumed.Observe("thread/tokenUsage/updated", Snapshot("thread", "after-restart", 80, 10));
        Check(Read(afterRestart) is { Ambiguous: true, Observed: null });
        Check(Read(first).Observed?.Total == 10);
        return Task.CompletedTask;
    }

    public static Task Outcomes()
    {
        var segment = new TranscriptSegment(Guid.NewGuid(), AudioChannel.Other, 0, 100, DateTimeOffset.UtcNow,
            "The deployment succeeded. Delivery date unclear.", true);
        var question = new TrackedQuestion("q", "When is delivery?", QuestionStatus.Open);
        var previous = ConversationState.Empty with { Questions = [question] };
        var request = new ProcessingRequest(Guid.NewGuid(), OperationKind.Background, 100, previous, [segment], "", "", "");
        var state = previous with { ProcessedThroughMs = 100 };
        var summary = new ProcessingResult(state, null) { Outcome = ResultOutcome.Summary, Summary = "Deployment discussed" };
        InterpretationValidation.Validate(request with { Previous = ConversationState.Empty },
            summary with { State = state with { Questions = [] } }, [segment]);
        InterpretationValidation.Validate(request, new(state, null) { Outcome = ResultOutcome.NoChange }, [segment]);
        var interpretation = new ContextualInterpretation("Date uncertain", "Ambiguous", [new(segment.Id, "Delivery date unclear.")],
            [], "No agreed date", true, ["q"]);
        var fact = new AnalysisSourceReference("f", "Deployment succeeded", [segment.Id])
            { Evidence = [new(segment.Id, "The deployment succeeded.")] };
        var partial = new ProcessingResult(state with { Facts = [fact.Text] }, "Deployment succeeded", [fact], [], [], [interpretation])
            { Outcome = ResultOutcome.PartialAnswer, Limitations = "Delivery date unknown.", OpenQuestionIds = ["q"] };
        var answer = request with { Kind = OperationKind.Answer };
        InterpretationValidation.Validate(answer, partial, [segment]);
        InterpretationValidation.Validate(answer, partial with { Outcome = ResultOutcome.Clarification, DisplayText = "When is delivery?" }, [segment]);
        Reject(() => InterpretationValidation.Validate(request, partial, [segment]));
        Reject(() => InterpretationValidation.Validate(answer, partial with { SourceReferences = [fact with
            { Evidence = [new(segment.Id, "Delivery date unclear.")] }] }, [segment]));
        Reject(() => InterpretationValidation.Validate(answer, partial with { SourceReferences = [fact with
            { Evidence = [new(segment.Id, "The deployment failed.")] }] }, [segment]));
        Reject(() => InterpretationValidation.Validate(answer, partial, [segment with { EndMs = 101 }]));
        Reject(() => InterpretationValidation.Validate(answer, partial with
        {
            State = partial.State with { Questions = [question with { Status = QuestionStatus.Displayed }] }
        }, [segment]));
        return Task.CompletedTask;
    }

    public static async Task ClarificationPublication()
    {
        var clock = new ManualClock();
        var source = new SimulatedTranscriptSource(clock) { DemoSpeechEnabled = false, FinalizationDelay = TimeSpan.Zero };
        var store = new MemoryConversationStore();
        var model = new ClarifyingModel();
        await using var engine = new ConversationEngine(clock, source, model, store);
        engine.SetMode(AnalysisMode.Automatic);
        engine.Start();
        for (int index = 0; index < 3; index++)
        {
            source.Inject(index == 1 ? "Unrelated topic" : "Delivery date evidence", AudioChannel.Other, SegmentMeaning.Fact, null);
            clock.Advance(5);
            engine.Pulse();
            await engine.WhenIdleAsync();
            Check(store.Read().Messages.Count == (index == 2 ? 2 : 1));
        }
        source.Inject("Another unrelated topic", AudioChannel.Other, SegmentMeaning.Fact, null);
        clock.Advance(5);
        engine.Pulse();
        await engine.WhenIdleAsync();
        Check(store.Read().Messages.Count == 2 && store.Read().State.Questions.Single().Status == QuestionStatus.Open);
        Check(engine.Answer());
        await engine.WhenIdleAsync();
        Check(store.Read().Messages.Count == 3);
    }

    private sealed class ClarifyingModel : IModelBackend
    {
        private int calls;
        public Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken cancellationToken)
        {
            var question = new TrackedQuestion("q", "When is delivery?", QuestionStatus.Open);
            var references = request.Segments.Where(segment => segment.Text == "Delivery date evidence")
                .Select(segment => new QuestionSourceReference(question.Id, [segment.Id])
                    { Evidence = [new(segment.Id, segment.Text)] }).ToArray();
            return Task.FromResult(new ProcessingResult(
                request.Previous with { ProcessedThroughMs = request.BoundaryMs, Questions = [question] },
                "Please clarify the date " + ++calls, QuestionReferences: references)
            {
                Outcome = ResultOutcome.Clarification, Limitations = "No confirmed delivery date.", OpenQuestionIds = [question.Id]
            });
        }
    }

    private static TurnUsageReceipt Read(TokenUsageScope scope) => JsonSerializer.Deserialize<TurnUsageReceipt>(
        File.ReadAllText(Path.Combine(scope.Directory, "token-usage-v1", scope.StepId.ToString("N") + ".json")))!;

    private static JsonElement Snapshot(string threadId, string turnId, long total, long last) =>
        JsonSerializer.SerializeToElement(new { threadId, turnId, tokenUsage = new
        {
            total = Counts(total), last = Counts(last), modelContextWindow = 100_000
        } });

    private static object Counts(long total) => new
    {
        inputTokens = total * 8 / 10, cachedInputTokens = total / 10, outputTokens = total * 2 / 10,
        reasoningOutputTokens = total / 10, totalTokens = total
    };

    private static JsonElement Completed(string id) =>
        JsonSerializer.SerializeToElement(new { threadId = "thread", turn = new { id, status = "completed" } });

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new Exception("Expected rejection.");
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Unexpected usage receipt.");
    }
}
