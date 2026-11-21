using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

public enum CaptureTimingKind
{
    Acquire = 1, Release, NextPacketSize, Receive, RecorderSubmit, TimelinePush,
    RecorderLock, TimelineLock, SourceLock, EventLogLock, EventWrite,
    NativeTimestampStep, TimelineGapDetected, TimelineErrorDetected, NativeError,
    SttSubmission, RecoverySave, RecoveryLock, RecoverySerialize, RecoveryWrite
}

// Только числа. Start/End — raw Stopwatch ticks; packet QPC уже в100нс.
[StructLayout(LayoutKind.Sequential)]
public readonly record struct CaptureTimingRecord(long Sequence, CaptureTimingKind Kind, long Start, long End,
    long PacketQpc100ns, long DevicePosition, int Frames, int Bytes, int Flags, int Outcome, long Value);

public sealed record CaptureTimingLaneSnapshot(int Channel, long Observed, long Overwritten, long Dropped,
    long EmptyAcquisitions, long TriggerObservedAt, CaptureTimingRecord[] Before, CaptureTimingRecord[] After,
    CaptureTimingRecord LastReleaseBeforeTrigger, CaptureTimingRecord LastNonemptyAcquireBeforeTrigger,
    CaptureTimingRecord LastNonemptyReleaseBeforeTrigger, CaptureTimingRecord AcquireAtTrigger,
    CaptureTimingRecord FirstReleaseAfterTrigger)
{
    public CaptureFirstBlockSnapshot? FirstBlock { get; init; }
    public long PhaseStarts { get; init; }
    public long PhaseEnds { get; init; }
    public int OpenPhases { get; init; }
    public long RejectedThreadOperations { get; init; }
    public long UnreleasedNativeSequence { get; init; }
    public long NativeErrors { get; init; }
}

public sealed record CaptureTimingSnapshot(int Version, long Frequency, long TriggerTicks, int TriggerChannel,
    CaptureTimingKind TriggerKind, long TriggerValue, int BeforeCapacity, int AfterCapacity, long PostTicks,
    CaptureTimingLaneSnapshot[] Channels)
{
    public long FirstBlockTicks { get; init; }
    public bool AfterJoin { get; init; }
    public string? PackageId { get; init; }
    public string? PackageManifestSha256 { get; init; }
    public AudioDiagnosticStatus? DiagnosticStatusAtExport { get; init; }
    public bool DiagnosticFlushSucceeded { get; init; }
}

// Единственная общая hot-path операция — CAS первой аномалии. Выгрузка только после join.
public sealed class CaptureTimingRun
{
    public const int BeforeCapacity = 8192, AfterCapacity = 8192;
    public readonly CaptureTimingLane You, Other;
    public readonly long Frequency, PostTicks;
    private long triggerTicks;
    private long firstBlockTicks;
    public long FirstBlockTicks => Volatile.Read(ref firstBlockTicks);
    internal void BeginFirstBlock(long at) => Interlocked.CompareExchange(ref firstBlockTicks, Math.Max(1, at), 0);
    private static int outputBusy;
    private int triggerChannel = -1;
    private CaptureTimingKind triggerKind;
    private long triggerValue;
    public long TriggerTicks => Volatile.Read(ref triggerTicks);
    public CaptureTimingRun(long frequency = 0)
    {
        Frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        PostTicks = Frequency; // Не больше1с post history; terminal release имеет отдельный слот.
        You = new(this, 0); Other = new(this, 1);
    }
    internal void Trigger(int channel, CaptureTimingKind kind, long at, long value)
    {
        if (Interlocked.CompareExchange(ref triggerTicks, Math.Max(1, at), 0) != 0) return;
        triggerChannel = channel; triggerKind = kind; triggerValue = value;
    }
    // Вызывать только после завершения обоих владельцев lane, не из callback/UI Pulse.
    public CaptureTimingSnapshot SnapshotAfterJoin() => new(2, Frequency, TriggerTicks, triggerChannel,
        triggerKind, triggerValue, BeforeCapacity, AfterCapacity, PostTicks, [You.SnapshotAfterJoin(), Other.SnapshotAfterJoin()])
        { FirstBlockTicks = FirstBlockTicks, AfterJoin = true };

