using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed record EvidenceCursor(long YouTextEnd, long OtherTextEnd, long YouGap, long OtherGap);

public sealed record ChannelGap(long Number, Guid Id, Guid Generation, AudioChannel Channel,
    long Start, long? Resume, long EvidenceQpc100ns, string Reason, long PreviousOnChannel);
public sealed record ContinuityCheckpoint(long Count, long FirstUncertainSample, long EvidenceThroughMs,
    ChannelGap? AwaitingYou, ChannelGap? AwaitingOther, ChannelGap? Change, long Revision, long LastYou, long LastOther, ChannelGap? Latest);
public interface IChannelGapStore
{
    void SaveGap(ChannelGap gap);
    ChannelGap ReadGap(long number, bool open);
}

public sealed partial class SessionJournal : IChannelGapStore
{
    private string GapPath(long number, bool open) =>
        Path.Combine(DirectoryPath, "channel-gaps", $"{number:D12}-{(open ? "open" : "closed")}.json");

    public void SaveGap(ChannelGap gap)
    {
        if (gap.Number <= 0 || gap.Id == Guid.Empty || gap.Start < 0 || gap.Resume < gap.Start)
            throw new IOException("Invalid channel gap.");
        lock (gate)
        {
            var path = GapPath(gap.Number, gap.Resume is null);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(gap);
            if (File.Exists(path))
            {
                if (File.ReadAllText(path) != json) throw new IOException("Conflicting channel gap.");
                return;
            }
            try { WriteAtomic(path, json); }
            catch when (File.Exists(path) && File.ReadAllText(path) == json) { }
        }
    }

    public ChannelGap ReadGap(long number, bool open)
    {
        lock (gate)
        {
            var result = JsonSerializer.Deserialize<ChannelGap>(File.ReadAllText(GapPath(number, open)));
            if (result?.Number != number || (result.Resume is null) != open) throw new IOException("Invalid gap journal.");
            return result;
        }
    }
}

public sealed partial class BufferedTranscriptSource
{
    private readonly bool gapContinuation;
    private readonly ChannelGap?[] awaiting = new ChannelGap?[2];
    private long gapCount, checkpointRevision;
    private ChannelGap? latestGap;
    private readonly long[] lastOnChannel = new long[2];
    private long firstGapSample = long.MaxValue;
    private ContinuityCheckpoint? durableContinuity;
    public bool GapContinuationEnabled => gapContinuation;
    public long EvidenceThroughMs
    {
        get { lock (gate) return Math.Min(ComputedFinalizedThroughMs, durableContinuity?.EvidenceThroughMs ?? FinalizedThroughMs); }
    }

    private long EvidenceBoundarySamples
    {
        get
        {
            if (gapCount == 0) return BoundarySamples;
            if (terminalStop && buffers.All(b => b.Completed) || restoredSafeMs.HasValue)
                return buffers.Max(b => b.Received);
            if (awaiting[0] is not null && awaiting[1] is not null) return RequestedSamples;
            if (awaiting[0] is not null) return buffers[1].Received;
            if (awaiting[1] is not null) return buffers[0].Received;
            return BoundarySamples;
        }
    }

    private bool GapWritesAcknowledged => gapCount == 0 || durableContinuity is { } c && c.Count == gapCount &&
        c.AwaitingYou == awaiting[0] && c.AwaitingOther == awaiting[1];

    private void Acknowledge(SttRecoverySnapshot snapshot)
    {
        durableThroughMs = Math.Max(durableThroughMs, snapshot.ConfirmedThroughMs);
        if (snapshot.Continuity is { } c && (durableContinuity is null || c.Revision >= durableContinuity.Revision))
            durableContinuity = c;
    }

