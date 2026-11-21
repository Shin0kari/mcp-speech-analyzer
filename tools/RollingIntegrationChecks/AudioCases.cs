using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SpeechAnalyzer.Audio.Windows;
using SpeechAnalyzer.Core;
using SpeechAnalyzer.Testing;

internal static class AudioCases
{
    private const int Rate = BufferedTranscriptSource.SampleRate;
    private const int Main = SttInputLimits.MaxSamples;

    internal static string Hash(float[] samples) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(samples.AsSpan())));

    private static float[] Signal(int count) => Enumerable.Range(0, count).Select(i => 0.5f + (i % 31) / 64f).ToArray();

    public static async Task Protocol()
    {
        float[] expected = Signal(Main);
        using var input = new MemoryStream();
        input.Write(BitConverter.GetBytes(expected.Length));
        input.Write(MemoryMarshal.AsBytes(expected.AsSpan()));
        input.Position = 0;
        using var output = new StringWriter();
        int calls = 0;
        await SttWorkerSession.RunAsync(input, output, false, (pcm, _, _) =>
        {
            calls++;
            Check.Is(pcm.AsSpan().SequenceEqual(expected), "PCM was truncated or padded.");
            return Task.FromResult(new SttWorkerResult("original", false, false, false));
        }, null, candidateInput: true);
        Check.Is(calls == 1 && output.ToString().Contains(SttWorkerSession.CandidateReady), "Protocol v2 readiness missing.");
        foreach (var invalid in new[] { 0, Main + 1 })
            await Check.Reject<IOException>(() => SttWorkerSession.RunAsync(new MemoryStream(BitConverter.GetBytes(invalid)),
                TextWriter.Null, false, NoRecognition, null, candidateInput: true));
        await Check.Reject<EndOfStreamException>(() => SttWorkerSession.RunAsync(new MemoryStream(BitConverter.GetBytes(Main)),
            TextWriter.Null, false, NoRecognition, null, candidateInput: true));
        await Check.Reject<IOException>(() => SttWorkerSession.RunAsync(new MemoryStream(BitConverter.GetBytes(Main)),
            TextWriter.Null, false, NoRecognition, null));
    }

    private static Task<SttWorkerResult> NoRecognition(float[] pcm, CancellationToken token, Action<SttWorkerPhase> observe) =>
        throw new InvalidOperationException("Invalid frame reached the decoder.");

    public static async Task Sender()
    {
        var previous = Environment.GetEnvironmentVariable("SPEECH_ANALYZER_PROTOCOL_FIXTURE");
        Environment.SetEnvironmentVariable("SPEECH_ANALYZER_PROTOCOL_FIXTURE", "1");
        try
        {
            await using var transport = new ProcessAudioTranscriber(Environment.ProcessPath!,
                TestRunOutput.Current.Resolve("--protocol-fixture-model"), candidateInput: true);
            var expected = Signal(Main);
            Check.Is(await transport.TranscribeAsync(expected, CancellationToken.None) == Hash(expected),
                "Production sender and fake child differ.");
            await Check.Reject<ArgumentOutOfRangeException>(() => transport.TranscribeAsync(new float[Main + 1], CancellationToken.None));
            var shortInput = Signal(17);
            Check.Is(await transport.TranscribeAsync(shortInput, CancellationToken.None) == Hash(shortInput),
                "Reused worker changed a short input.");
        }
        finally { Environment.SetEnvironmentVariable("SPEECH_ANALYZER_PROTOCOL_FIXTURE", previous); }
    }

    public static Task Timing()
    {
        using var payload = JsonDocument.Parse("""
            [
              {"StartMs":10.00,"EndMs":29990,"Text":" raw "},
              {"StartMs":100,"EndMs":48360,"Text":" overrun "},
              {"StartMs":200,"EndMs":199,"Text":"reversed"},
              {"StartMs":"bad","EndMs":300,"Text":"malformed"},
              {"EndMs":300,"Text":"missing"}
            ]
            """);
        var timings = SttTiming.Read(payload.RootElement, Main);
        Check.Is(timings.Select(t => t.Status).SequenceEqual(new[] { SttTimingStatus.Valid, SttTimingStatus.OutsideInput,
            SttTimingStatus.Reversed, SttTimingStatus.Malformed, SttTimingStatus.Malformed }) &&
            timings[0].StartMsJson == "10.00" && timings[1].EndMsJson == "48360" && timings[1].Text == " overrun "
            && timings[3].StartMsJson == "\"bad\"" && timings[4].StartMsJson is null, "Raw timing evidence changed.");
        return Task.CompletedTask;
    }

    public static async Task Tail()
    {
        var clock = new ControlledClock();
        var decoder = new Decoder();
        await using var source = new BufferedTranscriptSource(clock, decoder, chunkSeconds: 30);
        source.Start();
        float[] main = Signal(Main);
        clock.Milliseconds = 30000;
        source.PushSamples(AudioChannel.You, 0, main);
        source.PushSamples(AudioChannel.Other, 0, new float[Main]);
        await Ready(source, 30000);
        Check.Is(decoder.Inputs.Count == 1 && decoder.Inputs[0].SequenceEqual(main), "Main block changed.");
        clock.Milliseconds = 31000;
        long cut = source.SnapshotBoundary();
        float[] after = Signal(2 * Rate);
        source.PushSamples(AudioChannel.You, Main, after);
        source.PushSamples(AudioChannel.Other, Main, new float[after.Length]);
        await Ready(source, cut);
        Check.Is(cut == 31000 && decoder.Inputs[1].SequenceEqual(main.TakeLast(4 * Rate).Concat(after.Take(Rate))),
            "Tail input omitted context or used post-cut audio.");
        var tail = source.Segments.Last();
        Check.Is(tail.Text == Decoder.Original && tail.StartMs == 30000 && tail.EndMs == 31000 &&
            tail.SttInput is { DecoderStartSample: 26 * Rate, FreshStartSample: Main, EndSample: 31 * Rate }
            && tail.SttInput.RawTimings.Single().Status == SttTimingStatus.OutsideInput, "Decoder times changed ownership or raw text.");
        Check.Is(source.Segments[0].SttInput is { DecoderStartSample: 0, FreshStartSample: 0, EndSample: Main },
            "Prior main text was replaced.");
        await Ready(source, cut);
        Check.Is(decoder.Inputs.Count == 2, "Empty or repeated tail decoded again.");
        source.Stop();
        await source.WaitForStopCompletionAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check.Is(decoder.Inputs.Count == 3 && decoder.Inputs[2].SequenceEqual(after.Skip(Rate)) &&
            source.Segments.Last().StartMs == 31000 && source.Segments.Last().EndMs == 32000, "Stop used overlap or lost post-cut audio.");
    }

    public static async Task NoLookback()
    {
        var clock = new ControlledClock();
        var decoder = new Decoder();
        await using var source = new BufferedTranscriptSource(clock, decoder, chunkSeconds: 30);
        source.Start();
        clock.Milliseconds = 1000;
        long cut = source.SnapshotBoundary();
        source.PushSamples(AudioChannel.You, 0, Signal(Rate));
        source.PushSamples(AudioChannel.Other, 0, Signal(Rate));
        await Ready(source, cut);
        Check.Is(decoder.Inputs.Count == 2 && decoder.Inputs.All(input => input.Length == Rate)
            && source.Segments.Select(s => s.Channel).Distinct().Count() == 2, "Missing history manufactured samples or mixed channels.");
        clock.Milliseconds = 4000;
        cut = source.SnapshotBoundary();
        source.PushSamples(AudioChannel.You, Rate, Signal(3 * Rate));
        source.PushSamples(AudioChannel.Other, Rate, new float[3 * Rate]);
        await Ready(source, cut);
        Check.Is(decoder.Inputs.Count == 3 && decoder.Inputs.Last().Length == 3 * Rate, "Long tail added lookback.");
        source.Stop();
        await source.WaitForStopCompletionAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check.Is(decoder.Inputs.Count == 3 && source.StopStatus.State == TranscriptStopState.Complete, "Empty Stop submitted audio.");
    }

    public static async Task Gap()
    {
        var clock = new ControlledClock();
        var decoder = new Decoder();
        await using var source = new BufferedTranscriptSource(clock, decoder, chunkSeconds: 30);
        source.Start();
        clock.Milliseconds = 30000;
        source.PushSamples(AudioChannel.You, 0, Signal(Main));
        source.PushSamples(AudioChannel.Other, 0, new float[Main]);
        await Ready(source, 30000);
        source.BeginChannel(AudioChannel.You, 31 * Rate);
        source.BeginChannel(AudioChannel.Other, 31 * Rate);
        clock.Milliseconds = 32000;
        long cut = source.SnapshotBoundary();
        source.PushSamples(AudioChannel.You, 31 * Rate, Signal(Rate));
        source.PushSamples(AudioChannel.Other, 31 * Rate, new float[Rate]);
        await Ready(source, cut);
        Check.Is(decoder.Inputs.Count == 2 && decoder.Inputs.Last().Length == Rate &&
            source.Segments.Last().SttInput is { DecoderStartSample: 31 * Rate, FreshStartSample: 31 * Rate },
            "Tail crossed the discontinuity.");
    }

    public static async Task DurableTail()
    {
        var clock = new ControlledClock();
        var decoder = new Decoder();
        var journal = new SessionJournal(TestRunOutput.Current.NewDirectory("durable-tail"));
        await using var source = new BufferedTranscriptSource(clock, decoder, chunkSeconds: 30, recoveryStore: journal);
        source.Finalized += journal.AppendTranscript;
        source.Start();
        void Push(AudioChannel channel, long start, float[] pcm, int bornSeconds) =>
            source.PushSamples(channel, start, pcm, new(start, start + pcm.Length, 0,
                TimeSpan.FromSeconds(bornSeconds), TimeSpan.FromSeconds(bornSeconds + SttInputLimits.PcmLifetimeSeconds), true));
        clock.Milliseconds = 30000;
        Push(AudioChannel.You, 0, Signal(Main), 0);
        Push(AudioChannel.Other, 0, new float[Main], 0);
        await Ready(source, 30000);
        clock.Milliseconds = 31500;
        long cut = source.SnapshotBoundary();
        Push(AudioChannel.You, Main, Signal(24000), 30);
        Push(AudioChannel.Other, Main, new float[24000], 30);
        await Ready(source, cut);
        var descriptor = decoder.Descriptors.Last();
        Check.Is(descriptor.CandidateInput == true && descriptor.DecoderStart == 26 * Rate && descriptor.Start == Main
            && descriptor.End == Main + 24000 && descriptor.Samples == 88000 &&
            descriptor.Valid(descriptor.Generation, long.MaxValue, clock.Now), "Fresh ownership and decoder proof diverged.");
        clock.Milliseconds = 61500;
        Push(AudioChannel.You, descriptor.End, new float[Main], 31);
        Push(AudioChannel.Other, descriptor.End, new float[Main], 31);
        source.Advance();
        await Ready(source, 61500);
        Check.Is(decoder.Inputs.Count == 2 && journal.Transcript.Count == 2 &&
            journal.Transcript.Last().SttInput!.RawTimings.Single().Text == Decoder.Original &&
            journal.ReadSttRecovery()!.ConfirmedThroughMs == 61500, "Silent coverage or raw text was not durable.");
        source.Stop();
        await source.WaitForStopCompletionAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check.Is(source.StopStatus.State == TranscriptStopState.Complete, "Durable candidate Stop failed.");
    }

    public static async Task BoundedQueue()
    {
        var clock = new ControlledClock();
        var decoder = new Decoder { Hold = Check.Gate() };
        await using var source = new BufferedTranscriptSource(clock, decoder, chunkSeconds: 30);
        source.Start();
        float[] pcm = Signal(Main);
        clock.Milliseconds = 30000;
        source.PushSamples(AudioChannel.You, 0, pcm);
        await decoder.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.PushSamples(AudioChannel.Other, 0, pcm);
        clock.Milliseconds = 60000;
        source.PushSamples(AudioChannel.You, Main, pcm);
        source.PushSamples(AudioChannel.Other, Main, pcm);
        Check.Is(source.Metrics.PendingChunks == SttInputLimits.QueueCapacity + 1, "Unexpected pending limit.");
        clock.Milliseconds = 90000;
        source.PushSamples(AudioChannel.You, 2 * Main, pcm);
        Check.Is(source.Error is not null && source.Metrics.MaximumPendingChunks == SttInputLimits.QueueCapacity + 1,
            "Overload accepted an unbounded job.");
    }

    private static Task Ready(BufferedTranscriptSource source, long cut) =>
        source.WaitForFinalizationAsync(cut, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class Decoder : IExpiringAudioTranscriber, ISttTimingSource
    {
        public const string Original = " raw original ";
        public readonly List<float[]> Inputs = [];
        public readonly List<SttJobDescriptor> Descriptors = [];
        public readonly TaskCompletionSource Entered = Check.Gate();
        public TaskCompletionSource? Hold;
        public IReadOnlyList<SttRawTiming> LastTimings { get; } =
            [new("0", "90000", Original, SttTimingStatus.OutsideInput)];

        public Task<string> TranscribeAsync(float[] samples, CancellationToken token, SttJobDiagnostics? diagnostics)
            => TranscribeAsync(samples, token);

        public Task<string> TranscribeExpiringAsync(float[] samples, SttJobDescriptor descriptor, IClock clock,
            CancellationToken cancellationToken, SttJobDiagnostics? diagnostics)
        {
            Check.Is(descriptor.Valid(descriptor.Generation, long.MaxValue, clock.Now)
                && descriptor.InputSha256 == Hash(samples), "Original PCM proof was lost.");
            Descriptors.Add(descriptor);
            return TranscribeAsync(samples, cancellationToken);
        }

        public async Task<string> TranscribeAsync(float[] samples, CancellationToken token)
        {
            Inputs.Add(samples.ToArray());
            Entered.TrySetResult();
            if (Hold is not null) await Hold.Task.WaitAsync(token);
            return Original;
        }
    }
}
