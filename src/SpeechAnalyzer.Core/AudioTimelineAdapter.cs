namespace SpeechAnalyzer.Core;

public sealed record AudioTimelineStatus(long RequestedMs, long ReceivedMs, long RecognizedMs,
    int ReorderedPackets, int DuplicatePackets, int PendingPackets, string? Error, long YouSamples, long OtherSamples);

// QPC приходит в единицах 100 нс. devicePosition может считать native-кадры
// или оставаться нулём в process loopback; он не используется как индекс PCM 16 кГц.
public sealed class AudioTimelineAdapter(BufferedTranscriptSource source, IClock clock,
    Action<AudioTimelineDiagnostic>? diagnosticSink = null)
{
    private readonly object gate = new();
    private readonly StreamState[] streams = [new(), new()];
    private long originQpc, originSample;
    private bool accepting;
    private string? error;
    private int reordered, duplicates;
    private readonly Queue<AudioPacketTiming> timing = new();
    private readonly AudioPacketTiming?[] firstTiming = new AudioPacketTiming?[2];
    private long packetsSeen;
    private sealed class StreamState
    {
        public bool Initialized, AwaitingAnchor;
        public Packet? Startup;
        public long Expected, Delivered;
        public readonly SortedDictionary<long, Packet> Pending = [];
    }
    private sealed record Packet(long StartSample, float[] Samples, TimeSpan Arrived, PcmRangeProof? Proof = null, PcmEncoding Encoding = PcmEncoding.UnknownFloat);
    public void Begin(long qpc100ns)
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.TimelineLock))
        {
            if (accepting) throw new InvalidOperationException("Аудиошкала уже принимает пакеты.");
            if (error is not null) throw new IOException(error);
            originQpc = qpc100ns; originSample = source.ResumeSample;
            timing.Clear(); Array.Clear(firstTiming); packetsSeen = 0;
            for (int i = 0; i < streams.Length; i++)
            {
                streams[i].Initialized = false;
                streams[i].AwaitingAnchor = source.AwaitingChannel((AudioChannel)i);
                streams[i].Expected = originSample;
            }
            accepting = true;
        }
    }
    public void Push(AudioChannel channel, long qpc100ns, long devicePosition, ReadOnlySpan<float> samples,
        bool discontinuity = false, bool timestampError = false, bool silent = false, PcmEncoding encoding = PcmEncoding.UnknownFloat)
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.TimelineLock))
        {
            if (!accepting || error is not null || samples.Length == 0) return;
            if (channel is not (AudioChannel.You or AudioChannel.Other)) throw new ArgumentOutOfRangeException(nameof(channel));
            var stream = streams[(int)channel];
            long start = originSample + (long)Math.Round((qpc100ns - originQpc) * (16000d / 10_000_000));
            float peak = 0;
            foreach (float sample in samples) peak = Math.Max(peak, Math.Abs(sample));
            var metadata = new AudioPacketTiming(++packetsSeen, channel, qpc100ns, devicePosition,
                samples.Length, start, stream.Expected, stream.Delivered, discontinuity, timestampError,
                source.CaptureBoundaryMs, clock.Now.TotalMilliseconds, silent, peak);
            firstTiming[(int)channel] ??= metadata;
            if (timing.Count == 1024) timing.Dequeue();
            timing.Enqueue(metadata);
            if (source.GapContinuationEnabled && (stream.Initialized || timestampError || devicePosition < 0 || start < originSample || stream.AwaitingAnchor))
            {
                if (timestampError || discontinuity || devicePosition < 0 || start < originSample)
                {
                    ClearStartup(stream);
                    if (stream.Pending.Count != 0) RecoverPending(channel, stream, true);
                    source.TransitionGap(channel, stream.Expected, null, qpc100ns,
                        timestampError ? "Invalid QPC" : discontinuity ? "Native discontinuity" : "Invalid position");
                    stream.AwaitingAnchor = true;
                    return;
                }
                if (stream.AwaitingAnchor)
                {
                    // Pending trusted packets may precede this callback. Give them first ownership.
                    RecoverPending(channel, stream, true);
                    if (stream.AwaitingAnchor)
                    {
                        if (start < stream.Expected) return;
                        source.TransitionGap(channel, stream.Expected, start, qpc100ns, "Trusted QPC after interruption");
                        stream.Expected = start;
                        stream.Initialized = true;
                        stream.AwaitingAnchor = false;
                    }
                }
                if (start < stream.Expected - 1 && start + samples.Length > stream.Expected)
                {
                    source.TransitionGap(channel, start, null, qpc100ns, "Partial QPC overlap; placement ambiguous");
                    stream.AwaitingAnchor = true;
                    return;
                }
            }
            if (timestampError || devicePosition < 0) { Fail("Недостоверная временная метка аппаратного пакета."); return; }
            if (discontinuity && stream.Initialized) { Fail("WASAPI сообщил потерю непрерывности аудио."); return; }
            if (start < originSample) { Fail("Пакет предшествует началу текущего захвата."); return; }
            if (!stream.Initialized)
            {
                // У начального пакета с discontinuity преобразователь Windows может выдать
                // неполный PCM-блок. До второго QPC его позиция не объявляется подтверждённой.
                if (stream.Startup is null && discontinuity)
                {
                    stream.Startup = new(start, samples.ToArray(), clock.Now, source.PacketProof(qpc100ns, start, samples.Length), encoding); return;
                }
                if (stream.Startup is { } first)
                {
                    if (start == first.StartSample) { duplicates++; return; }
                    long corrected = start - first.Samples.Length;
                    long adjustment = corrected - first.StartSample;
                    if (adjustment is < -1 or > 64) { Fail("Неопределённый интервал при запуске преобразователя PCM."); return; }
                    stream.Expected = Math.Max(first.StartSample, corrected);
                    source.BeginChannel(channel, stream.Expected);
                    source.PushSamples(channel, stream.Expected, first.Samples, first.Proof is null ? null : first.Proof with { Start = stream.Expected, End = stream.Expected + first.Samples.Length }, first.Encoding);
                    stream.Delivered += first.Samples.Length; stream.Expected += first.Samples.Length;
                    Array.Clear(first.Samples); stream.Startup = null;
                }
                else { stream.Expected = start; source.BeginChannel(channel, start); }
                stream.Initialized = true;
            }
            // Округление QPC до сетки 16 кГц допускает один отсчёт; пустота не заполняется нулями.
            if (Math.Abs(start - stream.Expected) <= 1) start = stream.Expected;
            if (start + samples.Length <= stream.Expected) { duplicates++; return; }
            if (stream.Pending.ContainsKey(start)) { duplicates++; return; }
            if (stream.Pending.Count >= 64 || stream.Pending.Values.Sum(p => p.Samples.Length) + samples.Length > 16000)
            { Fail("Переполнен буфер перестановки аппаратных пакетов.", overflow: true); return; }
            if (start > stream.Expected) { CaptureTiming.Gap(start - stream.Expected); reordered++; }
            stream.Pending.Add(start, new(start, samples.ToArray(), clock.Now, source.PacketProof(qpc100ns, start, samples.Length), encoding));
            Drain(channel, stream); CheckPending();
        }
    }
    private static void ClearStartup(StreamState stream)
    {
        if (stream.Startup is { } startup) Array.Clear(startup.Samples);
        stream.Startup = null;
    }

    private void RecoverPending(AudioChannel channel, StreamState stream, bool atEnd)
    {
        while (stream.Pending.Count != 0 && error is null)
        {
            if (!atEnd && !stream.Pending.Values.Any(p => clock.Now - p.Arrived > TimeSpan.FromMilliseconds(500))) return;
            var first = stream.Pending.First().Value;
            if (first.StartSample > stream.Expected || stream.AwaitingAnchor)
            {
                if (first.StartSample < stream.Expected)
                {
                    stream.Pending.Remove(first.StartSample); Array.Clear(first.Samples);
                    continue;
                }
                source.TransitionGap(channel, stream.Expected, first.StartSample, first.Proof?.PacketQpc100ns ?? 0,
                    atEnd ? "Input closed or interrupted before reorder completed" : "Positive QPC gap after reorder timeout");
                stream.Expected = first.StartSample;
                stream.AwaitingAnchor = false;
            }
            Drain(channel, stream);
        }
    }

    private void Drain(AudioChannel channel, StreamState stream)
    {
        while (stream.Pending.Count > 0 && stream.Pending.First().Key <= stream.Expected)
        {
            var entry = stream.Pending.First(); stream.Pending.Remove(entry.Key);
            try
            {
                int skip = (int)Math.Min(entry.Value.Samples.Length, stream.Expected - entry.Key);
                if (source.GapContinuationEnabled && skip > 1 && skip < entry.Value.Samples.Length)
                {
                    source.TransitionGap(channel, entry.Key, null, entry.Value.Proof?.PacketQpc100ns ?? 0, "Ambiguous queued overlap");
                    stream.AwaitingAnchor = true;
                    return;
                }
                source.PushSamples(channel, stream.Expected, entry.Value.Samples.AsSpan(skip), entry.Value.Proof, entry.Value.Encoding);
                stream.Delivered += entry.Value.Samples.Length - skip;
                stream.Expected = Math.Max(stream.Expected, entry.Key + entry.Value.Samples.Length);
                if (source.Error is { } failure) { Fail(failure); return; }
            }
            finally { Array.Clear(entry.Value.Samples); }
        }
    }
    private void CheckPending()
    {
        for (int i = 0; i < streams.Length; i++)
        {
            var stream = streams[i];
            if (stream.Pending.Values.Concat(stream.Startup is null ? [] : new[] { stream.Startup }).Any(p => clock.Now - p.Arrived > TimeSpan.FromMilliseconds(500)))
            {
                long next = stream.Pending.Count == 0 ? stream.Expected : stream.Pending.First().Key;
                if (source.GapContinuationEnabled)
                {
                    if (stream.Startup is { } startup)
                    {
                        source.TransitionGap((AudioChannel)i, stream.Expected, null, startup.Proof?.PacketQpc100ns ?? 0, "Unconfirmed startup anchor");
                        ClearStartup(stream); stream.AwaitingAnchor = true;
                    }
                    RecoverPending((AudioChannel)i, stream, false);
                    continue;
                }
                Fail($"Неизвестный интервал: канал {(AudioChannel)i}, {stream.Expected}..{next}, пробел {next - stream.Expected} отсчётов; ожидание более 500 мс. Данные не заменены тишиной.", next > stream.Expected && streams.All(s => s.Initialized && s.Startup is null)
                    ? new SttUnknownInterval(1, source.PcmGeneration, (AudioChannel)i, stream.Expected, next, streams.Min(s => s.Expected), "Положительный unknown после ожидания перестановки500мс") : null);
                return;
            }
        }
    }
    public void Pulse() { using (CaptureTiming.Lock(gate, CaptureTimingKind.TimelineLock)) CheckPending(); }
    // Только после фактического завершения обоих recorder: до этого адаптер принимает хвост.
    public void Complete()
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.TimelineLock))
        {
            accepting = false;
            if (source.GapContinuationEnabled)
                for (int i = 0; i < streams.Length; i++)
                {
                    var stream = streams[i];
                    if (stream.Startup is { } startup)
                    {
                        source.TransitionGap((AudioChannel)i, stream.Expected, null, startup.Proof?.PacketQpc100ns ?? 0, "Unconfirmed startup at Stop");
                        ClearStartup(stream);
                    }
                    RecoverPending((AudioChannel)i, stream, true);
                }
            if (streams.Any(s => s.Pending.Count > 0 || s.Startup is not null)) Fail("Источники завершены с незаполненным интервалом аудио.");
            if (error is not null) throw new IOException(error);
            SaveDiagnostic("Источники штатно завершены.");
            source.ConfirmChannelCompleted(AudioChannel.You); source.ConfirmChannelCompleted(AudioChannel.Other);
        }
    }
    private void Fail(string message, SttUnknownInterval? interval = null, bool overflow = false)
    {
        CaptureTiming.Error();
        if (error is null) SaveDiagnostic(message);
        error ??= message; accepting = false;
        if (overflow && source.GapContinuationEnabled) source.ReportReorderOverflow(error);
        else if (interval is null || !source.TryBeginDrain(interval)) source.ReportFailure(error);
        foreach (var stream in streams)
        {
            foreach (var packet in stream.Pending.Values) Array.Clear(packet.Samples);
            stream.Pending.Clear();
            if (stream.Startup is { } first) Array.Clear(first.Samples);
            stream.Startup = null;
        }
    }
    private void SaveDiagnostic(string reason)
    {
        var diagnostic = new AudioTimelineDiagnostic(1, reason, originQpc, originSample, packetsSeen,
            firstTiming.OfType<AudioPacketTiming>().ToArray(), timing.ToArray());
        // Сбой записи метаданных не должен менять политику подтверждения аудио.
        try { (diagnosticSink ?? FunctionalRunTrace.WriteTimelineSnapshot)(diagnostic); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
    public AudioTimelineStatus Snapshot()
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.TimelineLock)) return new(source.CaptureBoundaryMs, source.ReceivedThroughMs, source.FinalizedThroughMs,
            reordered, duplicates, streams.Sum(s => s.Pending.Count + (s.Startup is null ? 0 : 1)), error, streams[0].Delivered, streams[1].Delivered);
    }
}
