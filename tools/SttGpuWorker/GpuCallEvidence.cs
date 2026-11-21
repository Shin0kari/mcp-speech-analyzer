using System.Diagnostics;

namespace SttGpuWorker;

public sealed record GpuCallEvidence(int Version, Guid WorkerNonce, long Ordinal, long OpenedTicks,
    long ClosedTicks, long SignalTicks, int SignalCount, int NativeErrors, string Source);

// A synchronous native callback is accepted only inside one active recognition scope.
// Startup and between-call messages never become evidence for a later call.
public sealed class GpuEvidenceTracker(Guid workerNonce)
{
    public const string EnvironmentKey = "SPEECH_ANALYZER_GPU_MEASUREMENT_NONCE";
    public const string Source = "whisper_backend_init_gpu/CUDA0/v1";
    private readonly object gate = new();
    private long ordinal, opened, signal;
    private int count, errors;
    private bool active;
    public void Begin(long currentOrdinal)
    {
        lock (gate)
        {
            if (active || currentOrdinal != ordinal + 1) throw new InvalidOperationException("GpuScopeOrder");
            ordinal = currentOrdinal; opened = Stopwatch.GetTimestamp(); signal = 0; count = 0; errors = 0; active = true;
        }
    }
    public void NativeLog(string? message, bool error)
    {
        lock (gate)
        {
            if (!active) return;
            if (error) errors++;
            if (message?.TrimEnd() == "whisper_backend_init_gpu: using CUDA0 backend")
            { count++; if (signal == 0) signal = Stopwatch.GetTimestamp(); }
        }
    }
    public GpuCallEvidence End()
    {
        lock (gate)
        {
            if (!active) throw new InvalidOperationException("NoGpuScope");
            active = false;
            return new(1, workerNonce, ordinal, opened, Stopwatch.GetTimestamp(), signal, count, errors, Source);
        }
    }
    public static bool Valid(GpuCallEvidence? value, Guid expectedNonce, long expectedOrdinal) => value is not null
        && value.Version == 1 && value.Source == Source && value.WorkerNonce == expectedNonce && value.Ordinal == expectedOrdinal
        && value.SignalCount > 0 && value.NativeErrors == 0 && value.OpenedTicks > 0
        && value.SignalTicks >= value.OpenedTicks && value.SignalTicks <= value.ClosedTicks;
}
