using System.Text.Json;
using SpeechAnalyzer.Core;

internal static class InterpretationTests
{
    private static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-000000000061");
    private static TranscriptSegment Segment() => new(Id, AudioChannel.Other, 0, 1000, DateTimeOffset.UnixEpoch,
        "Мы оплачиваем — пока не подтверждаем.", true, Recognition: new(0, 1000, "uncertain",
            [new(0, 1000, "Мы оплату пока не подтверждаем.")]));
    private static ProcessingRequest Request() => new(Guid.NewGuid(), OperationKind.Answer, 1000,
        ConversationState.Empty, [Segment()], "", "Уточни неясность.", "Решение об оплате ещё обсуждается.");
    private static ProcessingResult Result() => new(new(1000, [], [], [new("payment", "Оплата согласована?", QuestionStatus.Open)]),
        "Вероятно, оплату ещё не подтверждают. Уточните решение.", Interpretations:
        [new("Оплату, вероятно, не подтверждают.", "Контекст и распознанное отрицание.", [new(Id, "пока не подтверждаем")],
            ["Начало фразы могло быть распознано неверно."], "Неясно, подтверждена ли оплата.", true, ["payment"])]);
    private static void Check(bool value) { if (!value) throw new Exception("Нарушен контракт интерпретации."); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Неверный результат принят."); }

    public static Task Validation()
    {
        var request = Request(); var result = Result(); var item = result.Interpretations!.Single();
        void Validate(ProcessingResult value) => InterpretationValidation.Validate(request, value, request.Segments);
        Validate(result);
        Reject(() => Validate(result with { Interpretations = [item with { Evidence = [new(Guid.NewGuid(), "пока не подтверждаем")] }] }));
        Reject(() => Validate(result with { Interpretations = [item with { Evidence = [new(Id, "Мы оплату пока не подтверждаем.")] }] }));
        Reject(() => Validate(result with { Interpretations = [item with { Uncertainty = "" }] }));
        Reject(() => Validate(result with { State = result.State with { Questions = [new("payment", "Оплата?", QuestionStatus.Displayed)] } }));
        Reject(() => Validate(result with { DisplayedQuestionIds = ["payment"] }));
        Reject(() => Validate(result with { State = result.State with { Commitments = ["Оплата согласована"] } }));
        Reject(() => Validate(result with { State = result.State with { Facts = ["Оплата согласована"] } }));
        Reject(() => Validate(result with { Interpretations = [item with { AffectedQuestionIds = [] }],
            QuestionReferences = [new("payment", [Id])], State = result.State with { Questions = [new("payment", "Оплата?", QuestionStatus.AnsweredByParticipant)] } }));
        Reject(() => InterpretationValidation.Validate(request with { BoundaryMs = 999 }, result, request.Segments));
        var original = Segment(); InterpretationValidation.ValidateRecognition(original);
        Reject(() => InterpretationValidation.ValidateRecognition(original with { Recognition = original.Recognition! with { WindowEndMs = 9000 } }));
        Reject(() => CodexModelBackend.BuildPrompt(request with { Segments = [original with { Recognition = original.Recognition! with {
            Alternatives = [new(0, 2000, "Текст из пересекающего срез задания.")] } }] }, new("c", "a", "m", "medium")));
        Check(original.Text == "Мы оплачиваем — пока не подтверждаем.");
        string prompt = CodexModelBackend.BuildPrompt(request, new("c", "a", "m", "medium"));
        Check(prompt.Contains("recognition") && prompt.Contains("interpretations") && prompt.Contains("unresolved=true"));
        var parsed = CodexModelBackend.ParseResult(request, JsonSerializer.Serialize(new {
            processedThroughMs = 1000, facts = Array.Empty<string>(), commitments = Array.Empty<string>(),
            questions = new[] { new { id = "payment", text = "Оплата?", status = "Open" } }, sourceReferences = Array.Empty<string>(),
            questionReferences = Array.Empty<string>(), displayedQuestionIds = Array.Empty<string>(), displayText = result.DisplayText,
            interpretations = result.Interpretations }));
        Validate(parsed);
        return Task.CompletedTask;
    }

    public static async Task PersistenceAndRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "speech-interpretation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var request = Request(); var result = Result(); var journal = new SessionJournal(directory);
            journal.AppendTranscript(Segment());
            journal.AppendTranscript(JsonSerializer.Deserialize<TranscriptSegment>(JsonSerializer.Serialize(Segment()))!);
            Check(journal.Transcript.Count == 1);
            var versions = new ModelOperationVersions("c", "a", "m", "medium");
            journal.PrepareModelOperation(new(request.OperationId, request, versions,
                ModelOperationPhase.Prepared, null, null, DateTimeOffset.UnixEpoch));
            var retrieved = JsonSerializer.Deserialize<TranscriptSegment>(JsonSerializer.Serialize(Segment()))!;
            var retrieval = new RetrievalWorkflow(1, request.OperationId, request.BoundaryMs,
                OperationVersionHash.Of(JsonSerializer.Serialize(new { request, versions })),
                [new(Guid.NewGuid(), "Поиск точного оригинала")],
                [new(new(request.OperationId, "", Id), [new(retrieved, OperationVersionHash.Of(retrieved.Text))], "found")]);
            journal.SaveRetrieval(retrieval);
            var roundtrip = new SessionJournal(directory).ReadRetrieval(request.OperationId)!;
            journal.SaveRetrieval(roundtrip);
            Check(roundtrip.Batches.Single().Passages.Single().Segment.Recognition!.Alternatives.Single().Text == retrieved.Recognition!.Alternatives.Single().Text);
            journal.AdvanceModelOperation(request.OperationId, ModelOperationPhase.ResultReceived, result: result);
            // Реальный отказ атомарной замены state.json; результат уже долговечен.
            Directory.CreateDirectory(Path.Combine(directory, "state.json"));
            try { await journal.CommitAsync(new(request.OperationId, result, new(request.OperationId, DateTimeOffset.UnixEpoch, result.DisplayText!, null)), default);
                throw new Exception("Ожидался отказ записи."); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Directory.Delete(Path.Combine(directory, "state.json"));
            byte[] before = File.ReadAllBytes(Path.Combine(directory, "transcript.jsonl"));
            var restored = new SessionJournal(directory);
            var backend = new NoInference(); var clock = new ManualClock();
            await using (var engine = new ConversationEngine(clock, new SimulatedTranscriptSource(clock) { DemoSpeechEnabled = false }, backend, restored))
            { engine.ResumeRecoveredOperations(); await engine.WhenIdleAsync(); Check(engine.Snapshot().Error is null); }
            var reopened = new SessionJournal(directory);
            Check(backend.Calls == 0 && reopened.Read().Messages.Count == 1);
            var operation = reopened.ModelOperations.Single();
            Check(operation.Phase == ModelOperationPhase.Completed && JsonSerializer.Serialize(operation.Result!.Interpretations) == JsonSerializer.Serialize(result.Interpretations));
            Check(File.ReadAllBytes(Path.Combine(directory, "transcript.jsonl")).SequenceEqual(before));
            var changed = Segment() with { Text = "Оплату подтвердили." };
            try { reopened.AppendTranscript(changed); throw new Exception("Принята замена оригинала."); } catch (InvalidOperationException) { }
            var old = Segment() with { Recognition = null };
            Check(!JsonSerializer.Serialize(old).Contains("Recognition"));
            Check(!JsonSerializer.Serialize(result with { Interpretations = null }).Contains("Interpretations"));
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class NoInference : IModelBackend
    {
        public int Calls;
        public Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken token)
        { Calls++; throw new Exception("Повторное inference запрещено."); }
    }
}
