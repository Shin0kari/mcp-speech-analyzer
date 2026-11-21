using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed record TokenCounts(long Input, long CachedInput, long Output, long ReasoningOutput, long Total,
    long? CacheWriteInput = null)
{
    internal static TokenCounts Zero => new(0, 0, 0, 0, 0);
    internal TokenCounts Minus(TokenCounts value) =>
        new(Input - value.Input, CachedInput - value.CachedInput, Output - value.Output,
            ReasoningOutput - value.ReasoningOutput, Total - value.Total, CacheWriteInput - value.CacheWriteInput);

    internal TokenCounts Plus(TokenCounts value) =>
        new(Input + value.Input, CachedInput + value.CachedInput, Output + value.Output,
            ReasoningOutput + value.ReasoningOutput, Total + value.Total, CacheWriteInput + value.CacheWriteInput);

    internal bool Valid => Input >= 0 && Output >= 0 && CachedInput >= 0 && CachedInput <= Input &&
        ReasoningOutput >= 0 && ReasoningOutput <= Output && Total >= Input && Total - Input == Output &&
        CacheWriteInput is null or >= 0;
}

public sealed record TokenUsageScope(string Directory, Guid OperationId, Guid StepId, OperationKind Kind, bool Retrieval);
public sealed record TurnUsageReceipt(int Version, Guid OperationId, Guid StepId, OperationKind Kind, bool Retrieval,
    string ThreadId, string TurnId, string? TurnStatus, TokenCounts? Observed, bool Ambiguous,
    TokenCounts? LastResponse, TokenCounts? ThreadTotal, long? ContextWindow)
{
    public string Completeness => Observed is null || Ambiguous ? "unavailable" : "provisional";
    public string Semantics => "app-server.v2/thread/tokenUsage/updated";
}

// Notification totals are observations, not a final per-turn ledger.
internal sealed class CodexUsageObserver
{
    private const int Capacity = 64;
    private readonly object gate = new();
    private readonly Dictionary<string, TurnUsageReceipt> turns = new();
    private readonly Dictionary<string, string> paths = new();
    private readonly Queue<string> order = new();
    private readonly List<(string Method, JsonElement Parameters)> early = [];
    private string? thread;
    private string? latestTurn;
    private TokenCounts? latest;
    private bool starting;

    public void Bind(string id, bool fresh)
    {
        lock (gate)
        {
            thread = id;
            latest = fresh ? TokenCounts.Zero : null;
            latestTurn = null;
        }
    }

    public void Begin()
    {
        lock (gate) { early.Clear(); starting = true; }
    }

    public void End()
    {
        lock (gate) { early.Clear(); starting = false; }
    }

    public void Register(string id, string turn, TokenUsageScope scope)
    {
        lock (gate)
        {
            if (thread != id || turns.ContainsKey(turn)) return;
            if (order.Count == Capacity)
            {
                var expired = order.Dequeue();
                turns.Remove(expired);
                paths.Remove(expired);
            }
            order.Enqueue(turn);
            paths[turn] = Path.Combine(scope.Directory, "token-usage-v1", scope.StepId.ToString("N") + ".json");
            turns[turn] = new(1, scope.OperationId, scope.StepId, scope.Kind, scope.Retrieval, id, turn,
                null, null, false, null, null, null);
            Save(turn);
            foreach (var entry in early) ObserveCore(entry.Method, entry.Parameters);
            early.Clear();
            starting = false;
        }
    }

    public void Observe(string method, JsonElement parameters)
    {
        if (method is not ("thread/tokenUsage/updated" or "turn/completed" or "item/started" or "item/completed")) return;
        lock (gate)
        {
            try
            {
                if (!parameters.TryGetProperty("threadId", out var id) || id.GetString() != thread) return;
                if ((method is "item/started" or "item/completed") &&
                    parameters.GetProperty("item").GetProperty("type").GetString() != "contextCompaction") return;
                if (method == "turn/completed")
                {
                    var turn = parameters.GetProperty("turn");
                    parameters = JsonSerializer.SerializeToElement(new
                    {
                        threadId = thread,
                        turn = new { id = turn.GetProperty("id").GetString(), status = turn.GetProperty("status").GetString() }
                    });
                }
                if (starting && early.Count < Capacity) early.Add((method, parameters.Clone()));
                else ObserveCore(method, parameters);
            }
            catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException) { }
        }
    }

    private void ObserveCore(string method, JsonElement parameters)
    {
        if (method is "item/started" or "item/completed")
        {
            if (parameters.GetProperty("item").GetProperty("type").GetString() != "contextCompaction") return;
            latest = null;
            var compactedTurn = parameters.GetProperty("turnId").GetString();
            if (compactedTurn is not null && turns.TryGetValue(compactedTurn, out var compacted))
            {
                turns[compactedTurn] = compacted with { Ambiguous = true };
                Save(compactedTurn);
            }
            return;
        }
        var turn = (method == "turn/completed"
            ? parameters.GetProperty("turn").GetProperty("id") : parameters.GetProperty("turnId")).GetString();
        if (turn is null) return;
        turns.TryGetValue(turn, out var receipt);
        if (method == "turn/completed")
        {
            if (receipt is null) return;
            turns[turn] = receipt with { TurnStatus = parameters.GetProperty("turn").GetProperty("status").GetString() };
            Save(turn);
            return;
        }
        var usage = parameters.GetProperty("tokenUsage");
        var total = Read(usage.GetProperty("total"));
        var last = Read(usage.GetProperty("last"));
        long? window = usage.TryGetProperty("modelContextWindow", out var context) &&
            context.ValueKind == JsonValueKind.Number ? context.GetInt64() : null;
        if (receipt?.ThreadTotal == total || latest == total) return;

        var increment = latest is null ? null : total.Minus(latest);
        bool continuous = total.Valid && last.Valid && increment is not null &&
            (increment with { CacheWriteInput = null }) == (last with { CacheWriteInput = null }) &&
            (increment.CacheWriteInput is null || increment.CacheWriteInput == last.CacheWriteInput) && last.Total > 0;
        if (!continuous && increment is { Valid: false })
        {
            // A regressing snapshot cannot replace a newer receipt. Lose the baseline for subsequent turns.
            latest = null;
            return;
        }
        if (receipt is not null)
        {
            bool ambiguous = receipt.Ambiguous || !continuous || receipt.ThreadTotal is not null && latestTurn != turn;
            turns[turn] = receipt with
            {
                Ambiguous = ambiguous,
                Observed = ambiguous ? receipt.Observed : receipt.Observed?.Plus(last) ?? last,
                ThreadTotal = total, LastResponse = last, ContextWindow = window
            };
            Save(turn);
        }
        latest = total.Valid ? total : null;
        latestTurn = turn;
    }

    private static TokenCounts Read(JsonElement value) => new(value.GetProperty("inputTokens").GetInt64(),
        value.GetProperty("cachedInputTokens").GetInt64(), value.GetProperty("outputTokens").GetInt64(),
        value.GetProperty("reasoningOutputTokens").GetInt64(), value.GetProperty("totalTokens").GetInt64(),
        value.TryGetProperty("cacheWriteInputTokens", out var writes) ? writes.GetInt64() : null);

    private void Save(string turn)
    {
        var path = paths[turn];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicTextFile.Write(path, JsonSerializer.Serialize(turns[turn]));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            FunctionalRunTrace.Write("terra_usage_receipt_write_failed");
        }
    }
}

