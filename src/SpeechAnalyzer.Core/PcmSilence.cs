namespace SpeechAnalyzer.Core;

public enum PcmEncoding { UnknownFloat, SignedPcm16Div32768 }
public enum PcmInputClass { Uncertain, DigitalZero, Pcm16QuantizationFloor }

public static class PcmSilence
{
    public const string ClassifierVersion = "pcm16-floor-v1";
    // Это граница кодов квантования, не порог громкости и не VAD.
    public static PcmInputClass Classify(ReadOnlySpan<float> samples, PcmEncoding encoding)
    {
        if (samples.IsEmpty) return PcmInputClass.Uncertain;
        bool zero = true;
        foreach (float sample in samples)
        {
            if (sample == 0f) continue;
            zero = false;
            if (encoding != PcmEncoding.SignedPcm16Div32768 || (sample != -1f / 32768 && sample != 1f / 32768))
                return PcmInputClass.Uncertain;
        }
        return zero ? PcmInputClass.DigitalZero : PcmInputClass.Pcm16QuantizationFloor;
    }
    // Только точные нули, включая -0. Ненулевой шум и тихая речь проходят без обрезки.
    public static bool IsDigitalSilence(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples) if (sample != 0f) return false;
        return true;
    }
}