    // Отдельный writer не участвует в PCM/diagnostic admission; timeout не отменяет зависший OS I/O.
    public static async Task<string?> SaveAfterJoinAsync(CaptureTimingSnapshot snapshot, string path,
        Action<CaptureTimingSnapshot>? output = null)
    {
        if (Interlocked.CompareExchange(ref outputBusy, 1, 0) != 0) return "Предыдущая выгрузка измерения ещё не завершена.";
        var task = Task.Run(() =>
        {
            try
            {
                if (output is not null) { output(snapshot); return; }
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                JsonSerializer.Serialize(file, snapshot); file.Flush(true);
            }
            finally { Volatile.Write(ref outputBusy, 0); }
        });
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); return null; }
        catch (Exception e)
        {
            // Наблюдаем и поздний fault, не связывая его с жизненным циклом recorder.
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return "Измерение не сохранено: " + e.GetType().Name;
        }
    }
}

public sealed class CaptureTimingLane
{
    private readonly CaptureTimingRun run;
    public int Channel { get; }
    private readonly CaptureTimingRecord[] before = new CaptureTimingRecord[CaptureTimingRun.BeforeCapacity];
    private readonly CaptureTimingRecord[] after = new CaptureTimingRecord[CaptureTimingRun.AfterCapacity];
    private long observed, overwritten, dropped, empty, sequence, previousQpc;
    private int beforeWrite, beforeCount, afterCount, previousFrames;
    private long triggerObserved;
    private CaptureTimingRecord acquire, lastRelease, completedAcquire, completedRelease;
    private CaptureTimingRecord frozenRelease, frozenAcquire, frozenNonemptyRelease, frozenCurrent, firstRelease;
    private long packetQpc, device;
    private int frames, bytes, flags;
    private readonly CaptureFirstBlockLane firstBlock;
    private int ownerThread, openPhases;
    private long rejectedThreadOperations, phaseStarts, phaseEnds, unreleasedNativeSequence, nativeErrors;
    internal CaptureTimingLane(CaptureTimingRun run, int channel) { this.run = run; Channel = channel; firstBlock = new(run); }
    private bool IsOwner()
    {
        int id = Environment.CurrentManagedThreadId;
        if (ownerThread == 0) Interlocked.CompareExchange(ref ownerThread, id, 0);
        if (ownerThread == id) return true;
        Interlocked.Increment(ref rejectedThreadOperations); return false;
    }
    internal bool BeginPhase(CaptureTimingKind kind, long at)
    {
        if (!IsOwner()) return false;
        openPhases++; phaseStarts++;
        if (kind == CaptureTimingKind.SttSubmission) { run.BeginFirstBlock(at); firstBlock.SubmissionStarted(at); }
        return true;
    }
    internal void EndPhase(CaptureTimingKind kind, long at)
    {
        if (!IsOwner()) return;
        openPhases--; phaseEnds++;
        if (kind == CaptureTimingKind.SttSubmission) firstBlock.SubmissionEnded(at);
    }
    private void ObserveTrigger(long at)
    {
        if (triggerObserved != 0 || run.TriggerTicks == 0) return;
        triggerObserved = Math.Max(1, at);
        frozenRelease = lastRelease; frozenAcquire = completedAcquire; frozenNonemptyRelease = completedRelease;
        frozenCurrent = acquire;
    }
    public void Write(CaptureTimingKind kind, long start, long end, int outcome = 0, long value = 0)
    {
        if (!IsOwner()) return;
        if (outcome < 0) nativeErrors++;
        if (outcome < 0) run.Trigger(Channel, CaptureTimingKind.NativeError, end, outcome);
        ObserveTrigger(end);
        var row = new CaptureTimingRecord(sequence, kind, start, end, packetQpc, device, frames, bytes, flags, outcome, value);
        firstBlock.Write(row, acquire, lastRelease);
        observed++;
        if (triggerObserved == 0)
        {
            if (beforeCount == before.Length) overwritten++; else beforeCount++;
            before[beforeWrite] = row; beforeWrite = (beforeWrite + 1) % before.Length;
        }
        else if (end - run.TriggerTicks <= run.PostTicks && afterCount < after.Length) after[afterCount++] = row;
        else dropped++;
        if (kind == CaptureTimingKind.Release)
        {
            if (outcome >= 0) unreleasedNativeSequence = 0;
            if (triggerObserved != 0 && firstRelease.Kind == 0) firstRelease = row;
            lastRelease = row;
            if (frames > 0 && outcome >= 0) { completedAcquire = acquire; completedRelease = row; }
        }
    }
    public void Acquired(long start, long end, int frameCount, int byteCount, int rawFlags,
        long devicePosition, long qpc100ns, int outcome)
    {
        if (!IsOwner()) return;
        ObserveTrigger(end);
        sequence++; frames = frameCount; bytes = byteCount; flags = rawFlags; device = devicePosition; packetQpc = qpc100ns;
        acquire = new(sequence, CaptureTimingKind.Acquire, start, end, packetQpc, device, frames, bytes, flags, outcome, 0);
        if (frames > 0 && outcome >= 0) unreleasedNativeSequence = sequence;
        // Только диагностический триггер Other. Неполный startup You не меняет policy адаптера.
        if (outcome >= 0 && frameCount > 0 && Channel == 1)
        {
            long delta = previousFrames == 0 ? 0 : (long)Math.Round((qpc100ns - previousQpc) * .0016) - previousFrames;
            if (Math.Abs(delta) > 1) Anomaly(CaptureTimingKind.NativeTimestampStep, end, delta);
            previousQpc = qpc100ns; previousFrames = frameCount;
        }
        if (frameCount == 0) empty++;
        Write(CaptureTimingKind.Acquire, start, end, outcome);
    }
    public void Anomaly(CaptureTimingKind kind, long at, long value = 0)
    {
        if (!IsOwner()) return;
        run.Trigger(Channel, kind, at, value); Write(kind, at, at, value: value);
    }
    internal CaptureTimingLaneSnapshot SnapshotAfterJoin()
    {
        ObserveTrigger(Stopwatch.GetTimestamp());
        var history = new CaptureTimingRecord[beforeCount];
        int first = (beforeWrite - beforeCount + before.Length) % before.Length;
        for (int i = 0; i < history.Length; i++) history[i] = before[(first + i) % before.Length];
        return new(Channel, observed, overwritten, dropped, empty, triggerObserved, history, after.AsSpan(0, afterCount).ToArray(),
            frozenRelease, frozenAcquire, frozenNonemptyRelease, frozenCurrent, firstRelease)
        {
            FirstBlock = firstBlock.SnapshotAfterJoin(), PhaseStarts = phaseStarts, PhaseEnds = phaseEnds,
            OpenPhases = openPhases, RejectedThreadOperations = rejectedThreadOperations,
            UnreleasedNativeSequence = unreleasedNativeSequence, NativeErrors = nativeErrors
        };
    }
}

