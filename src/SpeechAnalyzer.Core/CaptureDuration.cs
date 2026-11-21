using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed record CaptureDurationState(int Version = 1, long ActiveCaptureDurationMs = 0,
    bool CaptureWasActive = false, bool TranscriptionPending = false,
    bool DurationUncertain = false, bool TranscriptMayBeIncomplete = false,
    DateTimeOffset? CheckpointUtc = null, long FinalizedAudioBoundaryMs = 0);

// UTC используется только для диагностики. Простой приложения никогда не прибавляется.
public sealed class CaptureDuration
{
    public const long CheckpointIntervalMs = 1000;
    private readonly object gate = new();
    private readonly IClock clock;
    private readonly Action<CaptureDurationState>? save;
    private CaptureDurationState state;
    private TimeSpan? activeSince;
    private TimeSpan lastCheckpoint;
    private long originMs;
    public CaptureDuration(IClock clock, CaptureDurationState? restored = null, Action<CaptureDurationState>? save = null)
    {
        this.clock = clock; this.save = save;
        state = restored ?? new();
        if (state.Version != 1 || state.ActiveCaptureDurationMs < 0 || state.FinalizedAudioBoundaryMs < 0
            || state.FinalizedAudioBoundaryMs > long.MaxValue / 16) throw new IOException("Неверный формат длительности захвата.");
        state = state with { DurationUncertain = state.DurationUncertain || state.CaptureWasActive,
            TranscriptMayBeIncomplete = state.TranscriptMayBeIncomplete || state.TranscriptionPending || state.CaptureWasActive,
            CaptureWasActive = false, TranscriptionPending = false };
        originMs = state.ActiveCaptureDurationMs;
    }
    public CaptureDurationState Snapshot()
    {
        lock (gate) return state with { ActiveCaptureDurationMs = CurrentMs(), CaptureWasActive = activeSince is not null };
    }
    private long CurrentMs() => originMs + (activeSince is { } start ? Math.Max(0, (long)(clock.Now - start).TotalMilliseconds) : 0);
    private void Persist(CaptureDurationState next)
    {
        next = next with { CheckpointUtc = clock.UtcNow };
        save?.Invoke(next); state = next; lastCheckpoint = clock.Now;
    }
    public void PrepareStart()
    {
        lock (gate) Persist(Snapshot() with { TranscriptionPending = true });
    }
    public void Start()
    {
        lock (gate)
        {
            if (activeSince is not null) return;
            var now = clock.Now;
            Persist(state with { CaptureWasActive = true, TranscriptionPending = true });
            activeSince = now;
        }
    }
    public void Checkpoint()
    {
        lock (gate)
            if (activeSince is not null && (clock.Now - lastCheckpoint).TotalMilliseconds >= CheckpointIntervalMs)
                Persist(Snapshot());
    }
    public void Stop(bool uncertain = false)
    {
        lock (gate)
        {
            originMs = CurrentMs(); activeSince = null;
            Persist(state with { ActiveCaptureDurationMs = originMs, CaptureWasActive = false,
                DurationUncertain = state.DurationUncertain || uncertain });
        }
    }
    public void CompleteTranscription(long? finalizedAudioBoundaryMs = null)
    {
        lock (gate)
        {
            long boundary = finalizedAudioBoundaryMs ?? state.FinalizedAudioBoundaryMs;
            if (boundary < state.FinalizedAudioBoundaryMs || boundary > long.MaxValue / 16)
                throw new IOException("Подтверждённая граница аудио не может уменьшаться.");
            Persist(Snapshot() with { TranscriptionPending = false, FinalizedAudioBoundaryMs = boundary });
        }
    }
    public void MarkTranscriptIncomplete()
    {
        lock(gate)Persist(Snapshot() with{TranscriptMayBeIncomplete=true});
    }
    public static CaptureDurationState? Load(string directory)
    {
        string path = Path.Combine(directory, "capture-duration.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<CaptureDurationState>(File.ReadAllText(path))
            ?? throw new IOException("Пустой файл длительности захвата.") : null;
    }
    public static void Save(string directory, CaptureDurationState state) => AtomicTextFile.Write(
        Path.Combine(directory, "capture-duration.json"), JsonSerializer.Serialize(state));
    public static string Format(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(long)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }
}
