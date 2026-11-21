using System.Diagnostics;
using System.Text.Json;
using SpeechAnalyzer.Core;
using SttGpuWorker;

namespace SpeechAnalyzer.Audio.Windows;

// Отмена/тайм-аут завершают только собственный worker; зависший native-код не остаётся в окне.
public sealed class ProcessAudioTranscriber(string workerPath, string modelPath, TimeSpan? deadline = null,
    string? vadModelPath = null, bool experimentalBeamSearch = false,
    bool experimentalCuda = false, bool originalPcmExpiry = false, bool workerObservations = false,
    string? cudaDependencyDirectory = null, Action<SttTransportEvent>? observe = null, bool candidateInput = false) : IExpiringAudioTranscriber, ISttTimingSource, IAsyncDisposable
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private Process? worker;
    private Task? stderr;
    private SttJobDiagnostics? workerDiagnostics;
    private Guid workerId;
    private long workerOrdinal;
    private bool gpuFailed;
    private readonly object metadataGate = new();
    private readonly Dictionary<long, Guid> workerJobs = [];
    private void Notify(string phase, Guid job = default, long ordinal = 0, double? budget = null,
        long? expiry = null, SttWorkerObservation? child = null, GpuResultEvidence? gpu = null, int? exit = null) =>
        observe?.Invoke(new(phase, workerId, job, ordinal, Stopwatch.GetTimestamp(), Stopwatch.Frequency, budget, expiry, child, gpu, exit));
    private SttObservationReceiver? observations;
    private SttObservationStatus? lastObservationStatus;
    public SttObservationStatus? ObservationStatus => observations?.Status ?? lastObservationStatus;
    private bool disposed;
    private long completedBlocks, nativeRecognitionBlocks, vadSkippedBlocks;
    public bool LastNativeRecognitionUsed { get; private set; }
    public bool LastVadSkipped { get; private set; }
    public bool LastGpuExecutionObserved { get; private set; }
    public object Metrics => new { completedBlocks = Interlocked.Read(ref completedBlocks),
        nativeRecognitionBlocks = Interlocked.Read(ref nativeRecognitionBlocks),
        vadSkippedBlocks = Interlocked.Read(ref vadSkippedBlocks), vadEnabled = vadModelPath is not null,
        cudaRequired = experimentalCuda, lastGpuExecutionObserved = LastGpuExecutionObserved, workerObservations = ObservationStatus };
    public IReadOnlyList<SttRawTiming> LastTimings { get; private set; } = [];
    public bool IsWorkerRunning => worker is { HasExited: false };
    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken) =>
        TranscribeAsync(samples, cancellationToken, null);
    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken, SttJobDiagnostics? diagnostics) =>
        TranscribeCoreAsync(samples, cancellationToken, diagnostics, null);
    public Task<string> TranscribeExpiringAsync(float[] samples, SttJobDescriptor descriptor, IClock clock,
        CancellationToken cancellationToken, SttJobDiagnostics? diagnostics)
    {
        if (!originalPcmExpiry || (descriptor.CandidateInput == true) != candidateInput
            || samples.Length != descriptor.Samples || !descriptor.Valid(descriptor.Generation, long.MaxValue, clock.Now))
            throw new SttCoverageException("Transport: исходный PCM expiry не подтверждён.");
        long observed = QpcNow();
        long expiry = checked(observed + (descriptor.Expires - clock.Now).Ticks);
        return TranscribeCoreAsync(samples, cancellationToken, diagnostics, expiry, descriptor.JobId);
    }
    private static long QpcNow() => (long)Math.Ceiling(Stopwatch.GetTimestamp() * (10_000_000m / Stopwatch.Frequency));
    private async Task<string> TranscribeCoreAsync(float[] samples, CancellationToken cancellationToken,
        SttJobDiagnostics? diagnostics, long? pcmExpiry, Guid jobId = default)
    {
        if (samples.Length < 1 || samples.Length > (candidateInput ? SttInputLimits.MaxSamples : SttInputLimits.LegacyMaxSamples)) throw new ArgumentOutOfRangeException(nameof(samples));
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan budget = deadline ?? TimeSpan.FromSeconds(SttInputLimits.InferenceBudgetSeconds);
        if (pcmExpiry is { } expiry) budget = TimeSpan.FromTicks(Math.Min(budget.Ticks, Math.Max(1, expiry - QpcNow())));
        timeout.CancelAfter(budget);
        using var cancellationObservation = diagnostics is null ? default : timeout.Token.Register(() =>
            diagnostics.Cancel(pcmExpiry is { } end && QpcNow() >= end ? SttCancelCause.PcmExpired : cancellationToken.IsCancellationRequested ? SttCancelCause.External : SttCancelCause.Deadline));
        byte[] bytes = new byte[samples.Length * sizeof(float)];
        diagnostics?.Configure(Path.GetFileName(modelPath).ToLowerInvariant() switch
        {
            "ggml-large-v3.bin" => SttModelSelection.LargeV3,
            "ggml-base.bin" or "ggml-base-q5_1.bin" => SttModelSelection.Base,
            _ => SttModelSelection.Unknown
        });
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (experimentalCuda && gpuFailed) throw new IOException("GpuTransportFailedClosed");
            if (worker is null) { workerId = Guid.NewGuid(); workerOrdinal = 0; lock (metadataGate) workerJobs.Clear(); }
            long ordinal = ++workerOrdinal;
            lock (metadataGate) { workerJobs[ordinal] = jobId; if (workerJobs.Count > 8) workerJobs.Remove(workerJobs.Keys.Min()); }
            Notify("CallStarted", jobId, ordinal, budget.TotalMilliseconds, pcmExpiry);
            timeout.Token.ThrowIfCancellationRequested();
            if (worker is null)
            {
                var start = new ProcessStartInfo(Path.GetFullPath(workerPath))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(Path.GetFullPath(modelPath));
                if (vadModelPath is not null) start.ArgumentList.Add(Path.GetFullPath(vadModelPath));
                if (experimentalBeamSearch)
                {
                    if (vadModelPath is null) throw new ArgumentException("Файловый beam5-эксперимент требует явного VAD.");
                    start.ArgumentList.Add("--experimental-beam5");
                }
                if (candidateInput) start.ArgumentList.Add(SttInputLimits.WorkerFlag);
                if (originalPcmExpiry) start.ArgumentList.Add("--pcm-expiry-v1");
                if (experimentalCuda) start.ArgumentList.Add("--experimental-cuda");
                start.Environment.Remove(GpuEvidenceTracker.EnvironmentKey);
                if (experimentalCuda)
                {
                    start.Environment[GpuEvidenceTracker.EnvironmentKey] = workerId.ToString();
                    if (cudaDependencyDirectory is not null)
                    {
                        string dependencies = Path.GetFullPath(cudaDependencyDirectory);
                        if (!Directory.Exists(dependencies)) throw new DirectoryNotFoundException("MissingChildCudaDependencies");
                        start.Environment["PATH"] = dependencies + ";" + start.Environment["PATH"];
                    }
                }
                start.Environment.Remove(SttObservationEmitter.EnvironmentKey);
                workerDiagnostics = diagnostics;
                diagnostics?.AttachWorker(workerId);
                if (workerObservations || observe is not null)
                {
                    try
                    {
                        observations = new(diagnostics, observe is null ? null : child =>
                        {
                            Guid id; lock (metadataGate) id = workerJobs.GetValueOrDefault(child.Ordinal);
                            Notify("Child", id, child.Ordinal, child: child);
                        });
                        start.Environment[SttObservationEmitter.EnvironmentKey] = observations.Name;
                    }
                    catch (Exception failure) { lastObservationStatus = new(true, false, 0, 0, false, failure.GetType().Name); if (observe is not null) throw; }
                }
                worker = Process.Start(start) ?? throw new IOException("Не удалось запустить локальный STT worker.");
                diagnostics?.AttachWorker(workerId); diagnostics?.Stage(SttStage.WorkerStarted);
                Notify("WorkerStarted", jobId, ordinal);
                stderr = DrainErrorsAsync(worker.StandardError);
                string? ready = await worker.StandardOutput.ReadLineAsync(timeout.Token);
                if (experimentalCuda) GpuWorkerContract.ValidateReady(ready, workerId);
                else if (ready != "{\"ready\":true}") throw new IOException("Локальный STT worker не подтвердил загрузку модели.");
                if (candidateInput && await worker.StandardOutput.ReadLineAsync(timeout.Token) != SttWorkerSession.CandidateReady)
                    throw new IOException("STT worker did not confirm the candidate input protocol.");
                diagnostics?.Stage(SttStage.WorkerReady);
                Notify("WorkerReady", jobId, ordinal);
            }
            else
            {
                workerDiagnostics = diagnostics;
                diagnostics?.AttachWorker(workerId); diagnostics?.Stage(SttStage.WorkerReused);
                Notify("WorkerReused", jobId, ordinal);
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (originalPcmExpiry && pcmExpiry is null) throw new SttCoverageException("Нужен исходный expiry PCM.");
            observations?.Register(diagnostics);
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            await worker.StandardInput.BaseStream.WriteAsync(BitConverter.GetBytes(samples.Length), timeout.Token);
            if (pcmExpiry is { } absoluteExpiry)
                await worker.StandardInput.BaseStream.WriteAsync(BitConverter.GetBytes(absoluteExpiry), timeout.Token);
            await worker.StandardInput.BaseStream.WriteAsync(bytes, timeout.Token);
            await worker.StandardInput.BaseStream.FlushAsync(timeout.Token);
            diagnostics?.Stage(SttStage.InputSent);
            Notify("InputSent", jobId, ordinal);
            string line = await worker.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Локальный STT worker завершился без результата.");
            diagnostics?.Stage(SttStage.ResponseReceived);
            Notify("ResponseReceived", jobId, ordinal);
            timeout.Token.ThrowIfCancellationRequested();
            if (line.Length > 65536) throw new IOException("Слишком большой результат STT.");
            using var json = JsonDocument.Parse(line);
            string text = json.RootElement.GetProperty("text").GetString() ?? "";
            LastTimings = candidateInput && json.RootElement.TryGetProperty("segments", out var timings)
                ? SttTiming.Read(timings, samples.Length) : [];
            LastNativeRecognitionUsed = !json.RootElement.TryGetProperty("nativeRecognitionUsed", out var used) || used.GetBoolean();
            LastVadSkipped = json.RootElement.TryGetProperty("vadSkipped", out var skipped) && skipped.GetBoolean();
            LastGpuExecutionObserved = json.RootElement.TryGetProperty("gpuExecutionObserved", out var gpu) && gpu.GetBoolean();
            if (experimentalCuda && LastNativeRecognitionUsed && !LastGpuExecutionObserved)
                throw new IOException("STT worker не подтвердил использование CUDA при распознавании.");
            GpuResultEvidence? gpuEvidence = null;
            if (experimentalCuda)
            {
                gpuEvidence = json.RootElement.GetProperty("gpuEvidence").Deserialize<GpuResultEvidence>();
                if (!GpuWorkerContract.Valid(gpuEvidence, workerId, ordinal, LastNativeRecognitionUsed)
                    || LastGpuExecutionObserved != LastNativeRecognitionUsed)
                    throw new IOException("InvalidPerCallCudaEvidence");
            }
            if (observe is not null) await observations!.WaitForCallAsync(ordinal, timeout.Token);
            Notify("ResponseValidated", jobId, ordinal, gpu: gpuEvidence);
            diagnostics?.ObserveBackend(LastGpuExecutionObserved && experimentalCuda);
            Interlocked.Increment(ref completedBlocks);
            if (LastNativeRecognitionUsed)
                Interlocked.Increment(ref nativeRecognitionBlocks);
            if (LastVadSkipped)
                Interlocked.Increment(ref vadSkippedBlocks);
            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            gpuFailed |= experimentalCuda;
            diagnostics?.Cancel(pcmExpiry is { } expires && QpcNow() >= expires ? SttCancelCause.PcmExpired : SttCancelCause.Deadline);
            await KillWorkerAsync(); throw new TimeoutException("Локальное распознавание превысило лимит времени; STT worker завершён.");
        }
        catch (OperationCanceledException)
        {
            gpuFailed |= experimentalCuda;
            diagnostics?.Cancel(SttCancelCause.External);
            await KillWorkerAsync(); throw;
        }
        catch { gpuFailed |= experimentalCuda; await KillWorkerAsync(); throw; }
        finally { Array.Clear(bytes); try { Notify("TransportBufferCleared", jobId, workerOrdinal); } finally { serial.Release(); } }
    }
    // Диагностический native-поток опустошается ограниченным буфером, без накопления за долгую сессию.
    private static async Task DrainErrorsAsync(StreamReader reader)
    {
        char[] buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) Array.Clear(buffer); }
        finally { Array.Clear(buffer); }
    }
    private async Task KillWorkerAsync(SttCancelCause? observedCause = null, bool graceful = false)
    {
        var process = worker; worker = null;
        if (process is null) { await CloseObservations(); return; }
        try
        {
            if (!process.HasExited)
            {
                if (graceful) process.StandardInput.Close();
                else { process.Kill(entireProcessTree: true); Notify("ForcedTermination"); }
            }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { if (!process.HasExited) process.Kill(entireProcessTree: true); Notify("ForcedTermination"); throw; }
            workerDiagnostics?.Stage(SttStage.WorkerExited, process.ExitCode, observedCause);
            Notify("WorkerExited", exit: process.ExitCode);
            if (stderr is { } errors) await errors.WaitAsync(TimeSpan.FromSeconds(3));
            Notify("StderrDrained");
            if (graceful && process.ExitCode != 0) throw new IOException("GpuWorkerNonzeroExit");
        }
        finally { process.Dispose(); stderr = null; await CloseObservations(); workerDiagnostics = null; }
    }
    private async Task CloseObservations()
    {
        if (observations is not { } value) return;
        // Неполнота после Kill не считается native hang или отсутствием child-события.
        if (observe is not null || experimentalCuda) await value.CompleteAsync();
        lastObservationStatus = value.Status; value.Dispose(); observations = null;
        workerDiagnostics?.WorkerObservationSummary(lastObservationStatus.Connected, lastObservationStatus.Received,
            lastObservationStatus.Dropped, lastObservationStatus.OutputClosed, lastObservationStatus.Error);
    }
    public async ValueTask DisposeAsync()
    {
        await serial.WaitAsync();
        try { if (!disposed) { disposed = true;
            workerDiagnostics?.Stage(SttStage.CancelRequested, observedCause: SttCancelCause.Dispose);
            await KillWorkerAsync(SttCancelCause.Dispose, graceful: experimentalCuda && !gpuFailed); } }
        finally { serial.Release(); }
    }
}
