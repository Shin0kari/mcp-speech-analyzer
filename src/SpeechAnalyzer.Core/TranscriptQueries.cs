using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed record TranscriptQuery(string Text = "", long FromMs = 0, long? ToMs = null, AudioChannel? Channel = null, Guid? SegmentId = null);
public sealed record TranscriptPage(
    [property: System.Text.Json.Serialization.JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments,
    [property: System.Text.Json.Serialization.JsonPropertyName("hasMore")] bool HasMore,
    [property: System.Text.Json.Serialization.JsonPropertyName("nextCursor")] string? NextCursor,
    [property: System.Text.Json.Serialization.JsonPropertyName("total")] int Total,
    [property: System.Text.Json.Serialization.JsonPropertyName("finalOnly")] bool FinalOnly = true);
public sealed class StaleTranscriptCursorException() : InvalidOperationException("Курсор расшифровки устарел или не принадлежит запросу. Начните запрос без cursor.");

// Страницы читают неизменный снимок окончательного текста: поздняя финализация и рост истории не сдвигают позиции.
public sealed class TranscriptQueries(Func<IReadOnlyList<TranscriptSegment>> read, IClock clock)
{
    private sealed record Cursor(Guid Snapshot, int Offset, string QueryHash);
    private sealed record Snapshot(TranscriptSegment[] Segments, TimeSpan CreatedAt, string QueryHash);
    private readonly Dictionary<Guid, Snapshot> snapshots = [];
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private readonly object gate = new();
    public TranscriptPage Read(TranscriptQuery query, int limit = 100, string? cursor = null)
    {
        if (limit is < 1 or > 500 || query.FromMs < 0 || query.ToMs < query.FromMs || query.Text.Length > 2000
            || query.Channel is { } channel && !Enum.IsDefined(channel))
            throw new ArgumentException("Параметры поиска: limit 1..500, непустой диапазон времени, text до 2000 символов.");
        string queryHash = OperationVersionHash.Of(JsonSerializer.Serialize(query));
        lock (gate)
        {
            foreach (var expired in snapshots.Where(p => clock.Now - p.Value.CreatedAt > TimeSpan.FromMinutes(5)).Select(p => p.Key).ToArray()) snapshots.Remove(expired);
            Cursor position;
            if (cursor is null)
            {
                var segments = read().Where(s => s.IsFinal && s.EndMs > query.FromMs && (query.ToMs is null || s.StartMs < query.ToMs)
                        && (query.Channel is null || s.Channel == query.Channel) && (query.SegmentId is null || s.Id == query.SegmentId)
                        && (query.Text.Length == 0 || s.Text.Contains(query.Text, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(s => s.StartMs).ThenBy(s => s.Channel).ThenBy(s => s.Id).ToArray();
                if (segments.Length > 200_000) throw new InvalidOperationException("Слишком широкий запрос: задайте диапазон времени или текст поиска.");
                while (snapshots.Count >= 8 || snapshots.Count > 0 && snapshots.Sum(p => p.Value.Segments.Length) + segments.Length > 200_000)
                    snapshots.Remove(snapshots.MinBy(p => p.Value.CreatedAt).Key);
                position = new(Guid.NewGuid(), 0, queryHash);
                snapshots.Add(position.Snapshot, new(segments, clock.Now, queryHash));
            }
            else position = Decode(cursor);
            if (position.QueryHash != queryHash || !snapshots.TryGetValue(position.Snapshot, out var snapshot)
                || snapshot.QueryHash != queryHash || position.Offset < 0 || position.Offset > snapshot.Segments.Length)
                throw new StaleTranscriptCursorException();
            var page = snapshot.Segments.Skip(position.Offset).Take(limit).ToArray();
            int next = position.Offset + page.Length;
            bool more = next < snapshot.Segments.Length;
            return new(page, more, more ? Encode(position with { Offset = next }) : null, snapshot.Segments.Length);
        }
    }
    private string Encode(Cursor cursor)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor));
        return Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(HMACSHA256.HashData(secret, bytes));
    }
    private Cursor Decode(string token)
    {
        try
        {
            if (token.Length > 2048) throw new FormatException();
            string[] parts = token.Split('.'); if (parts.Length != 2) throw new FormatException();
            byte[] bytes = Convert.FromBase64String(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(secret, bytes), Convert.FromBase64String(parts[1]))) throw new FormatException();
            return JsonSerializer.Deserialize<Cursor>(bytes) ?? throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw new StaleTranscriptCursorException(); }
    }
}
