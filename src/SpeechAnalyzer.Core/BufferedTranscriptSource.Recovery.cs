using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SpeechAnalyzer.Core;

public sealed class SttCoverageException(string message) : IOException(message);

public sealed partial class BufferedTranscriptSource
{
    private readonly ISttRecoveryStore? recoveryStore;
    private readonly OrderedSttPersistence? persistence;
    private long durableThroughMs, coverageCheckpointThroughMs;
    private bool persistenceFailed;
    private readonly Dictionary<Guid, SttJobFate> pendingResults = [];
    public bool PersistenceCleanupComplete => persistence?.CleanupComplete ?? true;
    public bool PersistenceIdle => persistence?.PendingWrites == 0 || persistence is null;
    private long ComputeThrough(Guid? completing = null) => Math.Min(RecoveryCeilingMs, Math.Min(EvidenceBoundarySamples / 16,
        segments.Where(s => !s.IsFinal && s.Id != completing && !pendingResults.ContainsKey(s.Id)).Select(s => s.StartMs)
        .Concat(buffers.Where(b => b.Samples.Count > 0).Select(b => b.Start / 16)).DefaultIfEmpty(EvidenceBoundarySamples / 16).Min()));
    private long ComputedFinalizedThroughMs => ComputeThrough();
    private bool CanPublish(Job job) => disposal is null && !lifetime.IsCancellationRequested && !hardFailure
        && job.Cancellation?.IsCancellationRequested != true
        && (job.Descriptor is not { } d || d.Valid(generation, unknown?.Fence ?? long.MaxValue, clock.Now));
    private Guid generation = Guid.NewGuid();
    private SttDrainPhase drainPhase;
    private SttUnknownInterval? unknown;
    private readonly long[] incompleteFrom = [long.MaxValue, long.MaxValue];
    private readonly Dictionary<Guid, Job> liveJobs = [];
    private readonly Dictionary<Guid, SttRecoveryJob> recoveryJobs = [];
    private Job? activeJob;
    private long? restoredSafeMs;
    private bool hardFailure;
    public Guid PcmGeneration => generation;
    public bool DrainEnabled => recoveryStore is not null;
    public SttRecoverySnapshot? RecoveryStatus { get { lock (gate) return recoveryStore is null ? null : RecoverySnapshot() with { ConfirmedThroughMs = FinalizedThroughMs }; } }
    public object? RecoverySummary { get { lock (gate) return recoveryStore is null ? null : new { version = gapCount == 0 ? 1 : 2, continuity = durableContinuity, phase = drainPhase, unknown, confirmedThroughMs = FinalizedThroughMs, incompleteFrom = incompleteFrom.ToArray(), jobs = recoveryJobs.Values.Select(j => new { j.Descriptor.JobId, j.Descriptor.Channel, j.Descriptor.Start, j.Descriptor.End, j.Descriptor.Expires, j.Fate }).ToArray() }; } }
    public event Action<SttUnknownInterval>? AdmissionClosed;

