using System.Diagnostics;

namespace SpeechAnalyzer.Core;

public enum SttStage { Queued = 1, Dequeued, WorkerStarted, WorkerReady, InputSent, ResponseReceived,
    CancelRequested, WorkerExited, FinalizationAcknowledged, Terminal, WorkerReused }
public enum SttCancelCause { None, TimelineFailure, Deadline, Dispose, PcmExpired, QueueOverflow, RecognitionFailure, SourceFailure, External, PeerDeadline }
public enum SttOutcome { None, Finalized, EmptyResult, RemovedQueued, CancelledActive, Failed, Rejected,
    FinalizedAfterCancellation, EmptyAfterCancellation }
public enum SttModelSelection { Unknown, Base, LargeV3 }
public enum SttBackendEvidence { Unknown, CudaResponseVerified }
public enum SttDiagnosticScope { Job, Worker }

// Только типизированные метаданные: никаких строк из модели, PCM, stderr или путей.
public sealed record SttDiagnosticRecord(int Version, Guid JobId, AudioChannel Channel, SttStage Stage,
    long Ticks, long Frequency, SttCancelCause CancelCause, SttOutcome Outcome, bool Dequeued,
    SttModelSelection ConfiguredModel, SttBackendEvidence Backend, int? ExitCode, Guid WorkerId, SttDiagnosticScope Scope);

public sealed class SttDiagnostics(AudioDiagnosticJournal journal)
{
    public SttJobDiagnostics Job(Guid id, AudioChannel channel) => new(journal, id, channel);
}

public sealed class SttJobDiagnostics
{
    // На job не больше24 записей, в том числе при повторных cancellation/exit наблюдениях.
    public const int EventLimit = 24;
    private readonly AudioDiagnosticJournal journal;
    private readonly Guid id;
    private readonly AudioChannel channel;
    private readonly object gate = new();
    private int emitted;
    private bool dequeued;
    private SttCancelCause cause;
    private SttOutcome outcome;
    private SttModelSelection model;
    private SttBackendEvidence backend;
    private Guid workerId;
    internal SttJobDiagnostics(AudioDiagnosticJournal journal, Guid id, AudioChannel channel)
    { this.journal = journal; this.id = id; this.channel = channel; }
    public SttCancelCause Cancellation { get { lock (gate) return cause; } }
    public void Stage(SttStage stage, int? exitCode = null, SttCancelCause? observedCause = null)
    {
        lock (gate)
        {
            if (stage == SttStage.Dequeued) dequeued = true;
            Emit(stage, exitCode, observedCause);
        }
    }
    public void Configure(SttModelSelection selection) { lock (gate) model = selection; }
    public void AttachWorker(Guid value) { lock (gate) workerId = value; }
    public void ObserveBackend(bool cudaVerified) { if (cudaVerified) lock (gate) backend = SttBackendEvidence.CudaResponseVerified; }
    public void WorkerObservation(SttWorkerObservation value)
    {
        lock (gate)
        {
            if (emitted++ >= EventLimit) { journal.NoteDropped(); return; }
            journal.TryWrite("stt_worker_lifecycle", new { Version = 1, JobId = id, WorkerId = workerId, Channel = channel,
                Scope = value.Ordinal == 0 ? SttDiagnosticScope.Worker : SttDiagnosticScope.Job,
                Observation = value, ReceivedTicks = Stopwatch.GetTimestamp(), NativeBoundaryObserved = false });
        }
    }
    public void WorkerObservationSummary(bool connected, long received, long dropped, bool outputClosed, string? error)
    {
        lock (gate)
        {
            if (emitted++ >= EventLimit) { journal.NoteDropped(); return; }
            journal.TryWrite("stt_worker_observation_summary", new { Version = 1, JobId = id, WorkerId = workerId,
                Scope = SttDiagnosticScope.Worker, Connected = connected, Received = received, Dropped = dropped,
                OutputClosed = outputClosed, Error = error, Ticks = Stopwatch.GetTimestamp(), Frequency = Stopwatch.Frequency });
        }
    }
    public void Cancel(SttCancelCause reason)
    {
        lock (gate)
        {
            if (cause != SttCancelCause.None || outcome != SttOutcome.None) return;
            cause = reason; Emit(SttStage.CancelRequested);
        }
    }
    public void Finish(SttOutcome result)
    {
        lock (gate)
        {
            if (outcome != SttOutcome.None) return;
            if (cause != SttCancelCause.None)
                result = result == SttOutcome.Finalized ? SttOutcome.FinalizedAfterCancellation :
                    result == SttOutcome.EmptyResult ? SttOutcome.EmptyAfterCancellation : result;
            outcome = result; Emit(SttStage.Terminal);
        }
    }
    private void Emit(SttStage stage, int? exitCode = null, SttCancelCause? observedCause = null)
    {
        // Вызов только bounded TryWrite: сериализация/диск выполняются существующим worker.
        // Лимит journal и его Dropped/Error остаются едиными для всей диагностики сессии.
        if (emitted++ >= EventLimit) { journal.NoteDropped(); return; }
        try { journal.TryWrite("stt_lifecycle", new SttDiagnosticRecord(1, id, channel, stage,
            Stopwatch.GetTimestamp(), Stopwatch.Frequency, observedCause ?? cause, outcome, dequeued, model, backend, exitCode, workerId,
            stage is SttStage.WorkerStarted or SttStage.WorkerReady or SttStage.WorkerReused or SttStage.WorkerExited
                || stage == SttStage.CancelRequested && observedCause is not null ? SttDiagnosticScope.Worker : SttDiagnosticScope.Job)); }
        catch { journal.NoteDropped(); }
    }
}

// Старый интерфейс и его потребители сохраняются; диагностика не требуется имитаторам/другим backend.
public interface ISttDiagnosticTranscriber : IAudioTranscriber
{
    Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken, SttJobDiagnostics? diagnostics);
}
