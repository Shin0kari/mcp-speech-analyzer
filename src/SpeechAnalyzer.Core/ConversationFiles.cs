using System.Text;

namespace SpeechAnalyzer.Core;

public sealed record SavedConversation(string DirectoryPath, DateTime LastChangedUtc)
{
    public string Label => $"{LastChangedUtc.ToLocalTime():dd.MM.yyyy HH:mm} · {Path.GetFileName(DirectoryPath)}";
}

public static class ConversationFiles
{
    public static IReadOnlyList<SavedConversation> ListRoots(params string[] roots) => roots
        .Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(List)
        .DistinctBy(item => item.DirectoryPath, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(item => item.LastChangedUtc).ToArray();
    public static IReadOnlyList<SavedConversation> List(string root) => !Directory.Exists(root) ? [] :
        Directory.EnumerateDirectories(root).Where(path => File.Exists(Path.Combine(path, "transcript.jsonl"))
            || File.Exists(Path.Combine(path, "capture-duration.json")))
        .Select(path => new SavedConversation(path, Directory.GetLastWriteTimeUtc(path)))
        .OrderByDescending(item => item.LastChangedUtc).ToArray();

    // Экспорт — производный снимок журнала, без ответов/сводок модели и без нового inference.
    public static string Export(SessionJournal journal, CaptureDurationState? duration = null)
    {
        var text = new StringBuilder("Speech Analyzer — исходная расшифровка\n");
        text.AppendLine("Только окончательный текст разговора. Ответы и анализ помощника хранятся отдельно.");
        if (duration is not null)
        {
            text.AppendLine("Активный захват: " + CaptureDuration.Format(duration.ActiveCaptureDurationMs));
            if (duration.DurationUncertain) text.AppendLine("Длительность неполна: аварийный интервал или время старой сессии неизвестны.");
            if (duration.TranscriptMayBeIncomplete || duration.TranscriptionPending)
                text.AppendLine("ВНИМАНИЕ: незавершённая транскрипция могла быть утрачена или ещё ожидается.");
        }
        if (journal.RecoveryNotice is { } notice) text.AppendLine("Восстановление: " + notice);
        text.AppendLine();
        foreach (var item in journal.Transcript)
        {
            text.AppendLine($"[{CaptureDuration.Format(item.StartMs)}.{item.StartMs % 1000:000}–{CaptureDuration.Format(item.EndMs)}.{item.EndMs % 1000:000}] " +
                $"{(item.Channel == AudioChannel.You ? "Вы" : "Собеседник")} · {item.CapturedAt:O} · {item.Id}");
            text.AppendLine(item.Text); text.AppendLine();
        }
        string path = Path.Combine(journal.DirectoryPath, "conversation.txt");
        AtomicTextFile.Write(path, text.ToString());
        return path;
    }
}
