using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed record CodexCompactionEvidence(string ThreadId, string TurnId, string ItemId);

// Ограниченное наблюдение одной явной компактизации: ACK запроса не доказывает завершение.
// Содержимое истории и сообщения модели здесь не накапливаются.
public sealed class CodexCompactionTracker(string threadId)
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<CodexCompactionEvidence> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? turnId;
    private string? itemId;
    private bool itemCompleted;
    private int events;
    public Task<CodexCompactionEvidence> Completion => completion.Task;

    public void Fail(Exception error) { lock (gate) completion.TrySetException(error); }

    public void Observe(string method, JsonElement parameters)
    {
        lock (gate)
        {
            if (completion.Task.IsCompleted || !parameters.TryGetProperty("threadId", out var thread)
                || thread.GetString() != threadId) return;
            if (method is not ("turn/started" or "turn/completed" or "item/started" or "item/completed")) return;
            if (++events > 256) { Fail(new IOException("Превышен лимит событий компактизации.")); return; }
            if (method == "turn/started")
            {
                turnId ??= parameters.GetProperty("turn").GetProperty("id").GetString();
                return;
            }
            if (turnId is null) return;
            if (method == "turn/completed")
            {
                var turn = parameters.GetProperty("turn");
                if (turn.GetProperty("id").GetString() != turnId) return;
                if (turn.GetProperty("status").GetString() != "completed" || itemId is null || !itemCompleted)
                    Fail(new IOException("Ход компактизации не завершён успешно с полным contextCompaction lifecycle."));
                else completion.TrySetResult(new(threadId, turnId, itemId));
                return;
            }
            if (parameters.GetProperty("turnId").GetString() != turnId) return;
            var item = parameters.GetProperty("item");
            if (item.GetProperty("type").GetString() != "contextCompaction") return;
            string? id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id)) { Fail(new IOException("Нет ID contextCompaction.")); return; }
            if (method == "item/started")
            {
                if (itemId is not null && itemId != id) Fail(new IOException("Несколько contextCompaction в одном ходе."));
                else itemId = id;
            }
            else if (itemId == id) itemCompleted = true;
            else Fail(new IOException("Завершение contextCompaction без соответствующего старта."));
        }
    }
}
