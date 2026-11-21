using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed record ReasoningOption(string Value, string Label);
public sealed record CodexSessionSettings(string? ThreadId, string? ReasoningEffort);

public static class CodexModelSettings
{
    public const string Model = "gpt-6.1-terra";
    public static string ThreadName(string threadId) => $"[Speech Analyzer] Terra · {threadId}";

    public static IReadOnlyList<ReasoningOption> ParseOptions(JsonElement model)
    {
        var options = model.GetProperty("supportedReasoningEfforts").EnumerateArray()
            .Select(item => item.GetProperty("reasoningEffort").GetString()
                ?? throw new IOException("Пустой уровень рассуждения в каталоге Codex."))
            .Distinct(StringComparer.Ordinal).Select(value => new ReasoningOption(value, value switch
            {
                "none" => "Без рассуждения", "minimal" => "Минимальный", "low" => "Низкий",
                "medium" => "Средний", "high" => "Высокий", "xhigh" => "Очень высокий",
                "max" => "Максимальный", "ultra" => "Ультра", _ => value
            })).ToArray();
        if (options.Length == 0) throw new IOException("Codex не сообщил уровни рассуждения Terra.");
        return options;
    }

    public static void ValidateEffort(string? effort, IReadOnlyList<ReasoningOption> options)
    {
        if (effort is not null && !options.Any(option => option.Value == effort))
            throw new InvalidOperationException($"Terra не поддерживает выбранный уровень рассуждения: {effort}. Выберите доступный уровень.");
    }
}
