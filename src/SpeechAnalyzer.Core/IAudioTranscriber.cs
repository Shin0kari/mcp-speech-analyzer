namespace SpeechAnalyzer.Core;

public interface IAudioTranscriber
{
    Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken);
}
