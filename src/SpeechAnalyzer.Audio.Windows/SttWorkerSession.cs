using System.Diagnostics;
using System.Text.Json;
using SpeechAnalyzer.Core;

namespace SpeechAnalyzer.Audio.Windows;

public sealed record SttWorkerResult(string text, bool nativeRecognitionUsed, bool vadSkipped, bool gpuExecutionObserved,
    object? segments = null, long? workingSetBytes = null, long? peakWorkingSetBytes = null,
    GpuResultEvidence? gpuEvidence = null);

// Общий production protocol loop; тесты подменяют только recognition delegate и потоки.
public static class SttWorkerSession
{
    public const string CandidateReady = "{\"asrInputVersion\":2}";
    public static async Task RunAsync(Stream input, TextWriter output, bool originalPcmExpiry,
        Func<float[], CancellationToken, Action<SttWorkerPhase>, Task<SttWorkerResult>> recognize,
        SttObservationEmitter? observations, GpuWorkerReady? gpuReady = null, bool candidateInput = false)
    {
        output.WriteLine(gpuReady is null ? "{\"ready\":true}" : JsonSerializer.Serialize(gpuReady));
        if (candidateInput) output.WriteLine(CandidateReady);
        output.Flush();
        byte[] header = new byte[4]; long ordinal = 0;
        while (true)
        {
            if (await input.ReadAsync(header.AsMemory(0, 1)).ConfigureAwait(false) == 0) return;
            await input.ReadExactlyAsync(header.AsMemory(1)).ConfigureAwait(false);
            int count = BitConverter.ToInt32(header);
            if (count < 1 || count > (candidateInput ? SttInputLimits.MaxSamples : SttInputLimits.LegacyMaxSamples)) throw new IOException("Неверный размер PCM-блока.");
            long current = ++ordinal;
            void Observe(SttWorkerPhase phase) => observations?.Emit(current, phase);
            long? expires = null;
            if (originalPcmExpiry)
            {
                byte[] timestamp = new byte[8]; await input.ReadExactlyAsync(timestamp).ConfigureAwait(false);
                expires = BitConverter.ToInt64(timestamp); Array.Clear(timestamp);
            }
            Observe(SttWorkerPhase.HeaderAccepted);
            long QpcNow() => (long)Math.Ceiling(Stopwatch.GetTimestamp() * (10_000_000m / Stopwatch.Frequency));
            if (expires is { } beforeRead && beforeRead <= QpcNow()) throw new IOException("PCM истёк до чтения.");
            int expiryArmed = 1;
            using var expiryWatchdog = expires is { } absolute
                ? new Timer(_ => { if (Interlocked.Exchange(ref expiryArmed, 0) == 1) Environment.Exit(3); }, null,
                    TimeSpan.FromTicks(Math.Max(1, absolute - QpcNow())), Timeout.InfiniteTimeSpan) : null;
            using var cancellation = new CancellationTokenSource();
            if (expires is { } cancellationAt) cancellation.CancelAfter(TimeSpan.FromTicks(Math.Max(1, cancellationAt - QpcNow())));
            byte[] bytes = new byte[count * sizeof(float)]; float[] samples = new float[count];
            try
            {
                await input.ReadExactlyAsync(bytes, cancellation.Token).ConfigureAwait(false);
                Observe(SttWorkerPhase.InputReadCompleted);
                cancellation.Token.ThrowIfCancellationRequested(); Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                Observe(SttWorkerPhase.ManagedProcessingEntered);
                var result = await recognize(samples, cancellation.Token, Observe).ConfigureAwait(false);
                Observe(SttWorkerPhase.ManagedProcessingExited);
                cancellation.Token.ThrowIfCancellationRequested();
                Observe(SttWorkerPhase.ResultSerializationEntered);
                string json = JsonSerializer.Serialize(result);
                Observe(SttWorkerPhase.ResultSerialized);
                output.WriteLine(json); output.Flush();
                Observe(SttWorkerPhase.ResultSendCompleted);
            }
            finally { Interlocked.Exchange(ref expiryArmed, 0); Array.Clear(bytes); Array.Clear(samples); }
        }
    }
}
