using System.IO.Pipes;
using SpeechAnalyzer.Core;

namespace SpeechAnalyzer.Audio.Windows;

public sealed record SttObservationStatus(bool Enabled, bool Connected, long Received, long Dropped,
    bool OutputClosed, string? Error);

// Максимум восемь недавних job references без PCM. Поздние события не приписываются новому job.
internal sealed class SttObservationReceiver : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<long, SttJobDiagnostics?> jobs = [];
    private readonly SttJobDiagnostics? first;
    private readonly Action<SttWorkerObservation>? observe;
    private readonly Dictionary<long, List<SttWorkerPhase>> phases = [];
    private readonly object gate = new();
    private readonly Task reader;
    private long ordinal, received, dropped, sequence;
    private bool connected, outputClosed;
    private string? error;
    public string Name { get; } = "sa-stt-" + Guid.NewGuid().ToString("N");
    public SttObservationStatus Status { get { lock (gate) return new(true, connected, received, dropped, outputClosed, error); } }
    public SttObservationReceiver(SttJobDiagnostics? diagnostic, Action<SttWorkerObservation>? observe = null)
    {
        first = diagnostic;
        this.observe = observe;
        pipe = new(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        reader = Task.Run(ReadAsync);
    }
    public void Register(SttJobDiagnostics? diagnostic)
    {
        lock (gate)
        {
            jobs.Add(++ordinal, diagnostic);
            phases[ordinal] = [];
            if (jobs.Count > 8) jobs.Remove(jobs.Keys.Min());
            if (phases.Count > 8) phases.Remove(phases.Keys.Min());
        }
    }
    private async Task ReadAsync()
    {
        try
        {
            await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
            lock (gate) connected = true;
            while (await SttObservationEmitter.ReadFrameAsync(pipe, stop.Token).ConfigureAwait(false) is { } value)
            {
                SttJobDiagnostics? diagnostic;
                lock (gate)
                {
                    if (++received > SttObservationEmitter.EventLimit + 1 || value.Sequence <= sequence)
                        throw new IOException("Нарушен порядок/лимит diagnostic frames.");
                    dropped += Math.Max(0, value.Sequence - sequence - 1); sequence = value.Sequence;
                    dropped = Math.Max(dropped, value.Dropped);
                    if (value.Phase == SttWorkerPhase.OutputClosed) outputClosed = true;
                    diagnostic = value.Ordinal == 0 ? first : jobs.GetValueOrDefault(value.Ordinal);
                    if (diagnostic is null && observe is null) dropped++;
                    if (value.Ordinal > 0 && phases.TryGetValue(value.Ordinal, out var chain))
                    { if (chain.Count == 9) throw new IOException("ExcessCallObservations"); chain.Add(value.Phase); }
                }
                observe?.Invoke(value);
                diagnostic?.WorkerObservation(value);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception failure) { lock (gate) error = failure.GetType().Name; }
    }
    public async Task WaitForCallAsync(long current, CancellationToken token)
    {
        SttWorkerPhase[] expected = [SttWorkerPhase.HeaderAccepted, SttWorkerPhase.InputReadCompleted,
            SttWorkerPhase.ManagedProcessingEntered, SttWorkerPhase.WhisperEnumerationEntered,
            SttWorkerPhase.WhisperEnumerationExited, SttWorkerPhase.ManagedProcessingExited,
            SttWorkerPhase.ResultSerializationEntered, SttWorkerPhase.ResultSerialized, SttWorkerPhase.ResultSendCompleted];
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (gate)
            {
                if (error is not null || dropped > 0) throw new IOException("TransportObservationFailure");
                if (phases.TryGetValue(current, out var chain) && chain.Count == 9)
                { if (!chain.SequenceEqual(expected)) throw new IOException("TransportObservationOrder"); return; }
            }
            if (started.ElapsedMilliseconds >= 250) throw new IOException("IncompleteCallObservations");
            await Task.Delay(2, token);
        }
    }
    public async Task CompleteAsync()
    {
        try { await reader.WaitAsync(TimeSpan.FromMilliseconds(300)); }
        catch (TimeoutException) { lock (gate) error ??= "IncompleteObservationTail"; }
    }
    public void Dispose()
    {
        stop.Cancel(); pipe.Dispose();
        _ = reader.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