public static class CaptureTiming
{
    [ThreadStatic] private static CaptureTimingLane? current;
    public static CaptureTimingLane? Current => current;
    public static long Now => current is null ? 0 : Stopwatch.GetTimestamp();
    public static Binding Bind(CaptureTimingLane? lane) => new(lane);
    public readonly struct Binding : IDisposable
    {
        private readonly CaptureTimingLane? previous;
        public Binding(CaptureTimingLane? lane) { previous = current; current = lane; }
        public void Dispose() => current = previous;
    }
    public static PhaseScope Phase(CaptureTimingKind kind) => new(kind);
    public static PhaseScope Phase(CaptureTimingKind kind, long value) => new(kind, value);
    public readonly struct PhaseScope : IDisposable
    {
        private readonly CaptureTimingLane? lane;
        private readonly long start;
        private readonly CaptureTimingKind kind;
        private readonly long value;
        public PhaseScope(CaptureTimingKind kind) : this(kind, 0) { }
        public PhaseScope(CaptureTimingKind kind, long value)
        {
            lane = current; this.kind = kind; this.value = value; start = lane is null ? 0 : Stopwatch.GetTimestamp();
            if (lane is not null && !lane.BeginPhase(kind, start)) lane = null;
        }
        public void Dispose()
        {
            if (lane is null) return;
            long end = Stopwatch.GetTimestamp(); lane.EndPhase(kind, end); lane.Write(kind, start, end, value: value);
        }
    }
    // Сохраняет прежнюю Monitor-семантику, включая reentrant lock и finally при исключении.
    public static LockScope Lock(object gate, CaptureTimingKind kind) => new(gate, kind);
    public readonly struct LockScope : IDisposable
    {
        private readonly object gate;
        public LockScope(object gate, CaptureTimingKind kind)
        {
            this.gate = gate; var lane = current; long start = lane is null ? 0 : Stopwatch.GetTimestamp();
            Monitor.Enter(gate);
            if (lane is not null) lane.Write(kind, start, Stopwatch.GetTimestamp());
        }
        public void Dispose() => Monitor.Exit(gate);
    }
    public static void Gap(long samples) => current?.Anomaly(CaptureTimingKind.TimelineGapDetected, Stopwatch.GetTimestamp(), samples);
    public static void Error() => current?.Anomaly(CaptureTimingKind.TimelineErrorDetected, Stopwatch.GetTimestamp());
}
