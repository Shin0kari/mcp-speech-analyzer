namespace SpeechAnalyzer.Core;

public enum TranscriptStopState { Open, Finishing, Complete, Incomplete, StoppedWithGaps }
public enum TranscriptStopReason { UserStop, TimelineFailure, RecognitionFailure, Deadline, PcmExpired, QueueFull, ForcedAbort, PersistenceFailure, CleanupFailure }
public sealed record TranscriptStopStatus(int Version, TranscriptStopState State, TranscriptStopReason Reason,
    long[] ChannelEndSamples, string? PrimaryIncident, string[] Details);

public sealed partial class BufferedTranscriptSource
{
    private bool terminalStop;
    private long[] terminalEnds = [];
    private TranscriptStopState stopState;
    private TranscriptStopReason stopReason = TranscriptStopReason.UserStop;
    private readonly List<string> stopDetails = [];
    private readonly TaskCompletionSource stopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool terminalSavePending;
    public TranscriptStopStatus StopStatus { get { lock (gate) return new(1, stopState, stopReason, terminalEnds.ToArray(), error, stopDetails.ToArray()); } }

    private void GracefulStop()
    {
        if (terminalStop) return;
        terminalStop = true;
        terminalEnds = buffers.Select(b => b.Received).ToArray();
        status = CaptureStatus.Stopped;
        if (error is not null || unknown is not null || hardFailure)
        {
            stopState = TranscriptStopState.Incomplete;
            TryPersistRecovery();
            stopCompletion.TrySetResult();
            return;
        }
        stopState = TranscriptStopState.Finishing;
        // Flush only real accepted samples. Existing Answer cuts have already split their prefix.
        for (int i = 0; i < buffers.Length && error is null; i++) FlushBuffer(i, terminalEnds[i]);
        foreach (var buffer in buffers) ClearLookback(buffer);
        if (recoveryStore is not null && !hardFailure) drainPhase = SttDrainPhase.Stopped;
        UpdateStopCompletion();
        CompleteWaiters();
    }
    private void UpdateStopCompletion()
    {
        if (!terminalStop || stopCompletion.Task.IsCompleted) return;
        if (error is not null || hardFailure || unknown is not null || incompleteFrom.Any(at => at != long.MaxValue))
            stopState = TranscriptStopState.Incomplete;
        else
        {
            if (audioCreated.Count != 0 || buffers.Any(b => b.Samples.Count != 0)) return;
            if (terminalSavePending) return;
            if (persistence is not null)
            {
                terminalSavePending = true;
                var snapshot = RecoverySnapshot();
                snapshot = snapshot with { Completion = snapshot.Completion! with { State = gapCount > 0 ? TranscriptStopState.StoppedWithGaps : TranscriptStopState.Complete } };
                _ = CompleteStopSaveAsync(persistence.Save(snapshot));
                return;
            }
            stopState = TranscriptStopState.Complete;
        }
        stopCompletion.TrySetResult();
    }
    private async Task CompleteStopSaveAsync(Task<SttRecoverySnapshot> save)
    {
        try
        {
            var acknowledged = await save.ConfigureAwait(false);
            lock (gate)
            {
                if (stopCompletion.Task.IsCompleted) return;
                Acknowledge(acknowledged);
                stopState = acknowledged.Completion!.State;
                CompleteWaiters(); stopCompletion.TrySetResult();
            }
        }
        catch (Exception failure)
        {
            lock (gate)
            {
                persistenceFailed = true;
                RecordStopFailure(TranscriptStopReason.PersistenceFailure, "Terminal persistence failed: " + failure.Message);
                stopState = TranscriptStopState.Incomplete; hardFailure = true;
                CompleteWaiters(); stopCompletion.TrySetResult();
            }
        }
    }
    private void RecordStopFailure(TranscriptStopReason reason, string message)
    {
        if (error is null) { stopReason = reason; error = message; }
        if (!stopDetails.Contains(message) && stopDetails.Count < 16) stopDetails.Add(message);
    }
    public Task WaitForStopCompletionAsync(CancellationToken token) => stopCompletion.Task.WaitAsync(token);
    public void Abort(TranscriptStopReason reason = TranscriptStopReason.ForcedAbort, string message = "STT принудительно отменён; остаток не подтверждён.")
    {
        lock (gate)
        {
            if (stopState is TranscriptStopState.Complete or TranscriptStopState.StoppedWithGaps) return;
            terminalStop = true;
            if (terminalEnds.Length == 0) terminalEnds = buffers.Select(b => b.Received).ToArray();
            RecordStopFailure(reason, message);
            HardStop();
            UpdateStopCompletion();
        }
    }
}
