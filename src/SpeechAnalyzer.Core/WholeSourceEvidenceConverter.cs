using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeechAnalyzer.Core;

// Reads workflow v4 wire evidence. Journal serialization uses the canonical record.
internal sealed class WholeSourceEvidenceConverter : JsonConverter<InterpretationQuote>
{
    public override InterpretationQuote Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var selection = JsonDocument.ParseValue(ref reader);
        var item = selection.RootElement;
        if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
            !item.TryGetProperty("sourceSegmentId", out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(id.GetString(), "D", out var sourceId) || sourceId == Guid.Empty ||
            !item.TryGetProperty("text", out var text) || text.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new JsonException("Invalid evidence selection.");
        return text.ValueKind == JsonValueKind.Null
            ? new(sourceId, "") { WholeSource = true }
            : new(sourceId, text.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, InterpretationQuote value, JsonSerializerOptions options)
        => throw new NotSupportedException("Wire evidence is read-only.");
}
