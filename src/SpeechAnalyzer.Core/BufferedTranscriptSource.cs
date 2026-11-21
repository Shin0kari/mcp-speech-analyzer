using System.Threading.Channels;
using System.Diagnostics;

namespace SpeechAnalyzer.Core;

public sealed record TranscriptionMetrics(int CompletedChunks, int PendingChunks, int MaximumPendingChunks,
    double MaximumQueueAgeSeconds, double ProcessingSeconds, double MaximumFinalizationLatencySeconds,
    double OldestPendingAgeSeconds = 0);

// Два потока PCM 16 кГц mono; все позиции — индексы отсчётов общего активного времени.
public sealed partial class BufferedTranscriptSource : ITranscriptSource, IAsyncDisposable
{
    public const int SampleRate = 16000;
    private readonly int chunkSamples;
    private readonly bool candidateInput;
    private int PcmLifetimeSeconds => candidateInput ? SttInputLimits.PcmLifetimeSeconds : SttInputLimits.LegacyPcmLifetimeSeconds;
    private int ProofCapacity => candidateInput ? SttInputLimits.ProofCapacity : SttInputLimits.LegacyProofCapacity;
    private bool SplitOnArrival => candidateInput || chunkSamples == SttInputLimits.LegacyMaxSamples;
    private readonly bool quantizationFloorGuard;
    private readonly object gate = new();
    private readonly IAudioTranscriber transcriber;
    private readonly SttDiagnostics? diagnostics;
    private readonly Dictionary<Guid, SttJobDiagnostics> diagnosticJobs = [];
    private readonly IClock clock;
    private DateTimeOffset startedAt;
    private readonly List<TranscriptSegment> segments = [];
    private readonly Buffer[] buffers = [new(), new()];
    private readonly List<(long Boundary, TaskCompletionSource Completion)> waiters = [];
    private readonly Channel<Job> jobs;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private CaptureStatus status;
    private string? error;
    private Task? disposal;
    private TimeSpan activeSince;
    private TimeSpan lastQueueTrace = TimeSpan.MinValue;
    private long activeOrigin;
    private long requestedBoundary;
    private readonly SortedSet<long> cuts = [];
    private readonly Dictionary<Guid, TimeSpan> audioCreated = [];
    public TimeSpan MaximumAudioAge { get; init; } = TimeSpan.FromSeconds(30);
    private int completedChunks, maximumPending;
    private double maximumQueueAge, processingSeconds, maximumFinalizationLatency;
    public TranscriptionMetrics Metrics { get { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) return new(completedChunks, audioCreated.Count, maximumPending,
        maximumQueueAge, processingSeconds, maximumFinalizationLatency,
        audioCreated.Count == 0 ? 0 : Math.Max(0, (clock.Now - audioCreated.Values.Min()).TotalSeconds)); } }
    private sealed class Buffer { public long Start, Received; public bool Completed; public bool Pcm16Only = true; public Lookback? History; public readonly List<float> Samples = []; public readonly List<PcmRangeProof> Proof = []; }
    private sealed record Job(Guid Id, float[] Samples, SttJobDiagnostics? Diagnostics, SttJobDescriptor? Descriptor = null, CancellationTokenSource? Cancellation = null, PcmInputClass InputClass = PcmInputClass.Uncertain)
    { public Task<SttRecoverySnapshot>? Admission; }
    public event Action<TranscriptSegment>? Finalized;
    public string? Error { get { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) return error; } }
    public int PendingChunks { get { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) return segments.Count(s => !s.IsFinal); } }
    public long ResumeSample { get { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) return buffers.Max(b => b.Received); } }
    public BufferedTranscriptSource(IClock clock, IAudioTranscriber transcriber, IEnumerable<TranscriptSegment>? restored = null,
        long restoredBoundaryMs = 0, int chunkSeconds = 4, SttDiagnostics? diagnostics = null, ISttRecoveryStore? recoveryStore = null, bool quantizationFloorGuard = false)
    {
        if (chunkSeconds is < 1 or > 8 and not SttInputLimits.MainSeconds) throw new ArgumentOutOfRangeException(nameof(chunkSeconds));
        candidateInput = chunkSeconds == SttInputLimits.MainSeconds;
        jobs = Channel.CreateBounded<Job>(new BoundedChannelOptions(candidateInput ? SttInputLimits.QueueCapacity : 8)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        chunkSamples = chunkSeconds * SampleRate;
        if (candidateInput) MaximumAudioAge = TimeSpan.FromSeconds(SttInputLimits.QueueWaitSeconds + SttInputLimits.InferenceBudgetSeconds + 1);
        if (restoredBoundaryMs < 0 || restoredBoundaryMs > long.MaxValue / 16)
            throw new ArgumentOutOfRangeException(nameof(restoredBoundaryMs));
        this.clock = clock; this.transcriber = transcriber; this.diagnostics = diagnostics; startedAt = clock.UtcNow;
        if (restored is not null)
        {
            segments.AddRange(restored);
            if (segments.Any(s => !s.IsFinal)) throw new ArgumentException("Восстанавливается только окончательная расшифровка.", nameof(restored));
        }
        long end = Math.Max(restoredBoundaryMs, segments.Select(s => s.EndMs).DefaultIfEmpty(0).Max()) * 16;
        foreach (var buffer in buffers) buffer.Start = buffer.Received = end;
        gapContinuation = recoveryStore is IChannelGapStore && Environment.GetEnvironmentVariable("SPEECH_ANALYZER_CHANNEL_GAPS") == "1";
        if (quantizationFloorGuard && recoveryStore is null) throw new ArgumentException("Защита требует durable recovery store.");
        this.quantizationFloorGuard = quantizationFloorGuard;
        this.recoveryStore = recoveryStore;
        if (recoveryStore is not null) persistence = new(recoveryStore, segment => Finalized?.Invoke(segment));
        try { RestoreRecovery(); durableThroughMs = Math.Min(ComputedFinalizedThroughMs, firstGapSample / 16); }
        catch { persistence?.Dispose(); throw; }
        worker = Task.Run(RecognizeAsync);
    }
    public CaptureStatus Status { get { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) return status; } }
    private long BoundarySamples => buffers.Min(b => b.Received);
    private long RequestedSamples => Math.Max(requestedBoundary, status == CaptureStatus.Capturing
        ? activeOrigin + Math.Max(0, (long)(clock.Now - activeSince).TotalMilliseconds) * 16 : BoundarySamples);
    public long CaptureBoundaryMs { get { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) return Math.Max(EvidenceBoundarySamples, RequestedSamples) / 16; } }
    public long ReceivedThroughMs { get { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) return BoundarySamples / 16; } }
    public long FinalizedThroughMs
    {
        get { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) return Math.Min(firstGapSample / 16, Math.Min(recoveryStore is null ? long.MaxValue : durableThroughMs, ComputedFinalizedThroughMs)); }
    }
    public IReadOnlyList<TranscriptSegment> Segments { get { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) return segments.OrderBy(s => s.StartMs).ThenBy(s => s.Channel).ToArray(); } }
    public void Start()
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
        {
            ThrowIfFailed();
            if (terminalStop) throw new InvalidOperationException("Завершённый STT источник нельзя запустить повторно.");
            if (status != CaptureStatus.Capturing)
            {
                startedAt = clock.UtcNow - TimeSpan.FromMilliseconds(buffers.Max(b => b.Received) / 16);
                activeSince = clock.Now; activeOrigin = buffers.Max(b => b.Received);
                foreach (var buffer in buffers) buffer.Completed = false;
            }
            status = CaptureStatus.Capturing;
        }
    }
    public void Resume() => Start();
    public void Pause() { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) { FlushStopped(); foreach (var buffer in buffers) ClearLookback(buffer); status = CaptureStatus.Paused; } }
    public void Stop() { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) GracefulStop(); }
    // Вызывается адаптером только после остановки соответствующего аппаратного источника.
    public void ConfirmChannelCompleted(AudioChannel channel) { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) buffers[(int)channel].Completed = true; }
    // До первого пакета источник ещё не был запущен: это известный интервал без захвата, не цифровая тишина.
    public void BeginChannel(AudioChannel channel, long startSample)
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
        {
            var buffer = buffers[(int)channel];
            if (buffer.Samples.Count != 0 || buffer.Start != buffer.Received || startSample < buffer.Received)
                throw new InvalidOperationException("Начало канала разрешено только до получения новых пакетов.");
            ClearLookback(buffer);
            buffer.Start = buffer.Received = startSample;
        }
    }
    private void FlushStopped()
    {
        long end = (Math.Max(RequestedSamples, buffers.Max(b => b.Received)) + 15) / 16 * 16;
        if (buffers.Any(b => b.Received != end) && !buffers.All(b => b.Completed))
            throw new IOException("Нельзя выровнять каналы: источники ещё не подтвердили завершение.");
        foreach (var buffer in buffers) buffer.Received = end;
        requestedBoundary = end;
        Flush(end);
        foreach (var buffer in buffers) if (buffer.Samples.Count == 0) buffer.Start = end;
        CompleteWaiters();
    }
    public void Advance()
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
        {
            if (status == CaptureStatus.Capturing && (lastQueueTrace == TimeSpan.MinValue || clock.Now - lastQueueTrace >= TimeSpan.FromSeconds(1)))
            {
                lastQueueTrace = clock.Now;
                FunctionalRunTrace.Write("stt_queue_sample", boundaryMs: CaptureBoundaryMs, transcriptionMetrics: Metrics);
            }
            if (error is null && audioCreated.Values.Any(at => clock.Now - at > MaximumAudioAge))
                Fail("Превышен срок жизни временного PCM. Распознавание отменено; остаток не подтверждён.", SttCancelCause.PcmExpired);
            CheckOriginalExpiry();
            foreach (var buffer in buffers)
                if (buffer.History is { Count: > 0 } history && clock.Now - history.Updated >= LookbackAge)
                    ClearLookback(buffer);
            FlushRegular(); CheckpointCandidateCoverage(); CompleteWaiters();
        }
    }
    public void PushSamples(AudioChannel channel, long startSample, ReadOnlySpan<float> samples, PcmRangeProof? proof = null, PcmEncoding encoding = PcmEncoding.UnknownFloat)
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (status != CaptureStatus.Capturing || error is not null) return;
            if (channel is not (AudioChannel.You or AudioChannel.Other)) throw new ArgumentOutOfRangeException(nameof(channel));
            if (startSample < 0 || startSample > long.MaxValue - samples.Length) throw new ArgumentOutOfRangeException(nameof(startSample));
            var buffer = buffers[(int)channel];
            long gap = startSample - buffer.Received;
            if (gap > 0) { Fail($"Неизвестный интервал аудио: канал {channel}, {buffer.Received}..{startSample} отсчётов.", SttCancelCause.TimelineFailure); return; }
            int skip = (int)Math.Clamp(buffer.Received - startSample, 0, samples.Length);
            long additional = samples.Length - skip;
            if (additional > (candidateInput ? SttInputLimits.MaxSamples : SttInputLimits.LegacyMaxSamples)
                || !SplitOnArrival && buffer.Samples.Count + additional > SttInputLimits.LegacyMaxSamples)
            { Fail("Очередь распознавания переполнена.", SttCancelCause.QueueOverflow); return; }
            int offset = skip;
            while (offset < samples.Length)
            {
                long at = buffer.Start + buffer.Samples.Count;
                int n = samples.Length - offset;
                if (SplitOnArrival)
                {
                    n = Math.Min(n, chunkSamples - buffer.Samples.Count);
                    foreach (long cut in cuts) if (cut > at) { n = (int)Math.Min(n, cut - at); break; }
                }
                if (recoveryStore is not null) AddProof(buffer, proof, at, at + n);
                buffer.Pcm16Only &= encoding == PcmEncoding.SignedPcm16Div32768;
                foreach (float sample in samples.Slice(offset, n)) buffer.Samples.Add(sample);
                offset += n;
                if (SplitOnArrival && (buffer.Samples.Count == chunkSamples || cuts.Contains(buffer.Start + buffer.Samples.Count)))
                {
                    FlushBuffer((int)channel, buffer.Start + buffer.Samples.Count);
                    if (error is not null) return;
                }
            }
            buffer.Received = Math.Max(buffer.Received, startSample + samples.Length);
            foreach (long cut in cuts) FlushConfirmed(cut);
            cuts.RemoveWhere(cut => cut <= EvidenceBoundarySamples);
            FlushRegular();
            if (waiters.Count != 0) CheckpointCandidateCoverage();
            CompleteWaiters();
        }
    }
    private void FlushRegular()
    {
        long cut = BoundarySamples;
        for (int i = 0; i < buffers.Length; i++)
            while (cut - buffers[i].Start >= chunkSamples && error is null) FlushBuffer(i, buffers[i].Start + chunkSamples);
    }
    private void Flush(long cut) { for (int i = 0; i < buffers.Length; i++) FlushBuffer(i, cut); }
    private void FlushConfirmed(long cut)
    {
        for (int i = 0; i < buffers.Length; i++)
            if (buffers[i].Received >= cut) FlushBuffer(i, cut);
    }
    private void FlushBuffer(int channel, long cut)
    {
        var buffer = buffers[channel];
        int count = (int)Math.Clamp(cut - buffer.Start, 0, buffer.Samples.Count);
        if (count == 0) return;
        if (recoveryStore is not null && drainPhase != SttDrainPhase.Open) return;
        using var submissionTiming = CaptureTiming.Phase(CaptureTimingKind.SttSubmission, count);
        if (!ValidateBufferProof(buffer, count)) { Fail("Недостоверный или истёкший PCM до submission.", SttCancelCause.PcmExpired); return; }
        long start = buffer.Start;
        int lookback = candidateInput && count <= ShortTailSamples && cuts.Contains(cut) ? AvailableLookback(buffer) : 0;
        float[] audio = new float[count + lookback];
        if (lookback > 0 && buffer.History is { } history)
            Array.Copy(history.Samples, history.Count - lookback, audio, 0, lookback);
        buffer.Samples.CopyTo(0, audio, lookback, count);
        // Решение только после проверки полноты, provenance и исходного expiry.
        var inputClass = quantizationFloorGuard ? PcmSilence.Classify(audio, buffer.Pcm16Only
            ? PcmEncoding.SignedPcm16Div32768 : PcmEncoding.UnknownFloat) : PcmInputClass.Uncertain;
        // Прежний fast path точных нулей не получает новых jobs и синхронных записей.
        if (!PcmSilence.IsDigitalSilence(audio.AsSpan(lookback)))
        {
            var id = Guid.NewGuid();
            var segment = new TranscriptSegment(id, (AudioChannel)channel, start / 16, (start + count + 15) / 16,
                startedAt + TimeSpan.FromMilliseconds(start / 16), "Ожидает локального распознавания…", false);
            if (gapContinuation || candidateInput) segment = segment with { StartSample = start, EndSample = start + count };
            if (candidateInput) segment = segment with { SttInput = new(start - lookback, start, start + count, []) };
            var diagnostic = diagnostics?.Job(id, (AudioChannel)channel);
            var job = CreateJob(id, (AudioChannel)channel, start, audio, diagnostic, buffer, inputClass, lookback);
            if (job is null) { Array.Clear(audio); return; }
            if (!jobs.Writer.TryWrite(job))
            {
                // A descriptor that never entered the queue has no durable admission or worker owner.
                recoveryJobs.Remove(id); job.Cancellation?.Dispose();
                diagnostic?.Finish(SttOutcome.Rejected); Array.Clear(audio);
                Fail("Очередь STT заполнена; остаток не подтверждён.", SttCancelCause.QueueOverflow); return;
            }
            if (diagnostic is not null) { diagnosticJobs.Add(id, diagnostic); diagnostic.Stage(SttStage.Queued); }
            segments.Add(segment);
            audioCreated[id] = clock.Now;
            if (recoveryStore is not null) liveJobs.Add(id, job);
            SessionEventLog.Current?.Write("stt_submission",new{id,channel,startSample=start,endSample=start+count,count});
            maximumPending = Math.Max(maximumPending, audioCreated.Count);
        }
        else Array.Clear(audio);
        RememberLookback(buffer, count);
        TrimProof(buffer, start + count);
        float[] remainder = buffer.Samples.Skip(count).ToArray();
        for (int i = 0; i < buffer.Samples.Count; i++) buffer.Samples[i] = 0;
        buffer.Samples.Clear(); buffer.Samples.AddRange(remainder); Array.Clear(remainder); buffer.Start += count;
        if (buffer.Samples.Count == 0) buffer.Pcm16Only = true;
        if (recoveryStore is not null)
        {
            // Consume accepted input exactly once before external persistence can reenter Stop.
            buffer.Received = Math.Max(buffer.Received, start + count);
            var admitted = liveJobs.Values.FirstOrDefault(j => j.Descriptor!.Channel == (AudioChannel)channel && j.Descriptor.Start == start);
            if (admitted is not null) admitted.Admission = PersistRecovery();
        }
    }
    public long SnapshotBoundary()
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
        {
            ThrowIfFailed(); long boundary = terminalStop ? EvidenceBoundarySamples / 16 : CaptureBoundaryMs;
            requestedBoundary = boundary * 16; cuts.Add(requestedBoundary);
            FlushConfirmed(requestedBoundary); return boundary;
        }
    }
    public async Task WaitForFinalizationAsync(long boundaryMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource completion;
        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
        {
            ThrowForBoundary(boundaryMs);
            if (boundaryMs < 0 || boundaryMs > CaptureBoundaryMs) throw new ArgumentOutOfRangeException(nameof(boundaryMs));
            if (drainPhase == SttDrainPhase.Open) { cuts.Add(boundaryMs * 16); FlushConfirmed(boundaryMs * 16); }
            ThrowForBoundary(boundaryMs);
            if (IsReady(boundaryMs)) return;
            if (recoveryStore is not null) _ = PersistRecovery();
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((boundaryMs, completion));
        }
        try { await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) waiters.RemoveAll(waiter => ReferenceEquals(waiter.Completion, completion)); }
    }
    private bool IsReady(long cut) => GapWritesAcknowledged && EvidenceThroughMs >= cut && EvidenceBoundarySamples / 16 >= cut && !segments.Any(s => !s.IsFinal && s.StartMs < cut)
        && !buffers.Any(b => b.Samples.Count > 0 && b.Start / 16 < cut);
    private void CompleteWaiters()
    {
        for (int i = waiters.Count - 1; i >= 0; i--)
            if (BoundaryError(waiters[i].Boundary) is not null || IsReady(waiters[i].Boundary))
            {
                if (BoundaryError(waiters[i].Boundary) is { } failure) waiters[i].Completion.TrySetException(new SttCoverageException(failure)); else waiters[i].Completion.TrySetResult();
                waiters.RemoveAt(i);
            }
    }
    private async Task RecognizeAsync()
    {
        try
        {
            await foreach (var job in jobs.Reader.ReadAllAsync(lifetime.Token))
            {
                try
                {
                    Task<SttRecoverySnapshot>? admission;
                    using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
                    {
                        activeJob = job; admission = job.Admission;
                    }
                    if (admission is not null)
                        await admission.WaitAsync(job.Cancellation!.Token).ConfigureAwait(false);
                    using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
                    {
                        if (!CanRun(job))
                        {
                            bool expired = job.Descriptor is { } d && clock.Now >= d.Expires;
                            RejectJob(job, expired ? SttJobFate.Expired : SttJobFate.Ineligible);
                            if (expired) Fail("Истёк исходный срок queued PCM.", SttCancelCause.PcmExpired);
                            continue;
                        }
                        if (job.Descriptor is { } verified && verified.InputSha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(job.Samples.AsSpan()))))
                            throw new SttCoverageException("Изменён submitted PCM.");
                        activeJob = job;
                        double queueAge = (clock.Now - audioCreated[job.Id]).TotalSeconds;
                        maximumQueueAge = Math.Max(maximumQueueAge, queueAge);
                        if (candidateInput && queueAge >= SttInputLimits.QueueWaitSeconds)
                            throw new TimeoutException("STT queue wait limit exceeded.");
                        job.Diagnostics?.Stage(SttStage.Dequeued);
                    }
                    long processingStart = Stopwatch.GetTimestamp();
                    var jobToken = job.Cancellation?.Token ?? lifetime.Token;
                    string text = job.InputClass != PcmInputClass.Uncertain ? "" : job.Descriptor is { } descriptor && transcriber is IExpiringAudioTranscriber expiring
                        ? await expiring.TranscribeExpiringAsync(job.Samples, descriptor, clock, jobToken, job.Diagnostics)
                        : transcriber is ISttDiagnosticTranscriber observed
                        ? await observed.TranscribeAsync(job.Samples, jobToken, job.Diagnostics)
                        : await transcriber.TranscribeAsync(job.Samples, jobToken);
                    double elapsed = Stopwatch.GetElapsedTime(processingStart).TotalSeconds;
                    Array.Clear(job.Samples);

                    TranscriptSegment? completed = null;
                    using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
                    {
                        if (!CanRun(job))
                        {
                            if (job.Descriptor is { } d && clock.Now >= d.Expires) Fail("Истёк исходный срок active PCM.", SttCancelCause.PcmExpired);
                            throw new SttCoverageException("Поздний STT результат не прошёл identity/expiry/fence.");
                        }
                        int index = segments.FindIndex(s => s.Id == job.Id);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            completed = segments[index] with { Text = text, IsFinal = true };
                            if (completed.SttInput is { } input && job.InputClass == PcmInputClass.Uncertain
                                && transcriber is ISttTimingSource timing)
                                completed = completed with { SttInput = input with { RawTimings = timing.LastTimings.ToArray() } };
                        }
                    }
                    if (recoveryStore is null)
                    {
                        if (completed is not null) Finalized?.Invoke(completed);
                    }
                    else
                    {
                        Task<SttRecoverySnapshot> saved;
                        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
                        {
                            if (!CanRun(job)) throw new SttCoverageException("STT result is no longer eligible.");
                            pendingResults[job.Id] = completed is null ? SttJobFate.Empty : SttJobFate.Finalized;
                            var snapshot = RecoverySnapshot(job.Id);
                            saved = persistence!.Save(snapshot, completed, job.InputClass == PcmInputClass.Uncertain ? null
                                : new(PcmSilence.ClassifierVersion, job.InputClass, job.Descriptor!));
                        }
                        var acknowledged = await saved.WaitAsync(job.Cancellation!.Token).ConfigureAwait(false);
                        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
                        {
                            if (!CanPublish(job)) throw new SttCoverageException("Late persistence acknowledgment is ineligible.");
                            Acknowledge(acknowledged);
                            recoveryJobs[job.Id] = new(job.Descriptor!, completed is null ? SttJobFate.Empty : SttJobFate.Finalized);
                        }
                    }
                    using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
                    {
                        if (!CanPublish(job)) throw new SttCoverageException("STT result is no longer eligible.");
                        if (completed is not null) segments[segments.FindIndex(s => s.Id == job.Id)] = completed;
                        else segments.RemoveAll(s => s.Id == job.Id);
                        pendingResults.Remove(job.Id);
                        if (completed is not null) job.Diagnostics?.Stage(SttStage.FinalizationAcknowledged);
                        completedChunks++; processingSeconds += elapsed;
                        if (completed is not null)
                            maximumFinalizationLatency = Math.Max(maximumFinalizationLatency,
                                Math.Max(0, (clock.UtcNow - (completed.CapturedAt + TimeSpan.FromMilliseconds(completed.EndMs - completed.StartMs))).TotalSeconds));
                        CompleteWaiters();
                        job.Diagnostics?.Finish(completed is null ? SttOutcome.EmptyResult : SttOutcome.Finalized);
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { job.Diagnostics?.Finish(SttOutcome.CancelledActive); break; }
                catch (OperationCanceledException) when (job.Cancellation?.IsCancellationRequested == true)
                { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) { RejectJob(job, clock.Now >= job.Descriptor!.Expires ? SttJobFate.Expired : SttJobFate.Ineligible); job.Diagnostics?.Finish(SttOutcome.CancelledActive); if (clock.Now >= job.Descriptor!.Expires) Fail("Истёк исходный срок active PCM.", SttCancelCause.PcmExpired); } }
                catch (SttCoverageException) when (recoveryStore is not null && recoveryJobs.TryGetValue(job.Id, out var rejected) && rejected.Fate != SttJobFate.Pending)
                { job.Diagnostics?.Finish(SttOutcome.CancelledActive); }
                catch (Exception failure)
                {
                    FunctionalRunTrace.Write(failure is TimeoutException ? "stt_timeout" : "stt_error");
                    job.Diagnostics?.Finish(SttOutcome.Failed);
                    using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) Fail("Локальное распознавание: " + failure.Message,
                        failure is TimeoutException ? SttCancelCause.PeerDeadline : SttCancelCause.RecognitionFailure);
                }
                finally { Array.Clear(job.Samples); using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) { audioCreated.Remove(job.Id); diagnosticJobs.Remove(job.Id); EndRecoveryJob(job); UpdateStopCompletion(); } }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) ClearAudio(); }
    }
    // Адаптер сообщает недостоверный интервал: ожидатели завершаются ошибкой, PCM очищается.
    public void ReportFailure(string message) { using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) Fail(message, SttCancelCause.TimelineFailure); }
    private void Fail(string message, SttCancelCause cause = SttCancelCause.SourceFailure)
    {
        RecordStopFailure(cause switch {
            SttCancelCause.TimelineFailure => TranscriptStopReason.TimelineFailure,
            SttCancelCause.PcmExpired => TranscriptStopReason.PcmExpired,
            SttCancelCause.QueueOverflow => TranscriptStopReason.QueueFull,
            SttCancelCause.PeerDeadline => TranscriptStopReason.Deadline,
            SttCancelCause.RecognitionFailure => TranscriptStopReason.RecognitionFailure,
            _ => TranscriptStopReason.PersistenceFailure }, message);
        status = CaptureStatus.Stopped;
        if (recoveryStore is not null)
        {
            for (int i = 0; i < buffers.Length; i++)
                if (buffers[i].Samples.Count > 0) incompleteFrom[i] = Math.Min(incompleteFrom[i], buffers[i].Start);
            hardFailure = true; drainPhase = SttDrainPhase.Failed;
            pendingResults.Clear();
            foreach (var job in liveJobs.Values)
            {
                var descriptor = job.Descriptor!;
                incompleteFrom[(int)descriptor.Channel] = Math.Min(incompleteFrom[(int)descriptor.Channel], descriptor.Start);
                // После hard failure временный PCM не возобновляется. Уже принятые
                // terminal-состояния не переписываем, в том числе при неизвестном ACK.
                if (recoveryJobs[job.Id].Fate == SttJobFate.Pending)
                    recoveryJobs[job.Id] = new(descriptor, SttJobFate.Interrupted);
            }
            TryPersistRecovery();
        }
        foreach (var job in diagnosticJobs.Values) job.Cancel(cause);
        lifetime.Cancel(); ClearAudio(); CompleteWaiters();
        UpdateStopCompletion();
    }
    private void ClearAudio()
    {
        while (jobs.Reader.TryRead(out var job)) { if (recoveryStore is not null) RejectJob(job, SttJobFate.Interrupted); job.Diagnostics?.Finish(SttOutcome.RemovedQueued); Array.Clear(job.Samples); audioCreated.Remove(job.Id); diagnosticJobs.Remove(job.Id); EndRecoveryJob(job); }
        foreach (var buffer in buffers) { for (int i = 0; i < buffer.Samples.Count; i++) buffer.Samples[i] = 0; buffer.Samples.Clear(); buffer.Proof.Clear(); ClearLookback(buffer); }
    }
    private void ThrowIfFailed()
    {
        ObjectDisposedException.ThrowIf(disposal is not null, this);
        if (error is not null) throw new IOException(error);
    }
    public ValueTask DisposeAsync()
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
        {
            status = CaptureStatus.Stopped;
            return new(disposal ??= Task.Run(DisposeCoreAsync));
        }
    }
    private async Task DisposeCoreAsync()
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock)) { if (stopState is not (TranscriptStopState.Complete or TranscriptStopState.StoppedWithGaps)) Abort(); foreach (var job in diagnosticJobs.Values) job.Cancel(SttCancelCause.Dispose); }
        lifetime.Cancel(); jobs.Writer.TryComplete(); await worker;
        using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
        {
            ClearAudio();
            foreach (var waiter in waiters) waiter.Completion.TrySetCanceled(); waiters.Clear();
        }
        persistence?.Dispose();
        if (persistence is { CleanupComplete: false })
            using (CaptureTiming.Lock(gate, CaptureTimingKind.SourceLock))
                if (stopDetails.Count < 16) stopDetails.Add("Persistence cleanup is incomplete; the outstanding writer retains exclusive store ownership.");
        lifetime.Dispose();
    }
}
