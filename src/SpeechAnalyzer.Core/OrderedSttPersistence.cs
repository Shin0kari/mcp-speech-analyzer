using System.Text.Json;
using System.Threading.Channels;

namespace SpeechAnalyzer.Core;

// One owner survives cancellation of its callers until the synchronous store returns.
// Commands contain immutable metadata, never PCM or closures over a Job.
public sealed class OrderedSttPersistence : IDisposable
{
    private static readonly Dictionary<object, OrderedSttPersistence> owners = [];
    private static readonly object ownersGate = new();
    private readonly object identity;
    private readonly ISttRecoveryStore store;
    private readonly Action<TranscriptSegment> append;
    private readonly Channel<Write> writes = Channel.CreateBounded<Write>(new BoundedChannelOptions(32)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource closed = new();
    private readonly Task pump;
    private readonly object lifecycle = new();
    private int pendingWrites;
    private bool released, admissionFailed;
    private sealed record Write(SttRecoverySnapshot Snapshot, TranscriptSegment? Transcript,
        PcmInputClassification? Classification, TaskCompletionSource<SttRecoverySnapshot> Ack);
    internal static void VerifyStoreAvailable(object identity)
    {
        lock (ownersGate)
            if (owners.ContainsKey(identity)) throw new IOException("The recovery store still has an outstanding persistence owner.");
    }
    public OrderedSttPersistence(ISttRecoveryStore store, Action<TranscriptSegment> append)
    {
        this.store = store; this.append = append; identity = store.PersistenceIdentity;
        lock (ownersGate)
        {
            if (owners.ContainsKey(identity)) throw new IOException("The recovery store still has an outstanding persistence owner.");
            owners.Add(identity, this);
        }
        pump = Task.Run(RunAsync);
    }
    public Task<SttRecoverySnapshot> Save(SttRecoverySnapshot snapshot, TranscriptSegment? transcript = null,
        PcmInputClassification? classification = null)
    {
        // Copy every mutable container at the ownership boundary.
        snapshot = snapshot with { Jobs = snapshot.Jobs.ToArray(), IncompleteFrom = snapshot.IncompleteFrom.ToArray(),
            Completion = snapshot.Completion is { } stop ? stop with { ChannelEndSamples = stop.ChannelEndSamples.ToArray(), Details = stop.Details.ToArray() } : null };
        var ack = new TaskCompletionSource<SttRecoverySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (lifecycle)
        {
            pendingWrites++;
            if (closed.IsCancellationRequested || admissionFailed && snapshot.Phase != SttDrainPhase.Failed || !writes.Writer.TryWrite(new(snapshot, transcript, classification, ack)))
            {
                pendingWrites--;
                admissionFailed = true;
                ack.SetException(new IOException("The bounded persistence lane is closed or full."));
            }
        }
        return ack.Task;
    }
    private async Task RunAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var write in writes.Reader.ReadAllAsync())
            {
                try
                {
                    // After any failed transaction, only conservative failure snapshots may
                    // pass. A later Open snapshot must not certify a failed transcript append.
                    if (closed.IsCancellationRequested || failure is not null && write.Snapshot.Phase != SttDrainPhase.Failed)
                        throw failure ?? new IOException("Persistence cleanup is incomplete.");
                    if (write.Classification is { } classification) store.SaveInputClassification(classification);
                    if (write.Transcript is { } transcript) append(transcript);
                    if (write.Snapshot.Continuity?.Change is { } gap)
                        ((IChannelGapStore)store).SaveGap(gap);
                    var exact = write.Snapshot with { TranscriptFingerprint = store.TranscriptFingerprint };
                    try { store.SaveSttRecovery(exact); }
                    catch when (JsonSerializer.Serialize(store.ReadSttRecovery()) == JsonSerializer.Serialize(exact)) { }
                    // This acknowledges the concrete saved value, not a revision inferred from a later write.
                    write.Ack.TrySetResult(exact);
                }
                catch (Exception error) { failure = error; write.Ack.TrySetException(error); }
                finally { lock (lifecycle) pendingWrites--; }
            }
        }
        finally { ReleaseIdentity(); }
    }
    private void ReleaseIdentity()
    {
        lock (ownersGate)
        {
            if (owners.TryGetValue(identity, out var owner) && ReferenceEquals(owner, this)) owners.Remove(identity);
            released = true;
        }
    }
    public void Dispose()
    {
        lock (lifecycle)
        {
            closed.Cancel(); writes.Writer.TryComplete();
            if (pendingWrites == 0) ReleaseIdentity();
        }
        // Do not await an uncancellable OS write. The pump retains store exclusivity.
    }
    public bool CleanupComplete { get { lock (ownersGate) return released; } }
    public int PendingWrites { get { lock (lifecycle) return pendingWrites; } }
}
