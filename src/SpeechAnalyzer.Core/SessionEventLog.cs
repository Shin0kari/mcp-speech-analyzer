using System.Diagnostics;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

// Отдельный файл на запуск: оборванная строка прежнего процесса не портит продолжение.
// Здесь только технические метаданные; текст результатов хранит SessionJournal.
public sealed class SessionEventLog
{
    private readonly object gate = new();
    private readonly long origin = Stopwatch.GetTimestamp();
    public static SessionEventLog? Current { get; set; }
    public string FilePath { get; }
    public string? Error => DiagnosticMetadataWriter.Error ?? (DiagnosticMetadataWriter.Dropped == 0 ? null : "Diagnostic metadata queue overflow.");
    public SessionEventLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "events-" + Guid.NewGuid().ToString("N") + ".jsonl");
        using var file = new FileStream(FilePath, FileMode.CreateNew); file.Flush(true);
    }
    public void Write(string name, object? data = null)
    {
        using var measurement = CaptureTiming.Phase(CaptureTimingKind.EventWrite);
        DiagnosticMetadataWriter.Write(FilePath, new { version = 1, name,
            utc = DateTimeOffset.UtcNow, monotonicQpc = Stopwatch.GetTimestamp(), qpcFrequency = Stopwatch.Frequency,
            elapsedMs = Stopwatch.GetElapsedTime(origin).TotalMilliseconds, data }, flush: true);
    }
}
