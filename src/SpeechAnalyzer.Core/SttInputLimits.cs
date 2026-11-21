namespace SpeechAnalyzer.Core;

public static class SttInputLimits
{
    public const int MainSeconds = 30;
    public const int LegacyMaxSamples = 8 * BufferedTranscriptSource.SampleRate;
    public const int MaxSamples = MainSeconds * BufferedTranscriptSource.SampleRate;
    public const int QueueCapacity = 3;
    public const int QueueWaitSeconds = 15;
    public const int InferenceBudgetSeconds = 20;
    public const int LegacyPcmLifetimeSeconds = 30;
    public const int PcmLifetimeSeconds = MainSeconds + QueueWaitSeconds + InferenceBudgetSeconds + 1;
    public const int LegacyProofCapacity = 2048;
    public const int ProofCapacity = 4096;
    public const string CandidateFlag = "--rolling-asr-v1";
    public const string WorkerFlag = "--asr-input-v2";
}
