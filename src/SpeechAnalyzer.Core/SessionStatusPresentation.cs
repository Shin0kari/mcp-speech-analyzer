namespace SpeechAnalyzer.Core;

public static class SessionStatusPresentation
{
    public static string Completion(TranscriptStopState state, bool recordingStopped) => state switch {
        TranscriptStopState.StoppedWithGaps => "Запись остановлена. Доступные фрагменты расшифрованы, в записи есть пропуски.",
        TranscriptStopState.Open => "",
        TranscriptStopState.Finishing when recordingStopped => "Запись остановлена; расшифровка завершается.",
        TranscriptStopState.Complete when recordingStopped => "Запись остановлена; допустимое аудио расшифровано полностью.",
        _ => recordingStopped ? "Запись остановлена; расшифровка неполна." : "Остановка записи не подтверждена; расшифровка неполна." };
    public static string AnalysisFailure(string error, bool recordingActive) =>
        (recordingActive ? "Анализ недоступен; аудиозапись продолжается. " : "Анализ недоступен; аудиозапись остановлена. ") + error;
}
