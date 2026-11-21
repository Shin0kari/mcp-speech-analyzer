using System.Text;
using SpeechAnalyzer.Core;
using Whisper.net;

namespace SpeechAnalyzer.Audio.Windows;

public sealed class WhisperTranscriber : IAudioTranscriber, IDisposable
{
    private readonly WhisperFactory factory;
    private readonly WhisperProcessor processor;
    private readonly WhisperVadFactory? vadFactory;
    private readonly WhisperVadProcessor? vad;
    private readonly SemaphoreSlim serial = new(1);
    public bool LastNativeRecognitionUsed { get; private set; }
    public bool LastVadSkipped { get; private set; }
    public sealed record TimedHypothesis(double StartMs, double EndMs, string Text);
    public IReadOnlyList<TimedHypothesis> LastSegments { get; private set; } = [];
    private readonly bool collectTimestamps, preserveRawTimingText;
    public WhisperTranscriber(string modelPath, string? vadModelPath = null, bool experimentalBeamSearch = false,
        bool collectTimestamps = false, bool preserveRawTimingText = false)
    {
        this.collectTimestamps = collectTimestamps;
        this.preserveRawTimingText = preserveRawTimingText;
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Выберите локальную многоязычную модель Whisper.", modelPath);
        factory = WhisperFactory.FromPath(modelPath);
        var builder = factory.CreateBuilder().WithLanguage("ru").WithNoContext()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8)).WithNoSpeechThreshold(0.6f);
        // Только явно заданный файловый эксперимент; штатный декодер не меняется.
        if (experimentalBeamSearch) builder.WithBeamSearchSamplingStrategy(strategy => strategy.WithBeamSize(5));
        processor = builder.Build();
        try
        {
            if (vadModelPath is not null)
            {
                vadFactory = WhisperVadFactory.FromPath(vadModelPath);
                vad = vadFactory.CreateBuilder().WithUseGpu(false).WithThreads(2).WithThreshold(0.5f)
                    .WithMinSpeechDuration(TimeSpan.FromMilliseconds(100)).WithMinSilenceDuration(TimeSpan.FromMilliseconds(100))
                    .WithSpeechPadding(TimeSpan.FromMilliseconds(150)).WithSamplesOverlap(TimeSpan.Zero).Build();
            }
        }
        catch { vadFactory?.Dispose(); processor.Dispose(); factory.Dispose(); serial.Dispose(); throw; }
    }
    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken) => TranscribeAsync(samples, cancellationToken, null);
    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken, Action<SttWorkerPhase>? observe)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastNativeRecognitionUsed = false; LastVadSkipped = false;
            LastSegments = [];
            if (PcmSilence.IsDigitalSilence(samples)) return "";
            // Проверка только всего блока: границы слов и временная шкала не обрезаются.
            if (vad is not null && vad.DetectSpeech(samples).Count == 0)
            { LastVadSkipped = true; return ""; }
            cancellationToken.ThrowIfCancellationRequested();
            LastNativeRecognitionUsed = true;
            var text = new StringBuilder();
            var segments = collectTimestamps ? new List<TimedHypothesis>() : null;
            // Это граница managed enumeration; native state allocation/вызов находятся внутри Whisper.net.
            observe?.Invoke(SttWorkerPhase.WhisperEnumerationEntered);
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    if (text.Length > 0) text.Append(' ');
                    text.Append(segment.Text.Trim());
                    segments?.Add(new(segment.Start.TotalMilliseconds, segment.End.TotalMilliseconds, preserveRawTimingText ? segment.Text : segment.Text.Trim()));
                }
            observe?.Invoke(SttWorkerPhase.WhisperEnumerationExited);
            LastSegments = segments is null ? [] : segments;
            return text.ToString();
        }
        finally { serial.Release(); }
    }
    public void Dispose() { vad?.Dispose(); vadFactory?.Dispose(); processor.Dispose(); factory.Dispose(); serial.Dispose(); }
}
