namespace SpeechAnalyzer.Core;

public sealed partial class BufferedTranscriptSource
{
    private const int ShortTailSamples = 2 * SampleRate;
    private const int LookbackCapacity = 4 * SampleRate;
    private static readonly TimeSpan LookbackAge = TimeSpan.FromSeconds(4);

    private sealed class Lookback
    {
        public readonly float[] Samples = new float[LookbackCapacity];
        public readonly List<PcmRangeProof> Proof = [];
        public long End;
        public int Count;
        public TimeSpan Updated;
    }

    private static void ClearLookback(Buffer buffer)
    {
        if (buffer.History is not { } history) return;
        Array.Clear(history.Samples, 0, history.Count);
        history.Count = 0;
        history.Proof.Clear();
    }

    private int AvailableLookback(Buffer buffer)
    {
        if (buffer.History is not { Count: > 0 } history || history.End != buffer.Start) return 0;
        if (clock.Now - history.Updated >= LookbackAge) return 0;
        if (recoveryStore is null) return history.Count;
        long cursor = buffer.Start;
        for (int i = history.Proof.Count - 1; i >= 0; i--)
        {
            var proof = history.Proof[i];
            if (proof.End != cursor || !proof.Trusted || proof.Expires <= clock.Now) break;
            cursor = proof.Start;
        }
        return (int)(buffer.Start - cursor);
    }

    private void RememberLookback(Buffer buffer, int count)
    {
        if (!candidateInput) return;
        var history = buffer.History ??= new();
        int kept = Math.Min(AvailableLookback(buffer), Math.Max(0, LookbackCapacity - count));
        Array.Copy(history.Samples, history.Count - kept, history.Samples, 0, kept);
        int fresh = Math.Min(count, LookbackCapacity);
        buffer.Samples.CopyTo(count - fresh, history.Samples, kept, fresh);
        if (history.Count > kept + fresh)
            Array.Clear(history.Samples, kept + fresh, history.Count - kept - fresh);
        history.Count = kept + fresh;
        history.End = buffer.Start + count;
        history.Updated = clock.Now;
        long earliest = history.End - history.Count;
        history.Proof.RemoveAll(p => p.End <= earliest || p.End > buffer.Start || kept == 0);
        foreach (var proof in buffer.Proof)
        {
            if (proof.Start >= history.End || history.Proof.Count >= ProofCapacity) break;
            if (proof.End > earliest) history.Proof.Add(proof with { End = Math.Min(proof.End, history.End) });
        }
        if (history.Proof.Count > 0 && history.Proof[0].Start < earliest)
            history.Proof[0] = history.Proof[0] with { Start = earliest };
    }
}
