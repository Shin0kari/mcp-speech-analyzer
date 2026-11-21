using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SpeechAnalyzer.Core;

internal sealed class FragmentEvidence(IReadOnlyDictionary<Guid, TranscriptSegment> originals)
{
    internal const int Version = 5;
    private const int MaxFragments = 16;
    private readonly Dictionary<Guid, Range[]> catalogues = new();

    internal static bool IsAvailable(TranscriptSegment segment, long cutoff) =>
        segment.IsFinal && segment.EndMs <= cutoff && !string.IsNullOrWhiteSpace(segment.Text);

    internal static Dictionary<Guid, TranscriptSegment> Permitted(IEnumerable<TranscriptSegment> segments, long cutoff)
    {
        var originals = new Dictionary<Guid, TranscriptSegment>();
        foreach (var segment in segments)
        {
            if (segment.Id == Guid.Empty)
                throw new InvalidDataException("An original has an empty segment ID.");
            if (!originals.TryAdd(segment.Id, segment) &&
                JsonSerializer.Serialize(originals[segment.Id]) != JsonSerializer.Serialize(segment))
                throw new InvalidDataException("Permitted originals disagree about the same segment ID.");
        }
        return originals.Where(item => item.Value.IsFinal && item.Value.EndMs <= cutoff)
            .ToDictionary(item => item.Key, item => item.Value);
    }

    internal static object PromptText(TranscriptSegment segment, long cutoff) =>
        IsAvailable(segment, cutoff)
            ? Partition(segment.Text).Select(range => segment.Text[range]).ToArray()
            : segment.Text;

    internal static JsonNode PromptBatches(IReadOnlyList<OriginalRetrievalBatch> batches, long cutoff, bool namedChannels = false)
    {
        var payload = JsonSerializer.SerializeToNode(batches)!.AsArray();
        for (int i = 0; i < batches.Count; i++)
            for (int j = 0; j < batches[i].Passages.Count; j++)
            {
                var original = batches[i].Passages[j].Segment;
                var segment = payload[i]![nameof(OriginalRetrievalBatch.Passages)]![j]![nameof(OriginalPassage.Segment)]!;
                segment[nameof(TranscriptSegment.Text)] = JsonSerializer.SerializeToNode(PromptText(original, cutoff));
                if (namedChannels) segment[nameof(TranscriptSegment.Channel)] = original.Channel.ToString();
            }
        return payload;
    }

    private static Range[] Partition(string text)
    {
        var ranges = new List<Range>();
        int start = 0;
        int lastContent = text.AsSpan().TrimEnd().Length;
        for (int i = 1; i < lastContent && ranges.Count < MaxFragments - 1; i++)
        {
            if (text[i - 1] is not ('.' or '!' or '?' or ',' or ';' or ':' or '…' or '。' or '！' or '？') ||
                !char.IsWhiteSpace(text[i])) continue;
            ranges.Add(start..i);
            start = i;
        }
        ranges.Add(start..text.Length);
        return ranges.ToArray();
    }

    private (string Text, Range Range) Locate(InterpretationQuote quote)
    {
        if (quote is not { FragmentId: >= 0 and <= MaxFragments } || quote.WholeSource ||
            !originals.TryGetValue(quote.SourceSegmentId, out var segment) || string.IsNullOrWhiteSpace(segment.Text))
            throw new InvalidDataException("Evidence must select a fragment of a permitted finalized original.");
        int id = quote.FragmentId.Value;
        if (id == 0) return (segment.Text, 0..segment.Text.Length);
        if (!catalogues.TryGetValue(segment.Id, out var ranges))
            catalogues.Add(segment.Id, ranges = Partition(segment.Text));
        if (id > ranges.Length) throw new InvalidDataException("Evidence fragment ID is outside the supplied catalogue.");
        return (segment.Text, ranges[id - 1]);
    }

    internal InterpretationQuote[] Resolve(IReadOnlyList<InterpretationQuote> selections)
    {
        if (selections.Count is < 1 or > 16) throw new InvalidDataException("Evidence requires 1 to 16 selections.");
        var result = new List<InterpretationQuote>(selections.Count);
        var seen = new HashSet<(Guid, Range)>();
        foreach (var selection in selections)
        {
            var (text, range) = Locate(selection);
            if (!seen.Add((selection.SourceSegmentId, range)))
                throw new InvalidDataException("Duplicate evidence selection.");
            result.Add(selection with { Text = text[range] });
        }
        return result.ToArray();
    }

    internal void Validate(IReadOnlyList<InterpretationQuote> evidence)
    {
        if (!Resolve(evidence).SequenceEqual(evidence))
            throw new InvalidDataException("Saved evidence does not match its canonical fragment.");
    }

    internal bool Independent(InterpretationQuote quote, IReadOnlyList<InterpretationQuote> uncertain)
    {
        var (_, range) = Locate(quote);
        foreach (var other in uncertain.Where(item => item.SourceSegmentId == quote.SourceSegmentId))
        {
            var (_, otherRange) = Locate(other);
            if (range.Start.Value < otherRange.End.Value && otherRange.Start.Value < range.End.Value)
                return false;
        }
        return true;
    }
}

internal sealed class FragmentEvidenceConverter : JsonConverter<InterpretationQuote>
{
    public override InterpretationQuote Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != 2 ||
            !element.TryGetProperty("sourceSegmentId", out var source) || source.ValueKind != JsonValueKind.String ||
            !source.TryGetGuid(out var id) || id == Guid.Empty ||
            !element.TryGetProperty("fragmentId", out var fragment) || fragment.ValueKind != JsonValueKind.Number ||
            !fragment.TryGetInt32(out int number))
            throw new JsonException("Expected a source segment ID and an integer fragment ID.");
        return new(id, "") { FragmentId = number };
    }

    public override void Write(Utf8JsonWriter writer, InterpretationQuote value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Wire selections are read-only.");
}