    public PcmRangeProof? PacketProof(long qpc, long start, int count)
    {
        if (recoveryStore is null || clock is not IPcmCaptureClock captureClock) return null;
        TimeSpan born = captureClock.FromPacketQpc(qpc);
        return new(start, start + count, qpc, born, born + TimeSpan.FromSeconds(PcmLifetimeSeconds), born <= clock.Now);
    }
    private void AddProof(Buffer buffer, PcmRangeProof? proof, long start, long end)
    {
        if (buffer.Proof.Count >= ProofCapacity) return; // Полнота descriptor затем проверяется; недоказанный хвост не допускается.
        buffer.Proof.Add(proof is null ? new(start, end, 0, TimeSpan.MinValue, TimeSpan.MinValue, false)
            : proof with { Start = start, End = end, Trusted = proof.Trusted && proof.Start <= start && proof.End >= end });
    }
    private static void TrimProof(Buffer buffer, long through)
    {
        buffer.Proof.RemoveAll(p => p.End <= through);
        if (buffer.Proof.Count > 0 && buffer.Proof[0].Start < through) buffer.Proof[0] = buffer.Proof[0] with { Start = through };
    }
    private bool ValidateBufferProof(Buffer buffer, int count)
    {
        if (recoveryStore is null) return true;
        long cursor = buffer.Start, end = cursor + count;
        foreach (var p in buffer.Proof.Where(p => p.Start < end))
        {
            if (!p.Trusted || p.Start != cursor || p.End <= p.Start || p.Born > clock.Now || p.Expires <= clock.Now
                || p.Expires - p.Born != TimeSpan.FromSeconds(PcmLifetimeSeconds)) return false;
            cursor = Math.Min(end, p.End);
        }
        return cursor == end;
    }
    private Job? CreateJob(Guid id, AudioChannel channel, long start, float[] audio, SttJobDiagnostics? diagnostic, Buffer buffer, PcmInputClass inputClass = PcmInputClass.Uncertain, int lookback = 0)
    {
        if (recoveryStore is null) return new(id, audio, diagnostic, InputClass: inputClass);
        long decoderStart = start - lookback, end = decoderStart + audio.Length;
        IEnumerable<PcmRangeProof> proof = buffer.Proof;
        if (lookback > 0) proof = buffer.History!.Proof.Concat(proof);
        var provenance = proof.Where(p => p.Start < end && p.End > decoderStart)
            .Select(p => p with { Start = Math.Max(decoderStart, p.Start), End = Math.Min(end, p.End) }).ToImmutableArray();
        var descriptor = new SttJobDescriptor(id, channel, generation, start, end, audio.Length,
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(audio.AsSpan()))), provenance)
        { CandidateInput = candidateInput ? true : null, DecoderStart = lookback > 0 ? decoderStart : null };
        if (!descriptor.Valid(generation, long.MaxValue, clock.Now) || transcriber is not IExpiringAudioTranscriber)
        { Fail("STT: исходный возраст или provenance PCM не подтверждены.", SttCancelCause.PcmExpired); return null; }
        if (liveJobs.Count >= 9) { Fail("Очередь STT заполнена.", SttCancelCause.QueueOverflow); return null; }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancellation.CancelAfter(descriptor.Expires - clock.Now);
        recoveryJobs.Add(id, new(descriptor, SttJobFate.Pending));
        return new(id, audio, diagnostic, descriptor, cancellation, inputClass);
    }
    private bool CanRun(Job job) => disposal is null && !lifetime.IsCancellationRequested && !hardFailure && (recoveryStore is null || job.Descriptor is { } d
        && job.Cancellation?.IsCancellationRequested == false && recoveryJobs.TryGetValue(job.Id, out var record)
        && record.Fate == SttJobFate.Pending && d.Valid(generation, unknown?.Fence ?? long.MaxValue, clock.Now)
        && d.Samples == job.Samples.Length);

    public bool TryBeginDrain(SttUnknownInterval interval)
    {
        lock (gate)
        {
            if (recoveryStore is null) return false;
            if (unknown is not null) return true;
            if (error is not null || interval.Version != 1 || interval.Generation != generation || interval.Start < 0
                || interval.End <= interval.Start || interval.Fence < 0 || interval.Fence > interval.Start || interval.Fence > BoundarySamples) return false;
            interval = interval with { Reason = interval.Reason.Length <= 256 ? interval.Reason : interval.Reason[..256] };
            unknown = interval; drainPhase = SttDrainPhase.Draining;
            error = "STT admission закрыт: " + interval.Reason;
            stopReason = TranscriptStopReason.TimelineFailure;
            status = CaptureStatus.Stopped;
            for (int i = 0; i < buffers.Length; i++)
            {
                if (buffers[i].Samples.Count > 0) incompleteFrom[i] = Math.Min(incompleteFrom[i], buffers[i].Start);
                for (int n = 0; n < buffers[i].Samples.Count; n++) buffers[i].Samples[n] = 0;
                buffers[i].Samples.Clear(); buffers[i].Proof.Clear(); ClearLookback(buffers[i]);
            }
            foreach (var job in liveJobs.Values.ToArray())
                if (!CanRun(job)) RejectJob(job, clock.Now >= job.Descriptor!.Expires ? SttJobFate.Expired : SttJobFate.Ineligible);
            try { PersistRecovery(); }
            catch (Exception failure) { Fail("Не сохранён unknown/fence: " + failure.Message); return true; }
            // At most one notification per source, outside capture locks.
            _ = Task.Run(() => AdmissionClosed?.Invoke(interval));
            CompleteWaiters();
            return true;
        }
    }
    private void RejectJob(Job job, SttJobFate fate)
    {
        if (recoveryStore is null || job.Descriptor is not { } d || !liveJobs.ContainsKey(job.Id)) return;
        recoveryJobs[job.Id] = new(d, fate);
        pendingResults.Remove(job.Id);
        incompleteFrom[(int)d.Channel] = Math.Min(incompleteFrom[(int)d.Channel], d.Start);
        job.Diagnostics?.Cancel(fate == SttJobFate.Expired ? SttCancelCause.PcmExpired : SttCancelCause.TimelineFailure);
        job.Cancellation?.Cancel();
        if (!ReferenceEquals(activeJob, job)) Array.Clear(job.Samples);
        if (!ReferenceEquals(activeJob, job)) job.Diagnostics?.Finish(SttOutcome.RemovedQueued);
        CompleteWaiters();
    }
    private void CheckOriginalExpiry()
    {
        if (recoveryStore is null) return;
        bool expired = false;
        foreach (var job in liveJobs.Values.ToArray())
            if (job.Descriptor!.Expires <= clock.Now && recoveryJobs[job.Id].Fate == SttJobFate.Pending) { expired = true; RejectJob(job, SttJobFate.Expired); }
        if (expired) { Fail("Истёк исходный срок submitted PCM.", SttCancelCause.PcmExpired); return; }
        foreach (var buffer in buffers)
            if (buffer.Proof.Any(p => p.Expires <= clock.Now)) { Fail("Истёк исходный срок PCM в накопительном буфере.", SttCancelCause.PcmExpired); break; }
    }
    private void EndRecoveryJob(Job job)
    {
        if (recoveryStore is null) return;
        if (ReferenceEquals(activeJob, job)) activeJob = null;
        liveJobs.Remove(job.Id);
        pendingResults.Remove(job.Id);
        job.Cancellation?.Dispose();
        if (drainPhase == SttDrainPhase.Open) recoveryJobs.Remove(job.Id);
        if (drainPhase == SttDrainPhase.Draining && liveJobs.Count == 0) drainPhase = SttDrainPhase.Drained;
        TryPersistRecovery();
        CompleteWaiters();
        UpdateStopCompletion();
    }
    public bool HasIncompleteRecoveryCoverage { get { lock (gate) return gapCount != 0 || unknown is not null || incompleteFrom.Any(at => at != long.MaxValue) || liveJobs.Count > 0 || buffers.Any(b => b.Samples.Count > 0); } }
    private void HardStop()
    {
        for (int i = 0; i < buffers.Length; i++) if (buffers[i].Samples.Count > 0) incompleteFrom[i] = Math.Min(incompleteFrom[i], buffers[i].Start);
        if (!hardFailure) drainPhase = SttDrainPhase.Stopped;
        // Fail closes execution eligibility before cancellation can resume an inline continuation.
        Fail("STT остановлен пользователем; незавершённые задания отменены.", SttCancelCause.Dispose);
        TryPersistRecovery();
    }
    private string? BoundaryError(long boundaryMs)
    {
        if (recoveryStore is null) return error;
        if (hardFailure) return error ?? "Ответ недоступен после жёсткой отмены STT.";
        long cut = checked(boundaryMs * 16);
        if (unknown is not null && cut > BoundarySamples) return "Ответ недоступен: вход до границы не был полностью получен.";
        if (unknown is not null && cut > unknown.Fence) return "Ответ недоступен: граница после неизвестного интервала.";
        if (incompleteFrom.Any(at => at < cut)) return "Ответ недоступен: неполное покрытие до исходной границы (отброшенный/истёкший PCM).";
        if (restoredSafeMs is { } safe && boundaryMs > safe) return "Ответ недоступен: незавершённая расшифровка утрачена при перезапуске.";
        bool eligibleStopWait = terminalStop && stopState == TranscriptStopState.Finishing && cut <= BoundarySamples;
        if (drainPhase is SttDrainPhase.Failed or SttDrainPhase.Stopped && !IsReady(boundaryMs) && !eligibleStopWait)
            return error ?? "STT завершён без полного покрытия.";
        return null;
    }
    public bool HasDurableRestoredCoverage(long boundaryMs) => restoredSafeMs is { } safe && safe >= boundaryMs;
    public void ValidateAnswerCoverage(long boundaryMs)
    {
        lock (gate)
        {
            ThrowForBoundary(boundaryMs);
            if (!IsReady(boundaryMs)) throw new SttCoverageException("Ответ недоступен: расшифровка до границы не сохранена полностью.");
        }
    }
    private void ThrowForBoundary(long boundaryMs)
    {
        ObjectDisposedException.ThrowIf(disposal is not null, this);
        if (BoundaryError(boundaryMs) is { } failure) throw new SttCoverageException(failure);
    }
    private long RecoveryCeilingMs => recoveryStore is null ? long.MaxValue : Math.Min(restoredSafeMs ?? long.MaxValue,
        Math.Min(unknown?.Fence ?? long.MaxValue, incompleteFrom.Min()) / 16);
    private SttRecoverySnapshot RecoverySnapshot(Guid? completing = null)
    {
        long through = ComputeThrough(completing);
        var jobs = recoveryJobs.Values.Select(record => pendingResults.TryGetValue(record.Descriptor.JobId, out var fate)
            ? record with { Fate = fate } : record).ToArray();
        var snapshot = new SttRecoverySnapshot(gapCount == 0 ? 1 : 2, generation, drainPhase, unknown,
            Math.Min(through, firstGapSample / 16), incompleteFrom.ToArray(), jobs, null, StopStatus)
        {
            Continuity = gapCount == 0 ? null : new(gapCount, firstGapSample, through, awaiting[0], awaiting[1],
                null, ++checkpointRevision, lastOnChannel[0], lastOnChannel[1], latestGap)
        };
        if (!persistenceFailed) return snapshot;
        return snapshot with {
            Version = durableContinuity is null ? 1 : 2,
            Continuity = durableContinuity is { } committed ? committed with { Change = null } : null,
            ConfirmedThroughMs = Math.Min(durableThroughMs, snapshot.ConfirmedThroughMs)
        };
    }
    private void CheckpointCandidateCoverage()
    {
        if (!candidateInput || persistence is not { PendingWrites: 0 } || error is not null || drainPhase != SttDrainPhase.Open) return;
        long through = ComputeThrough();
        long acknowledged = durableContinuity?.EvidenceThroughMs ?? durableThroughMs;
        if (through <= Math.Max(acknowledged, coverageCheckpointThroughMs)) return;
        coverageCheckpointThroughMs = through;
        _ = PersistRecovery();
    }

    private Task<SttRecoverySnapshot> PersistRecovery()
    {
        if (persistence is null) return Task.FromResult(RecoverySnapshot());
        var save = persistence.Save(RecoverySnapshot());
        _ = ObservePersistence(save);
        return save;
    }
    private async Task ObservePersistence(Task<SttRecoverySnapshot> save)
    {
        try
        {
            var acknowledged = await save.ConfigureAwait(false);
            lock (gate)
            {
                if (disposal is not null || hardFailure) return;
                Acknowledge(acknowledged);
                CompleteWaiters();
            }
        }
        catch (Exception failure)
        {
            lock (gate)
            {
                if (persistenceFailed || disposal is not null) return;
                persistenceFailed = true;
                Fail("Recovery persistence failed: " + failure.Message);
                // One bounded best-effort failure checkpoint. Never retry a failed checkpoint.
                if (persistence is not null) _ = IgnoreFailureCheckpoint(persistence.Save(RecoverySnapshot()));
            }
        }
    }
    private static async Task IgnoreFailureCheckpoint(Task<SttRecoverySnapshot> save)
    { try { await save.ConfigureAwait(false); } catch { } }
    private void TryPersistRecovery()
    {
        if (!persistenceFailed) _ = PersistRecovery();
    }
    private void RestoreRecovery()
    {
        if (recoveryStore?.ReadSttRecovery() is not { } saved) return;
        if (saved.Version is not (1 or 2) || saved.Jobs.Length > 9 || saved.IncompleteFrom.Length != 2) throw new IOException("Неверная версия recovery.");
        // Ни исходные ticks, ни старый PCM не возобновляются после рестарта.
        RestoreContinuity(saved);
        unknown = saved.Unknown; generation = saved.Generation; drainPhase = SttDrainPhase.Interrupted;
        if (saved.Completion is { } completion)
        {
            terminalEnds = completion.ChannelEndSamples.ToArray();
            stopReason = completion.Reason;
            stopDetails.AddRange(completion.Details.Take(16));
        }
        hardFailure = saved.Phase == SttDrainPhase.Failed || saved.Phase == SttDrainPhase.Stopped && saved.IncompleteFrom.Any(at => at != long.MaxValue);
        Array.Copy(saved.IncompleteFrom, incompleteFrom, 2);
        foreach (var record in saved.Jobs)
        {
            var d = record.Descriptor;
            bool savedFinal = segments.Any(s => s.Id == d.JobId && s.IsFinal && s.Channel == d.Channel
                && s.StartMs == d.Start / 16 && s.EndMs == (d.End + 15) / 16);
            var fate = savedFinal ? SttJobFate.Finalized : record.Fate == SttJobFate.Empty ? SttJobFate.Empty : SttJobFate.Interrupted;
            recoveryJobs[d.JobId] = record with { Fate = fate };
            if (fate == SttJobFate.Interrupted) incompleteFrom[(int)d.Channel] = Math.Min(incompleteFrom[(int)d.Channel], d.Start);
        }
        restoredSafeMs = saved.TranscriptFingerprint == recoveryStore.TranscriptFingerprint ? (saved.Continuity?.EvidenceThroughMs ?? saved.ConfirmedThroughMs) : 0;
        if (terminalStop && terminalEnds.Length == 2)
        {
            for (int i = 0; i < 2; i++)
            {
                buffers[i].Start = buffers[i].Received = terminalEnds[i];
                buffers[i].Completed = true;
            }
        }
        else foreach (var buffer in buffers) buffer.Start = buffer.Received = Math.Max(buffer.Received, restoredSafeMs.Value * 16);
        if (saved.Completion?.State == TranscriptStopState.StoppedWithGaps && durableContinuity is not null) return;
        error = "STT восстановлен без временного PCM; автоматическое продолжение запрещено.";
    }
}
