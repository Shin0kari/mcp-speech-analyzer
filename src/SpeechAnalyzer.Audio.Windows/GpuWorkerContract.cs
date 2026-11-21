using System.Diagnostics;
using System.Text.Json;
using SpeechAnalyzer.Core;
using SttGpuWorker;

namespace SpeechAnalyzer.Audio.Windows;

public sealed record GpuWorkerReady(bool ready, int gpuProtocolVersion, Guid workerNonce, SttRuntimeKind runtime);
public sealed record GpuResultEvidence(int Version, Guid WorkerNonce, long Ordinal, SttRuntimeKind Runtime,
    GpuCallEvidence? Call, long EnumerationEntered, long EnumerationExited, long Frequency);

// GPU metadata extends ready/result only; CPU framing and the shared PCM loop remain compatible.
public static class GpuWorkerContract
{
    public static GpuWorkerReady Ready(Guid nonce, SttRuntimeKind runtime)
    {
        if (nonce == Guid.Empty || runtime != SttRuntimeKind.Cuda) throw new IOException("RequiredCudaRuntimeUnavailable");
        return new(true, 1, nonce, runtime);
    }
    public static void ValidateReady(string? line, Guid nonce)
    {
        if (line is null || line.Length > 4096) throw new IOException("MissingGpuReady");
        var ready = JsonSerializer.Deserialize<GpuWorkerReady>(line);
        if (ready is not { ready: true, gpuProtocolVersion: 1, runtime: SttRuntimeKind.Cuda } || ready.workerNonce != nonce)
            throw new IOException("InvalidGpuReady");
    }
    public static bool Valid(GpuResultEvidence? value, Guid nonce, long ordinal, bool nativeUsed) =>
        value is { Version: 1, Runtime: SttRuntimeKind.Cuda } && value.WorkerNonce == nonce && value.Ordinal == ordinal
        && value.Frequency == Stopwatch.Frequency
        && (nativeUsed ? GpuEvidenceTracker.Valid(value.Call, nonce, ordinal)
            && value.Call!.OpenedTicks <= value.EnumerationEntered && value.EnumerationEntered > 0
            && value.EnumerationEntered <= value.Call.SignalTicks && value.Call.SignalTicks <= value.EnumerationExited
            && value.EnumerationExited <= value.Call.ClosedTicks
            : value.Call is null && value.EnumerationEntered == 0 && value.EnumerationExited == 0);

    public static async Task<SttWorkerResult> Recognize(GpuEvidenceTracker tracker, Guid nonce, long ordinal,
        Func<Action<SttWorkerPhase>, Task<SttWorkerResult>> recognize, Action<SttWorkerPhase> observe)
    {
        long entered = 0, exited = 0;
        tracker.Begin(ordinal);
        GpuCallEvidence call;
        SttWorkerResult result;
        try
        {
            result = await recognize(phase =>
            {
                if (phase == SttWorkerPhase.WhisperEnumerationEntered) entered = Stopwatch.GetTimestamp();
                if (phase == SttWorkerPhase.WhisperEnumerationExited) exited = Stopwatch.GetTimestamp();
                observe(phase);
            });
        }
        finally { call = tracker.End(); }
        var evidence = new GpuResultEvidence(1, nonce, ordinal, SttRuntimeKind.Cuda,
            result.nativeRecognitionUsed ? call : null, entered, exited, Stopwatch.Frequency);
        if (!Valid(evidence, nonce, ordinal, result.nativeRecognitionUsed)) throw new IOException("MissingCallCudaEvidence");
        return result with { gpuExecutionObserved = result.nativeRecognitionUsed, gpuEvidence = evidence };
    }
}
