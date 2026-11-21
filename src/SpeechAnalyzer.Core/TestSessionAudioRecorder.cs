using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SpeechAnalyzer.Core;

public sealed record AudioRecordingStatus(bool Enabled, bool Accepting, string Directory, string? Error,
    int QueuedSamples, long YouSamples, long OtherSamples, int CompletedParts, long AvailableThroughSample)
{
    public RecorderFailure? Failure { get; init; }
    public long?[] ConfirmedEndSamples { get; init; } = [null, null];
    public int PendingDataPackets { get; init; }
    public int PendingControls { get; init; }
}
public sealed record RecorderFailure(string Kind, AudioChannel? Channel, long? Start, long? End,
    long[] AcceptedEndSamples, long?[] ConfirmedEndSamples);

// Тестовый retained recorder. Не зависит от transcriber, модели или MCP.
public sealed class TestSessionAudioRecorder : IAsyncDisposable
{
    public const int Rate = 16000, PartSamples = 160000, QueueSamples = 64000;
    // Two channels at 100 packets/s: one second adds 200 packets/32000 samples.
    // Keep the existing PCM bound; packet metadata is separately bounded even
    // for one-sample packets. Two reserved control slots include active controls.
    public const int QueueDataPackets = QueueSamples / 160, QueueControls = 2;
    private readonly object gate = new();
    private readonly Channel<Packet> queue = Channel.CreateBounded<Packet>(new BoundedChannelOptions(QueueDataPackets + QueueControls) { SingleReader = true });
    private readonly string directory, run = Guid.NewGuid().ToString("N");
    private readonly Task writer;
    private readonly Action? beforeWrite;
    private readonly SessionEventLog events;
    private readonly IRecorderStorage storage;
    private readonly long[] written = new long[2];
    private readonly Part?[] parts = new Part?[2];
    private readonly long?[] expected = new long?[2];
    private readonly long[] submittedEnd = new long[2];
    private readonly long?[] appendedEnd = [null, null], confirmedEnd = [null, null];
    private RecorderFailure? failure;
    private int dataPackets, controls;
    private int queuedSamples, completed, nextPart;
    private bool accepting, disposed;
    private long originQpc, originSample, available;
    private string? error;
    private sealed record Packet(AudioChannel Channel, long Start, long Qpc, float[] Samples, bool Discontinuity, bool TimestampError, TaskCompletionSource? Barrier = null, bool CloseParts = true,
        long? DevicePosition = null, bool? Silent = null, long? ArrivalQpc100ns = null);
    private sealed class Part(Stream file, string path, int channel)
    {
        public readonly Stream File = file;
        public readonly string Path = path;
        public readonly int Channel = channel;
        public int Samples, Flushed;
    }
    public TestSessionAudioRecorder(string sessionDirectory, SessionEventLog events, long initialSample = 0, Action? beforeWrite = null, IRecorderStorage? storage = null)
    {
        this.events = events; this.beforeWrite = beforeWrite; this.storage = storage ?? new FileRecorderStorage();
        directory = Path.Combine(sessionDirectory, "audio"); if (storage is null) Directory.CreateDirectory(directory);
        available = initialSample;
        if (storage is null) RecoverParts(directory, events);
        foreach (string file in storage is null ? Directory.EnumerateFiles(directory, "packets-*.jsonl") : [])
            foreach (string line in File.ReadLines(file))
                try { using var j = JsonDocument.Parse(line); available = Math.Max(available, j.RootElement.GetProperty("endSample").GetInt64()); }
                catch (Exception e) when (e is JsonException or KeyNotFoundException) { /* Последний неполный metadata-tail не считается coverage. */ }
        writer = Task.Run(WriteAsync);
    }
    public AudioRecordingStatus Status { get { using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock)) return new(true, accepting, directory, error,
        queuedSamples, written[0], written[1], completed, available) { Failure = failure is null ? null : failure with {
            AcceptedEndSamples = (long[])failure.AcceptedEndSamples.Clone(), ConfirmedEndSamples = (long?[])failure.ConfirmedEndSamples.Clone() },
            ConfirmedEndSamples = (long?[])confirmedEnd.Clone(), PendingDataPackets = dataPackets, PendingControls = controls }; } }
    public void Begin(long qpc100ns)
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock))
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (error is not null) throw new IOException(error);
            if (accepting) throw new InvalidOperationException("Recorder уже запущен.");
            originSample = available; originQpc = qpc100ns; Array.Clear(expected); accepting = true;
            Array.Fill(submittedEnd,originSample);
            events.Write("recording_start_or_resume", new { originQpc, originSample, rate = Rate, partSamples = PartSamples,
                channels = new[] { "Вы", "Собеседник" }, captureScope = "process_tree" });
        }
    }
    public void End(long qpc100ns)
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock))
        {
            if (!accepting) return;
            accepting = false; available = Math.Max(available, originSample + Math.Max(0, (qpc100ns-originQpc)*Rate/10_000_000));
            events.Write("recording_pause_or_stop", new { qpc100ns, available });
        }
    }
    public bool Submit(AudioChannel channel, long qpc100ns, ReadOnlySpan<float> samples, bool discontinuity = false, bool timestampError = false,
        long? devicePosition = null, bool? silent = null, long? arrivalQpc100ns = null)
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock))
        {
            if (!accepting || error is not null) return false;
            // WASAPI может прислать callback без PCM и без пригодной QPC.
            // Он не подтверждает интервал и не должен останавливать сохранение следующих пакетов.
            if (samples.IsEmpty && channel is AudioChannel.You or AudioChannel.Other) return true;
            long start = originSample + (long)Math.Round((qpc100ns-originQpc)*(Rate/10_000_000d));
            if (channel is not (AudioChannel.You or AudioChannel.Other) || samples.Length > Rate || samples.Length == 0)
            { Fail("Неверный packet recorder.", new { start, count = samples.Length }); return false; }
            // Ошибочная QPC не повод терять исходный PCM: сохраняем packet-order с явной неопределённостью времени.
            if(timestampError||start<originSample){start=submittedEnd[(int)channel];timestampError=true;}
            if (samples.Length + queuedSamples > QueueSamples)
            { Fail("Дисковая очередь переполнена: запись аудио неполна.", new { channel, start, end = start+samples.Length, reason = "queue-overflow" }, "sample-capacity", channel, start, start+samples.Length); return false; }
            if (dataPackets >= QueueDataPackets)
            { Fail("Очередь пакетов recorder переполнена: запись аудио неполна.", new { channel, start, count = samples.Length }, "packet-capacity", channel, start, start+samples.Length); return false; }
            var copy = samples.ToArray(); var packet = new Packet(channel, start, qpc100ns, copy, discontinuity, timestampError,
                DevicePosition:devicePosition, Silent:silent, ArrivalQpc100ns:arrivalQpc100ns);
            queuedSamples += samples.Length;
            if (!queue.Writer.TryWrite(packet)) { queuedSamples -= samples.Length; Array.Clear(copy); Fail("Очередь recorder недоступна.", new { channel, start, count = samples.Length }, "queue-unavailable", channel, start, start+samples.Length); return false; }
            dataPackets++;
            submittedEnd[(int)channel]=start+samples.Length;available = Math.Max(available, start + samples.Length); return true;
        }
    }
    private void Fail(string message, object? details = null, string kind = "sink-error", AudioChannel? channel = null, long? start = null, long? end = null)
    {
        bool first;
        using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock)) {
            first = error is null; error ??= message; accepting = false;
            failure ??= new(kind, channel, start, end, (long[])submittedEnd.Clone(), (long?[])confirmedEnd.Clone());
        }
        if (first) events.Write("recording_error_or_gap", new { message, details, failure });
    }
    private void ConfirmMetadata(Stream metadata)
    {
        storage.Flush(metadata);
        using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock)) Array.Copy(appendedEnd, confirmedEnd, 2);
    }
    private void ThrowIfFailed() { using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock)) if(error is not null) throw new IOException(error); }
    private Part Open(int c)
    {
        string file = Path.Combine(directory, $"{run}-{(c==0?"you":"other")}-{++nextPart:D6}.partial.wav");
        var stream = storage.Open(file);
        try { Header(stream, 0); storage.Flush(stream); events.Write("audio_part_open", new { file, channel = c }); return new(stream, file, c); }
        catch { stream.Dispose(); throw; }
    }
    private static void Header(Stream stream, long samples)
    {
        long position = stream.Position; stream.Position = 0;
        using var w = new BinaryWriter(stream, Encoding.ASCII, true);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(checked((uint)(36+samples*2))); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((ushort)1); w.Write((ushort)1); w.Write(Rate); w.Write(Rate*2); w.Write((ushort)2); w.Write((ushort)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(checked((uint)(samples*2))); stream.Position = Math.Max(44, position);
    }
    private void Close(Part p)
    {
        Header(p.File,p.Samples); storage.Flush(p.File); p.File.Dispose();
        string final = p.Path.Replace(".partial.wav", ".wav", StringComparison.Ordinal); storage.CommitPart(p.Path, final);
        using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock)) completed++;
        events.Write("audio_part_closed", new { file = final, channel = p.Channel, samples = p.Samples });
    }
    private async Task WriteAsync()
    {
        bool writingFailed=false;
        Packet? current = null;
        try
        {
            using var metadata = storage.Open(Path.Combine(directory, "packets-"+run+".jsonl"));
            await foreach (var packet in queue.Reader.ReadAllAsync())
            {
                current = packet;
                try
                {
                    if(packet.Barrier is {} barrier)
                    {
                        for(int channelIndex=0;channelIndex<2;channelIndex++)if(parts[channelIndex] is {} active)
                        { if(packet.CloseParts){Close(active);parts[channelIndex]=null;}else{Header(active.File,active.Samples);storage.Flush(active.File);} appendedEnd[channelIndex]=expected[channelIndex]; }
                        ConfirmMetadata(metadata);barrier.TrySetResult();continue;
                    }
                    beforeWrite?.Invoke();
                    int c = (int)packet.Channel;
                    if (packet.TimestampError || packet.Discontinuity || expected[c] is long previous && Math.Abs(previous-packet.Start)>1)
                        events.Write("audio_timeline_gap_or_discontinuity", new { channel = c, expectedSample = expected[c], packet.Start, packet.Qpc, packet.Discontinuity, packet.TimestampError });
                    expected[c] = packet.Start + packet.Samples.Length;
                    int offset = 0;
                    while (offset < packet.Samples.Length)
                    {
                        var p = parts[c] ??= Open(c); int n = Math.Min(PartSamples-p.Samples,packet.Samples.Length-offset);
                        byte[] bytes = new byte[n*2];
                        try
                        {
                            for (int i=0;i<n;i++) { float sample = packet.Samples[offset+i]; if (!float.IsFinite(sample)) throw new IOException("Нечисловой PCM."); short value = (short)Math.Clamp((int)Math.Round(sample*32768),short.MinValue,short.MaxValue); System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i*2),value); }
                            p.File.Write(bytes);
                        }
                        finally { Array.Clear(bytes); }
                        var row = new { version = 2, file = Path.GetFileName(p.Path).Replace(".partial.wav", ".wav", StringComparison.Ordinal), channel = c,
                            label = c==0?"Вы":"Собеседник", fileSample = p.Samples, startSample = packet.Start+offset, endSample = packet.Start+offset+n,
                            packetQpc100ns = packet.Qpc, packetOffset = offset, packet.TimestampError, packet.Discontinuity,
                            packetSamples = packet.Samples.Length, packet.DevicePosition, packet.Silent, packet.ArrivalQpc100ns };
                        metadata.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row)+"\n"));
                        p.Samples += n; offset += n; using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock)) written[c] += n;
                        // Appended bytes are not durable confirmation. Per-channel
                        // confirmation advances only after its data and index flush.
                        if (p.Samples-p.Flushed >= Rate) { Header(p.File,p.Samples); storage.Flush(p.File); appendedEnd[c] = packet.Start + offset; ConfirmMetadata(metadata); p.Flushed=p.Samples; }
                        if (p.Samples==PartSamples) { Close(p); parts[c]=null; appendedEnd[c] = packet.Start + offset; ConfirmMetadata(metadata); }
                    }
                }
                catch (Exception e)
                {
                    // Publish the first failure before waking a checkpoint caller.
                    // Stop uses this status to finish healthy STT independently.
                    Fail("Ошибка записи аудио: "+e.GetType().Name, kind:"sink-error",
                        channel:packet.Barrier is null ? packet.Channel : null,
                        start:packet.Barrier is null ? packet.Start : null,
                        end:packet.Barrier is null ? packet.Start+packet.Samples.Length : null);
                    packet.Barrier?.TrySetException(new IOException(error, e)); throw;
                }
                finally { using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock)) { queuedSamples -= packet.Samples.Length; if(packet.Barrier is null)dataPackets--;else controls--; } Array.Clear(packet.Samples); }
            }
            // Close data before the final index acknowledgment.
            for(int c=0;c<2;c++)if(parts[c] is {} p){Close(p);parts[c]=null;appendedEnd[c]=expected[c];}
            ConfirmMetadata(metadata);
        }
        catch (Exception e) { writingFailed=true;Fail("Ошибка записи аудио: "+e.GetType().Name, kind:"sink-error",
            channel:current?.Barrier is null ? current?.Channel : null, start:current?.Barrier is null ? current?.Start : null,
            end:current?.Barrier is null && current is not null ? current.Start+current.Samples.Length : null); }
        finally
        {
            // Prevent racing admission into an exited writer before draining.
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var packet)) { packet.Barrier?.TrySetException(new IOException(error ?? "Recorder stopped")); using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock)) { queuedSamples-=packet.Samples.Length;if(packet.Barrier is null)dataPackets--;else controls--; } Array.Clear(packet.Samples); }
            for(int c=0;c<2;c++) if(parts[c] is {} p)
            {
                try { if(writingFailed){p.File.Dispose();events.Write("audio_part_incomplete",new{file=p.Path,knownSamples=p.Samples});}else Close(p); }
                catch(Exception e) { p.File.Dispose(); Fail("Не завершена часть аудио: "+e.GetType().Name); }
                parts[c]=null;
            }
        }
    }
    public static void RecoverParts(string directory, SessionEventLog events)
    {
        foreach(string file in Directory.EnumerateFiles(directory,"*.partial.wav"))
        {
            long samples;
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                if(stream.Length<44)throw new IOException("Неполный WAV header: "+Path.GetFileName(file));
                byte[] header=new byte[44];stream.ReadExactly(header);
                if(Encoding.ASCII.GetString(header,0,4)!="RIFF"||Encoding.ASCII.GetString(header,8,8)!="WAVEfmt "||
                    BitConverter.ToUInt16(header,20)!=1||BitConverter.ToUInt16(header,22)!=1||BitConverter.ToInt32(header,24)!=Rate||BitConverter.ToUInt16(header,34)!=16||
                    Encoding.ASCII.GetString(header,36,4)!="data"||stream.Length>44+PartSamples*2)
                    throw new IOException("Неизвестный формат активной WAV-части.");
                samples=(stream.Length-44)/2;stream.SetLength(44+samples*2);Header(stream,samples);stream.Flush(true);
            }
            string recovered=file.Replace(".partial.wav",".recovered.wav",StringComparison.Ordinal);File.Move(file,recovered);
            events.Write("audio_part_recovered",new{previousFile=file,metadataFile=Path.GetFileName(file).Replace(".partial.wav",".wav",StringComparison.Ordinal),file=recovered,samples,timelineMayBeIncomplete=true});
        }
    }
    public async ValueTask DisposeAsync()
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock)){if(!disposed){disposed=true;accepting=false;queue.Writer.TryComplete();}}
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        ThrowIfFailed();
    }
    public async Task FlushAsync(bool closeParts = true)
    {
        Task? acknowledgment = null;
        using (CaptureTiming.Lock(gate, CaptureTimingKind.RecorderLock))
        {
            if (!writer.IsCompleted && !disposed)
            {
                if (controls >= QueueControls) throw new IOException("Recorder checkpoint capacity exhausted; request not accepted.");
                var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // One bounded shared FIFO, with independent admission quotas. A
                // control cannot displace a supported data packet or wait off-queue.
                if (!queue.Writer.TryWrite(new(AudioChannel.You,0,0,[],false,false,barrier,closeParts))) throw new IOException("Recorder checkpoint unavailable.");
                controls++; acknowledgment = barrier.Task;
            }
        }
        if (acknowledgment is not null) await acknowledgment.WaitAsync(TimeSpan.FromSeconds(10));
        else await writer.WaitAsync(TimeSpan.FromSeconds(10));
        ThrowIfFailed();
    }
}
