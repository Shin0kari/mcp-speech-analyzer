using System.Diagnostics;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

// Не содержит текста или PCM. Включается только явным параметром запуска измерений.
public static class FunctionalRunTrace
{
    private static readonly object Gate = new();
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly string? Path = Environment.GetEnvironmentVariable("SPEECH_ANALYZER_METRICS_PATH");
    public static string? Error => DiagnosticMetadataWriter.Error;

    public static void WriteTimelineSnapshot(AudioTimelineDiagnostic diagnostic)
    {
        if (string.IsNullOrWhiteSpace(Path)) return;
        DiagnosticMetadataWriter.Write(Path + ".timeline.jsonl", diagnostic);
    }

    public static void Write(string name, Guid? operationId = null, long? boundaryMs = null, int? pending = null,
        TranscriptionMetrics? transcriptionMetrics = null)
    {
        SessionEventLog.Current?.Write(name, new { operationId, boundaryMs, pending, transcriptionMetrics });
        if (string.IsNullOrWhiteSpace(Path)) return;
        DiagnosticMetadataWriter.Write(Path, new { name, operationId, boundaryMs, pending, transcriptionMetrics,
            elapsedMs = Stopwatch.GetElapsedTime(Origin).TotalMilliseconds, processId = Environment.ProcessId });
    }
}