    private void RestoreContinuity(SttRecoverySnapshot saved)
    {
        if (saved.Continuity is not { } c) return;
        if (saved.Version != 2 || recoveryStore is not IChannelGapStore store || c.Count <= 0 ||
            c.FirstUncertainSample < 0 || c.EvidenceThroughMs < saved.ConfirmedThroughMs)
            throw new IOException("Invalid continuity checkpoint.");
        store.ReadGap(c.Count, c.AwaitingYou?.Number == c.Count || c.AwaitingOther?.Number == c.Count);
        firstGapSample = c.FirstUncertainSample;
        gapCount = c.Count;
        checkpointRevision = c.Revision;
        latestGap = c.Latest;
        lastOnChannel[0] = c.LastYou; lastOnChannel[1] = c.LastOther;
        awaiting[0] = c.AwaitingYou; awaiting[1] = c.AwaitingOther;
        if (saved.TranscriptFingerprint == recoveryStore.TranscriptFingerprint) durableContinuity = c;
        if (saved.Completion?.State == TranscriptStopState.StoppedWithGaps && durableContinuity is not null)
        {
            stopState = TranscriptStopState.StoppedWithGaps;
            terminalStop = true;
            stopCompletion.TrySetResult();
        }
    }

    internal bool AwaitingChannel(AudioChannel channel)
    {
        lock (gate) return awaiting[(int)channel] is not null;
    }

    internal void ReportReorderOverflow(string message)
    {
        lock (gate) Fail(message, SttCancelCause.QueueOverflow);
    }

    internal void TransitionGap(AudioChannel channel, long uncertainStart, long? resume, long qpc, string reason)
    {
        lock (gate)
        {
            if (!gapContinuation || error is not null || terminalStop) return;
            int lane = (int)channel;
            var buffer = buffers[lane];
            var gap = awaiting[lane];
            if (gap is null)
            {
                firstGapSample = Math.Min(firstGapSample, uncertainStart);
                FlushBuffer(lane, buffer.Received);
                ClearLookback(buffer);
                if (error is not null) return;
                gap = new(++gapCount, Guid.NewGuid(), generation, channel, uncertainStart, resume, qpc, reason, lastOnChannel[lane]);
                lastOnChannel[lane] = gapCount;
            }
            else
            {
                if (resume is null) return;
                gap = gap with { Resume = resume };
            }
            latestGap = gap;
            awaiting[lane] = resume is null ? gap : null;
            if (resume is long anchor) BeginChannel(channel, anchor);
            var snapshot = RecoverySnapshot();
            var save = persistence!.Save(snapshot with { Continuity = snapshot.Continuity! with { Change = gap } });
            _ = ObservePersistence(save);
        }
    }

    public IReadOnlyList<ChannelGap> ReadEvidenceGaps(long afterMs, long throughMs, EvidenceCursor? consumed = null)
    {
        ContinuityCheckpoint? saved;
        lock (gate) saved = durableContinuity;
        if (saved is null || recoveryStore is not IChannelGapStore store) return [];
        var result = new List<ChannelGap>();
        foreach (long head in new[] { saved.LastYou, saved.LastOther })
        {
            long i = head;
            while (i > 0)
            {
                var gap = store.ReadGap(i, saved.AwaitingYou?.Number == i || saved.AwaitingOther?.Number == i);
                long known = gap.Channel == AudioChannel.You ? consumed?.YouGap ?? 0 : consumed?.OtherGap ?? 0;
                if (i <= known) break;
                if (consumed is null && gap.Resume <= checked(afterMs * 16)) break;
                if (gap.Start < checked(throughMs * 16))
                    result.Add(gap.Resume > checked(throughMs * 16) ? gap with { Resume = null } : gap);
                if (gap.PreviousOnChannel >= i) throw new IOException("Invalid gap chain.");
                i = gap.PreviousOnChannel;
            }
        }
        return result.OrderBy(g => g.Start).ThenBy(g => g.Channel).ToArray();
    }

    internal string? ContinuityMessage
    {
        get
        {
            lock (gate)
            {
                if (durableContinuity is not { } c || status != CaptureStatus.Capturing) return null;
                if (c.AwaitingYou is not null || c.AwaitingOther is not null)
                {
                    string waiting = c.AwaitingYou is null ? "Собеседник" : c.AwaitingOther is null ? "Вы" : "оба источника";
                    return $"Запись продолжается. Ожидание временной метки: {waiting}.";
                }
                string channel = c.Latest?.Channel == AudioChannel.You ? "Вы" : "Собеседник";
                return $"Запись и расшифровка продолжаются. Пропусков: {c.Count}. Последний: {channel}.";
            }
        }
    }
}
