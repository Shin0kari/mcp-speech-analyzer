using System.Globalization;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

public enum SttTimingStatus { Valid, Malformed, Reversed, OutsideInput }

public sealed record SttRawTiming(string? StartMsJson, string? EndMsJson, string? Text, SttTimingStatus Status);

public sealed record SttInputEvidence(long DecoderStartSample, long FreshStartSample, long EndSample,
    IReadOnlyList<SttRawTiming> RawTimings);

public interface ISttTimingSource
{
    IReadOnlyList<SttRawTiming> LastTimings { get; }
}

public static class SttTiming
{
    public static IReadOnlyList<SttRawTiming> Read(JsonElement timings, int samples)
    {
        double durationMs = samples * (1000d / BufferedTranscriptSource.SampleRate);
        if (timings.ValueKind != JsonValueKind.Array)
            return [new(null, null, timings.GetRawText(), SttTimingStatus.Malformed)];
        return timings.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Object)
                return new SttRawTiming(null, null, item.GetRawText(), SttTimingStatus.Malformed);
            string? start = Raw(item, "StartMs"), end = Raw(item, "EndMs");
            var status = !Parse(start, out double a) || !Parse(end, out double b) ? SttTimingStatus.Malformed
                : b < a ? SttTimingStatus.Reversed
                : a < 0 || b > durationMs ? SttTimingStatus.OutsideInput : SttTimingStatus.Valid;
            string? text = item.TryGetProperty("Text", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            return new SttRawTiming(start, end, text, status);
        }).ToArray();
    }

    private static string? Raw(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) ? value.GetRawText() : null;

    private static bool Parse(string? value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
}
