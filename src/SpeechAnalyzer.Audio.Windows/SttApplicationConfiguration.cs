using SpeechAnalyzer.Core;

namespace SpeechAnalyzer.Audio.Windows;

public sealed record SttApplicationConfiguration(string WorkerPath, bool Cuda, bool OriginalPcmExpiry,
    bool WorkerObservations, string? CudaDependencyDirectory, bool CandidateInput = false)
{
    public static SttRuntimeKind[] RuntimeOrder(bool cuda) => cuda ? [SttRuntimeKind.Cuda] : [SttRuntimeKind.Cpu, SttRuntimeKind.CpuNoAvx];
    public static SttApplicationConfiguration Resolve(string baseDirectory, IEnumerable<string> arguments,
        Func<string, string?> environment)
    {
        var args = arguments.ToHashSet(StringComparer.Ordinal);
        bool cuda = args.Contains("--gpu-stt-v1") || args.Contains("--experimental-cuda-stt");
        return new(Path.Combine(baseDirectory, "stt-worker", "SttWorker.exe"), cuda,
            args.Contains("--pre-gap-drain"), environment("SPEECH_ANALYZER_STT_PHASES") == "1",
            cuda ? Path.Combine(baseDirectory, "cuda-bin") : null, args.Contains(SttInputLimits.CandidateFlag));
    }
    public ProcessAudioTranscriber Create(string modelPath, string? vadPath = null,
        Action<SttTransportEvent>? observe = null) => new(WorkerPath, modelPath, vadModelPath: vadPath,
            experimentalCuda: Cuda, originalPcmExpiry: OriginalPcmExpiry,
            workerObservations: WorkerObservations, cudaDependencyDirectory: CudaDependencyDirectory, observe: observe, candidateInput: CandidateInput);
}

// Optional in-memory consumer owns its capacity and export. No text/PCM is exposed.
public sealed record SttTransportEvent(string Phase, Guid WorkerId, Guid JobId, long Ordinal,
    long Ticks, long Frequency, double? BudgetMs = null, long? PcmExpiry100ns = null,
    SttWorkerObservation? Child = null, GpuResultEvidence? Gpu = null, int? ExitCode = null);
