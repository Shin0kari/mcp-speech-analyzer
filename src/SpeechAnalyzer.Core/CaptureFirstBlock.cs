namespace SpeechAnalyzer.Core;

// A separate numerical window; it never creates an anomaly or changes capture admission.
public sealed record CaptureFirstBlockSnapshot(long AnchorTicks, long WindowEndTicks,
    int BeforeCapacity, int AfterCapacity, long BeforeOverwritten, long WindowDropped, long OutsideWindow, long CrossingWindow,
    long SubmissionStart, long SubmissionEnd, CaptureTimingRecord AcquireAtAnchor,
    CaptureTimingRecord LastReleaseBeforeAnchor, CaptureTimingRecord FirstReleaseAfterAnchor,
    CaptureTimingRecord[] Before, CaptureTimingRecord[] After);

internal sealed class CaptureFirstBlockLane(CaptureTimingRun run)
{
    internal const int BeforeCapacity = 512, AfterCapacity = 8192;
    private readonly CaptureTimingRecord[] before = new CaptureTimingRecord[BeforeCapacity];
    private readonly CaptureTimingRecord[] after = new CaptureTimingRecord[AfterCapacity];
    private int beforeWrite, beforeCount, afterCount;
    private long overwritten, dropped, outside, crossing, anchor, submissionStart, submissionEnd;
    private CaptureTimingRecord acquireAtAnchor, releaseBeforeAnchor, firstReleaseAfterAnchor;
    internal void SubmissionStarted(long at) { if (submissionStart == 0) submissionStart = at; }
    internal void SubmissionEnded(long at) { if (submissionEnd == 0) submissionEnd = at; }
    internal void Write(CaptureTimingRecord row, CaptureTimingRecord acquire, CaptureTimingRecord release)
    {
        if (anchor == 0 && run.FirstBlockTicks != 0)
        {
            anchor = run.FirstBlockTicks; acquireAtAnchor = acquire; releaseBeforeAnchor = release;
        }
        if (anchor == 0)
        {
            if (beforeCount == before.Length) overwritten++; else beforeCount++;
            before[beforeWrite] = row; beforeWrite = (beforeWrite + 1) % before.Length;
        }
        else if (row.End - anchor <= 4 * run.Frequency)
        {
            if (afterCount < after.Length) after[afterCount++] = row; else dropped++;
        }
        else { outside++; if (row.Start <= anchor + 4 * run.Frequency) crossing++; }
        // A stalled callback's release remains available even beyond the four-second window.
        if (anchor != 0 && firstReleaseAfterAnchor.Kind == 0 && row.Kind == CaptureTimingKind.Release)
            firstReleaseAfterAnchor = row;
    }
    internal CaptureFirstBlockSnapshot SnapshotAfterJoin()
    {
        var history = new CaptureTimingRecord[beforeCount];
        int first = (beforeWrite - beforeCount + before.Length) % before.Length;
        for (int i = 0; i < history.Length; i++) history[i] = before[(first + i) % before.Length];
        return new(anchor, anchor == 0 ? 0 : anchor + 4 * run.Frequency, BeforeCapacity, AfterCapacity,
            overwritten, dropped, outside, crossing, submissionStart, submissionEnd, acquireAtAnchor,
            releaseBeforeAnchor, firstReleaseAfterAnchor, history, after.AsSpan(0, afterCount).ToArray());
    }
}
