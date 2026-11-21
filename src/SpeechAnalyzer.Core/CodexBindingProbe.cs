using System.Text.Json;

namespace SpeechAnalyzer.Core;

public static class CodexBindingProbe
{
    public static void ValidateId(string candidateId)
    {
        if (string.IsNullOrWhiteSpace(candidateId) || candidateId.Length > 200) throw new ArgumentException("Неверный ID чата: укажите от 1 до 200 символов.");
    }
    // Отдельный read-only контракт позволяет проверять протокол без процесса Codex и inference.
    public static async Task<ChatBindingCandidate> ReadAsync(string candidateId, Func<string, object, Task<JsonElement>> request)
    {
        ValidateId(candidateId);
        var response = await request("thread/read", new { threadId = candidateId, includeTurns = false }).ConfigureAwait(false);
        var thread = response.GetProperty("thread");
        int turns = 0; string? cursor = null;
        for (int page = 0; page < 4; page++)
        {
            var history = await request("thread/turns/list", new { threadId = candidateId, cursor, limit = 50,
                itemsView = "summary", sortDirection = "asc" }).ConfigureAwait(false);
            turns += history.GetProperty("data").GetArrayLength();
            cursor = history.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            if (cursor is null) break;
        }
        string? Optional(string property) => thread.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new(thread.GetProperty("id").GetString()!, thread.GetProperty("cwd").GetString()!,
            Optional("name"), Optional("model"), turns, cursor is null);
    }
}
