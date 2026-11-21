using System.Text.Json.Serialization;

namespace SpeechAnalyzer.Core;

// Альтернативы относятся к тому же интервалу, а не к дополнительным репликам.
public sealed record RecognitionHypothesis(long WindowStartMs, long WindowEndMs, string Text);
public sealed record RecognitionEvidence(long WindowStartMs, long WindowEndMs, string Stability,
    IReadOnlyList<RecognitionHypothesis> Alternatives);
public sealed record InterpretationQuote(Guid SourceSegmentId, string Text)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WholeSource { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FragmentId { get; init; }
}
public sealed record ContextualInterpretation(string ProposedMeaning, string Reason,
    IReadOnlyList<InterpretationQuote> Evidence, IReadOnlyList<string> Assumptions,
    string Uncertainty, bool Unresolved, IReadOnlyList<string> AffectedQuestionIds);

public static class InterpretationValidation
{
    public static void Validate(ProcessingRequest request, ProcessingResult result, IReadOnlyList<TranscriptSegment> originals)
    {
        var interpretations = result.Interpretations ?? [];
        if (interpretations.Count > 16) throw new InvalidDataException("Слишком много интерпретаций.");
        bool usesFragments = result.ResultContractVersion == FragmentEvidence.Version;
        var sources = usesFragments ? FragmentEvidence.Permitted(originals, request.BoundaryMs) :
            originals.Where(s => s.IsFinal && s.EndMs <= request.BoundaryMs).ToDictionary(s => s.Id);
        var fragments = usesFragments ? new FragmentEvidence(sources) : null;
        if (fragments is not null)
        {
            foreach (var reference in result.SourceReferences ?? []) fragments.Validate(reference.Evidence ?? []);
            foreach (var reference in result.QuestionReferences ?? []) fragments.Validate(reference.Evidence ?? []);
            foreach (var item in interpretations)
                if (item?.Evidence is { } evidence) fragments.Validate(evidence);
        }
        foreach (var segment in sources.Values) ValidateRecognition(segment, request.BoundaryMs);
        if (result.ResultContractVersion is not (null or 3 or 4 or FragmentEvidence.Version) || result.ResultContractVersion is >= 3 && result.Outcome is null)
            throw new InvalidDataException("Unsupported or incomplete result contract.");
        if (result.ResultContractVersion == 4)
        {
            var quotes = interpretations.SelectMany(item => item?.Evidence ?? [])
                .Concat((result.QuestionReferences ?? []).SelectMany(item => item.Evidence ?? []))
                .Concat((result.SourceReferences ?? []).SelectMany(item => item.Evidence ?? []));
            foreach (var quote in quotes)
                if (quote is { WholeSource: true } &&
                    (!sources.TryGetValue(quote.SourceSegmentId, out var original) || !string.Equals(quote.Text, original.Text, StringComparison.Ordinal)))
                    throw new InvalidDataException("Whole-source evidence does not match the permitted original.");
        }
        if (result.Outcome is not null) ValidateOutcome(request, result, sources);
        foreach (var item in interpretations)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.ProposedMeaning) || item.ProposedMeaning.Length > 4000 ||
                string.IsNullOrWhiteSpace(item.Reason) || item.Reason.Length > 4000 || item.Uncertainty is null ||
                item.Uncertainty.Length > 4000 || item.Evidence is null || item.Evidence.Count is < 1 or > 16 ||
                item.Assumptions is null || item.Assumptions.Count > 16 || item.Assumptions.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 2000) ||
                item.AffectedQuestionIds is null || item.AffectedQuestionIds.Count > 32 ||
                item.Unresolved && string.IsNullOrWhiteSpace(item.Uncertainty))
                throw new InvalidDataException("Неверная интерпретация или отсутствует явная неопределённость.");
            foreach (var quote in item.Evidence)
                if (quote is null || !sources.TryGetValue(quote.SourceSegmentId, out var source) || string.IsNullOrWhiteSpace(quote.Text) ||
                    !source.Text.Contains(quote.Text, StringComparison.Ordinal))
                    throw new InvalidDataException("Интерпретация ссылается на недоступную или изменённую исходную цитату.");
            var affected = item.AffectedQuestionIds.AsEnumerable();
            if (result.Outcome is null)
                affected = affected.Concat((result.QuestionReferences ?? [])
                    .Where(r => r.SourceSegmentIds.Any(id => item.Evidence.Any(e => e.SourceSegmentId == id))).Select(r => r.QuestionId));
            foreach (string id in affected.Distinct())
            {
                var question = result.State.Questions.SingleOrDefault(q => q.Id == id)
                    ?? throw new InvalidDataException("Интерпретация ссылается на неизвестный вопрос.");
                if (item.Unresolved && (question.Status != QuestionStatus.Open || (result.DisplayedQuestionIds ?? []).Contains(id)))
                    throw new InvalidDataException("Неопределённая интерпретация не может закрыть вопрос.");
            }
        }
        if (result.Outcome is null && interpretations.Any(i => i.Unresolved))
            foreach (string entry in result.State.Commitments.Except(request.Previous.Commitments)
                .Concat(result.State.Facts.Except(request.Previous.Facts)))
                if (!(result.SourceReferences ?? []).Any(r => r.Text == entry && r.SourceSegmentIds.Count > 0 &&
                    r.SourceSegmentIds.All(id => sources.ContainsKey(id) &&
                        !interpretations.Where(i => i.Unresolved).SelectMany(i => i.Evidence).Any(e => e.SourceSegmentId == id))))
                    throw new InvalidDataException("Новый подтверждённый факт или обязательство требует независимого источника без неопределённой интерпретации.");
        if (result.Outcome is not null)
        {
            var uncertain = interpretations.Where(i => i.Unresolved).SelectMany(i => i.Evidence).ToArray();
            foreach (string entry in result.State.Commitments.Except(request.Previous.Commitments)
                .Concat(result.State.Facts.Except(request.Previous.Facts)))
                if (!(result.SourceReferences ?? []).Any(reference => reference.Text == entry &&
                    reference.Evidence!.Any(quote => (fragments?.Independent(quote, uncertain) ?? Independent(quote, uncertain, sources[quote.SourceSegmentId].Text)))))
                    throw new InvalidDataException("Новая подтверждённая запись требует точной цитаты вне неопределённого фрагмента.");
            foreach (var question in result.State.Questions.Where(question => question.Status != QuestionStatus.Open &&
                !request.Previous.Questions.Any(previous => previous.Id == question.Id && previous.Status == question.Status)))
                if (!(result.QuestionReferences ?? []).Any(reference => reference.QuestionId == question.Id &&
                    reference.Evidence!.Any(quote => (fragments?.Independent(quote, uncertain) ?? Independent(quote, uncertain, sources[quote.SourceSegmentId].Text)))))
                    throw new InvalidDataException("Закрытие вопроса требует точной цитаты вне неопределённого фрагмента.");
        }
    }

    private static bool Independent(InterpretationQuote quote, InterpretationQuote[] uncertain, string text)
    {
        var otherQuotes = uncertain.Where(item => item.SourceSegmentId == quote.SourceSegmentId).ToArray();
        if (otherQuotes.Length == 0) return true;
        int start = text.IndexOf(quote.Text, StringComparison.Ordinal);
        if (text.IndexOf(quote.Text, start + 1, StringComparison.Ordinal) >= 0) return false;
        foreach (var other in otherQuotes)
            for (int index = text.IndexOf(other.Text, StringComparison.Ordinal); index >= 0;
                index = text.IndexOf(other.Text, index + 1, StringComparison.Ordinal))
                if (start < index + other.Text.Length && index < start + quote.Text.Length) return false;
        return true;
    }

    private static void ValidateOutcome(ProcessingRequest request, ProcessingResult result,
        IReadOnlyDictionary<Guid, TranscriptSegment> sources)
    {
        var outcome = result.Outcome!.Value;
        if (!Enum.IsDefined(outcome) || outcome == ResultOutcome.Unusable)
            throw new InvalidDataException("Модель вернула непригодный результат.");
        var questions = result.State.Questions.ToDictionary(question => question.Id);
        var displayed = result.DisplayedQuestionIds ?? [];
        var open = result.OpenQuestionIds ?? [];
        var previous = request.Previous.Questions.ToDictionary(question => question.Id);
        var references = result.QuestionReferences ?? [];
        bool visible = outcome is ResultOutcome.FullAnswer or ResultOutcome.PartialAnswer or ResultOutcome.Clarification;
        bool unresolved = outcome is ResultOutcome.PartialAnswer or ResultOutcome.Clarification;
        if (request.Kind == OperationKind.Background && visible ||
            (visible ? string.IsNullOrWhiteSpace(result.DisplayText) : result.DisplayText is not null) ||
            (outcome == ResultOutcome.Summary ? string.IsNullOrWhiteSpace(result.Summary) : result.Summary is not null) ||
            (unresolved ? string.IsNullOrWhiteSpace(result.Limitations) : result.Limitations is not null) ||
            (!visible || outcome == ResultOutcome.Clarification) && displayed.Count != 0 ||
            !unresolved && open.Count != 0 || open.Distinct().Count() != open.Count ||
            displayed.Distinct().Count() != displayed.Count ||
            open.Any(id => !questions.TryGetValue(id, out var q) || q.Status != QuestionStatus.Open) ||
            displayed.Any(id => !questions.TryGetValue(id, out var q) || q.Status != QuestionStatus.Displayed) ||
            request.Kind == OperationKind.Automatic && displayed.Any(id => previous.TryGetValue(id, out var q) && q.Status != QuestionStatus.Open))
            throw new InvalidDataException("Поля результата не соответствуют заявленному исходу.");
        if (result.ResultContractVersion is >= 3 && unresolved)
        {
            if (open.Count == 0 || open.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("An unresolved result requires explicit addressed question IDs.");
            foreach (string id in open)
            {
                var targetReferences = references.Where(reference => reference.QuestionId == id).ToArray();
                if (targetReferences.Length != 1 ||
                    !ValidEvidence(targetReferences[0].SourceSegmentIds, targetReferences[0].Evidence, sources))
                    throw new InvalidDataException("Each addressed unresolved question requires its own exact source evidence, including unchanged questions.");
            }
        }
        if (request.Previous.Facts.Except(result.State.Facts).Any() ||
            request.Previous.Commitments.Except(result.State.Commitments).Any() ||
            previous.Keys.Except(questions.Keys).Any())
            throw new InvalidDataException("Результат удаляет ранее сохранённое состояние.");
        if (outcome == ResultOutcome.NoChange && (!request.Previous.Facts.SequenceEqual(result.State.Facts) ||
            !request.Previous.Commitments.SequenceEqual(result.State.Commitments) ||
            !request.Previous.Questions.SequenceEqual(result.State.Questions) ||
            (result.Interpretations?.Count ?? 0) != 0 || references.Count != 0 || (result.SourceReferences?.Count ?? 0) != 0))
            throw new InvalidDataException("Результат NoChange содержит изменения.");
        foreach (var question in questions.Values)
        {
            if (previous.TryGetValue(question.Id, out var old) && old == question) continue;
            if (string.IsNullOrWhiteSpace(question.Id) || string.IsNullOrWhiteSpace(question.Text) ||
                question.Status == QuestionStatus.Displayed && !displayed.Contains(question.Id) ||
                !references.Any(reference => reference.QuestionId == question.Id &&
                    reference.SourceSegmentIds.Count > 0 && reference.SourceSegmentIds.All(sources.ContainsKey)))
                throw new InvalidDataException("Новый или изменённый вопрос требует допустимого источника.");
        }
        if (references.Any(reference => !questions.ContainsKey(reference.QuestionId) ||
            !ValidEvidence(reference.SourceSegmentIds, reference.Evidence, sources)))
            throw new InvalidDataException("Ссылка вопроса указывает на недопустимый источник.");
        foreach (var reference in result.SourceReferences ?? [])
            if (!ValidEvidence(reference.SourceSegmentIds, reference.Evidence, sources))
                throw new InvalidDataException("Ссылка на факт содержит недопустимую цитату.");
    }

    private static bool ValidEvidence(IReadOnlyList<Guid> ids, IReadOnlyList<InterpretationQuote>? evidence,
        IReadOnlyDictionary<Guid, TranscriptSegment> sources) =>
        evidence is { Count: > 0 and <= 16 } && ids.Count > 0 &&
        ids.All(id => sources.ContainsKey(id) && evidence.Any(quote => quote?.SourceSegmentId == id)) &&
        evidence.All(quote => quote is not null && ids.Contains(quote.SourceSegmentId) &&
            !string.IsNullOrWhiteSpace(quote.Text) && sources[quote.SourceSegmentId].Text.Contains(quote.Text, StringComparison.Ordinal));

    public static void ValidateRecognition(TranscriptSegment segment, long? boundaryMs = null)
    {
        if (segment.Recognition is not { } evidence) return;
        if (evidence.WindowStartMs < 0 || evidence.WindowStartMs > segment.StartMs || evidence.WindowEndMs < segment.EndMs ||
            evidence.WindowEndMs - evidence.WindowStartMs > 8000 || string.IsNullOrWhiteSpace(evidence.Stability) ||
            evidence.Alternatives is null || evidence.Alternatives.Count > 2 || evidence.Alternatives.Any(a =>
                a is null || a.WindowStartMs < 0 || a.WindowEndMs <= a.WindowStartMs || a.WindowEndMs - a.WindowStartMs > 8000 ||
                a.WindowStartMs >= segment.EndMs || a.WindowEndMs <= segment.StartMs || a.Text is null || a.Text.Length > 16000))
            throw new InvalidDataException("Неверное или слишком большое происхождение гипотезы STT.");
        if (boundaryMs is { } boundary && (evidence.WindowEndMs > boundary || evidence.Alternatives.Any(a => a.WindowEndMs > boundary)))
            throw new InvalidDataException("Гипотеза с аудио после среза не может участвовать в текущем запросе.");
    }
}
