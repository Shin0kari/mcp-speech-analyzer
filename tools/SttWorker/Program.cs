using System.Diagnostics;
using System.Text.Json;
using SpeechAnalyzer.Audio.Windows;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;
using SpeechAnalyzer.Core;
using SttGpuWorker;

// Локальный двоичный stdin: Int32 число отсчётов, затем float32 mono 16 кГц.
// stdout содержит только JSON результата; звук никогда не записывается на диск.
bool candidateInput = args.Contains(SttInputLimits.WorkerFlag, StringComparer.Ordinal);
if (candidateInput) args = args.Where(a => a != SttInputLimits.WorkerFlag).ToArray();
bool originalPcmExpiry = args.Contains("--pcm-expiry-v1", StringComparer.Ordinal);
if (originalPcmExpiry) args = args.Where(a => a != "--pcm-expiry-v1").ToArray();
bool experimentalCuda = args.LastOrDefault() == "--experimental-cuda";
if (experimentalCuda) args = args[..^1];
if (args.Length is < 1 or > 3 || args.Length == 3 && args[2] != "--experimental-beam5") return 2;
try
{
    // Bundled CUDA assets must never opt an ordinary launch into GPU implicitly.
    RuntimeOptions.RuntimeLibraryOrder = SttApplicationConfiguration.RuntimeOrder(experimentalCuda)
        .Select(kind => Enum.Parse<RuntimeLibrary>(kind.ToString())).ToList();
    Guid nonce = Guid.Empty;
    GpuEvidenceTracker? tracker = null;
    IDisposable? logger = null;
    // Явный файловый GPU-опыт: CPU fallback запрещён, native-метаданные идут только в stderr.
    if (experimentalCuda)
    {
        if (!Guid.TryParse(Environment.GetEnvironmentVariable(GpuEvidenceTracker.EnvironmentKey), out nonce) || nonce == Guid.Empty)
            throw new IOException("MissingGpuNonce");
        tracker = new(nonce);
        logger = LogProvider.AddLogger((level, message) => tracker.NativeLog(message, level == WhisperLogLevel.Error));
    }
    using var scopedLogger = logger;
    using var transcriber = new WhisperTranscriber(args[0], args.Length >= 2 ? args[1] : null,
        experimentalBeamSearch: args.Length == 3, collectTimestamps: candidateInput, preserveRawTimingText: candidateInput);
    if (experimentalCuda)
    {
        if (RuntimeOptions.LoadedLibrary != RuntimeLibrary.Cuda)
            throw new InvalidOperationException("CUDA runtime не загружен.");
    }
    await using var observations = SpeechAnalyzer.Core.SttObservationEmitter.FromEnvironment();
    var runtime = Enum.TryParse<SpeechAnalyzer.Core.SttRuntimeKind>(RuntimeOptions.LoadedLibrary?.ToString(), out var kind)
        ? kind : SpeechAnalyzer.Core.SttRuntimeKind.Unknown;
    observations?.Emit(0, SpeechAnalyzer.Core.SttWorkerPhase.RuntimeSelected,
        new(runtime, "whisper.dll", typeof(Whisper.net.WhisperFactory).Assembly.GetName().Version?.ToString()));
    using var input = Console.OpenStandardInput();
    long ordinal = 0;
    await SttWorkerSession.RunAsync(input, Console.Out, originalPcmExpiry, async (samples, token, observe) =>
    {
        async Task<SttWorkerResult> Recognize(Action<SttWorkerPhase> observer)
        {
            string text = await transcriber.TranscribeAsync(samples, token, observer);
            return new(text, transcriber.LastNativeRecognitionUsed, transcriber.LastVadSkipped, false, candidateInput ? transcriber.LastSegments : null);
        }
        return tracker is null ? await Recognize(observe)
            : await GpuWorkerContract.Recognize(tracker, nonce, ++ordinal, Recognize, observe);
    }, observations, experimentalCuda ? GpuWorkerContract.Ready(nonce, runtime) : null, candidateInput);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Локальный STT worker: " + error.GetType().Name); return 1;
}

