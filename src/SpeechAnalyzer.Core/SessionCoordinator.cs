namespace SpeechAnalyzer.Core;

public enum SessionPhase { Stopped, Starting, Capturing, Pausing, Paused, Stopping, Faulted, Closing, Closed }

// StopAsync подтверждает прекращение входящих пакетов, включая допустимый хвост.
// Неуспешный StartAsync также требует StopAsync для отката частичного старта.
public interface ICaptureLifecycle : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class SimulatedCaptureLifecycle : ICaptureLifecycle
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Единственный вход для команд окна и MCP. Долгий анализ остаётся в очереди Engine.
public sealed class SessionCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly ConversationEngine engine;
    private readonly ITranscriptSource source;
    private readonly ICaptureLifecycle capture;
    private readonly TimeSpan timeout;
    private readonly TimeSpan transcriptionTimeout;
    private readonly bool independentRecording;
    private void Analyze(Action action)
    {
        try { action(); }
        catch(Exception error) when(independentRecording)
        { Error ??= SessionStatusPresentation.AnalysisFailure(error.Message, !RecordingStopped); SessionEventLog.Current?.Write("analysis_error",new{error=error.Message}); }
    }
    private readonly ICaptureCheckpointScheduler checkpointScheduler;
    private IDisposable? checkpointSchedule;
    private int checkpointPending;
    private volatile bool closing;
    private Task? shutdown;
    private Task? stopOperation;
    public SessionPhase Phase { get; private set; } = SessionPhase.Stopped;
    public string? Error { get; private set; }
    public bool RecordingStopped { get; private set; } = true;
    public TranscriptStopStatus? TranscriptionCompletion => (source as BufferedTranscriptSource)?.StopStatus;
    public CaptureDuration Duration { get; }
    public event Action? Changed;
    public SessionCoordinator(ConversationEngine engine, ITranscriptSource source, ICaptureLifecycle? capture = null, TimeSpan? timeout = null, CaptureDuration? duration = null, ICaptureCheckpointScheduler? checkpointScheduler = null, TimeSpan? transcriptionTimeout = null, bool independentRecording = false)
    {
        this.engine = engine; this.source = source; this.independentRecording=independentRecording;
        this.capture = capture ?? new SimulatedCaptureLifecycle();
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        this.transcriptionTimeout = transcriptionTimeout ?? (source is BufferedTranscriptSource buffered
            ? buffered.MaximumAudioAge : this.timeout);
        Duration = duration ?? new(new SystemClock());
        this.checkpointScheduler = checkpointScheduler ?? new CaptureCheckpointScheduler();
        engine.Changed += Notify;
    }
    public EngineSnapshot Snapshot() => engine.Snapshot();
    public IReadOnlyList<TranscriptSegment> Segments => source.Segments;
    private void Notify() => Changed?.Invoke();
    private async Task CommandAsync(Func<Task> action, bool reportError = true)
    {
        ObjectDisposedException.ThrowIf(closing, this);
        await commands.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(closing, this);
            await action().ConfigureAwait(false);
        }
        catch (Exception error) { if (reportError) Error ??= error.Message; throw; }
        finally { commands.Release(); Notify(); }
    }
    private async Task Bounded(Func<CancellationToken, Task> action, TimeSpan? limit = null)
    {
        TimeSpan budget = limit ?? timeout;
        using var deadline = new CancellationTokenSource(budget);
        await action(deadline.Token).WaitAsync(budget).ConfigureAwait(false);
    }
    public Task StartAsync() => CommandAsync(async () =>
    {
        if (Phase is SessionPhase.Capturing or SessionPhase.Paused) return;
        if (Phase != SessionPhase.Stopped) throw new InvalidOperationException("Перед стартом устраните ошибку и остановите источники.");
        Phase = SessionPhase.Starting; Error = null; stopOperation = null;
        try
        {
            Duration.PrepareStart(); Analyze(engine.Start);
            RecordingStopped = false;
            await Bounded(capture.StartAsync); Duration.Start(); Phase = SessionPhase.Capturing;
            SessionEventLog.Current?.Write("session_start",new{phase=Phase.ToString()});
            StartCheckpoints();
        }
        catch
        {
            StopCheckpoints();
            Phase = SessionPhase.Faulted;
            // Буфер остаётся открытым до подтверждения прекращения пакетов.
            try { await Bounded(capture.StopAsync); RecordingStopped = true; Duration.Stop(); engine.Stop(); }
            catch (Exception rollback) { Error = "Не подтверждена остановка частично запущенных источников: " + rollback.Message; }
            throw;
        }
    });
    public Task PauseAsync() => CommandAsync(async () =>
    {
        if (Phase == SessionPhase.Paused) return;
        if (Phase != SessionPhase.Capturing) return;
        Phase = SessionPhase.Pausing;
        StopCheckpoints();
        try { await Bounded(capture.StopAsync); RecordingStopped = true; Duration.Stop(); Analyze(engine.TogglePause); Phase = SessionPhase.Paused; SessionEventLog.Current?.Write("session_pause"); }
        catch { Phase = SessionPhase.Faulted; Duration.Stop(uncertain: true); throw; }
    });
    public Task ResumeAsync() => CommandAsync(async () =>
    {
        if (Phase != SessionPhase.Paused) return;
        Phase = SessionPhase.Starting;
        try
        {
            Duration.PrepareStart(); Analyze(engine.TogglePause);
            RecordingStopped = false;
            await Bounded(capture.StartAsync); Duration.Start(); Phase = SessionPhase.Capturing;
            SessionEventLog.Current?.Write("session_resume",new{phase=Phase.ToString()});
            StartCheckpoints();
        }
        catch
        {
            StopCheckpoints();
            Phase = SessionPhase.Faulted;
            try { await Bounded(capture.StopAsync); RecordingStopped = true; Duration.Stop(); engine.Stop(); } catch { }
            throw;
        }
    });
    private async Task StopCoreAsync()
    {
        StopCheckpoints();
        if (Phase is SessionPhase.Stopped or SessionPhase.Closed) return;
        Phase = SessionPhase.Stopping;
        bool captureStopped = false;
        try
        {
            await Bounded(capture.StopAsync);
            captureStopped = true;
            RecordingStopped = true;
            Duration.Stop();
            engine.Stop();
            Notify();
            long boundary = source.CaptureBoundaryMs;
            if (source is BufferedTranscriptSource buffered)
            {
                await Bounded(buffered.WaitForStopCompletionAsync, transcriptionTimeout);
                if (buffered.StopStatus.State == TranscriptStopState.StoppedWithGaps)
                    Duration.MarkTranscriptIncomplete();
                if (buffered.StopStatus.State == TranscriptStopState.Incomplete)
                {
                    Duration.MarkTranscriptIncomplete();
                    Error ??= buffered.StopStatus.PrimaryIncident;
                }
            }
            else await Bounded(async cancellation =>
            {
                var finalization = source.WaitForFinalizationAsync(boundary, cancellation);
                while (!finalization.IsCompleted)
                {
                    source.Advance();
                    await Task.WhenAny(finalization, Task.Delay(20, cancellation)).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                }
                await finalization.ConfigureAwait(false);
            }, transcriptionTimeout);
            Duration.CompleteTranscription(source.FinalizedThroughMs);
            Phase = SessionPhase.Stopped;
            SessionEventLog.Current?.Write("session_stop",new{boundaryMs=boundary,finalizedThroughMs=source.FinalizedThroughMs});
        }
        catch (Exception failure)
        {
            (source as BufferedTranscriptSource)?.Abort(failure is TimeoutException or OperationCanceledException
                ? TranscriptStopReason.Deadline : TranscriptStopReason.CleanupFailure, "Завершение Stop: " + failure.Message);
            Error ??= (source as BufferedTranscriptSource)?.Error ?? failure.Message;
            Phase = SessionPhase.Faulted;
            Duration.Stop(uncertain: !captureStopped);
            Duration.MarkTranscriptIncomplete();
            throw;
        }
    }
    public Task StopAsync()
    {
        lock (commands) return stopOperation ??= CommandAsync(StopCoreAsync);
    }
    public Task SelectAudioSourcesAsync(AudioSourceSelection selection, string? microphoneId, string? windowId, WindowMatchPolicy policy, long? catalogVersion = null, AudioSelectionChange change = AudioSelectionChange.Both)
        => CommandAsync(() =>
        {
            selection.ValidateRequest(microphoneId, windowId, catalogVersion, change);
            if (Phase != SessionPhase.Stopped) throw new InvalidOperationException("Сначала остановите захват, затем меняйте источники.");
            selection.Select(microphoneId, windowId, policy, catalogVersion, change);
            return Task.CompletedTask;
        }, reportError: false);
    public Task ConfigureAsync(string context, string fallback, Func<Task>? save = null) => CommandAsync(async () =>
    {
        if (save is not null) await save().ConfigureAwait(false);
        engine.Configure(context, fallback);
    });
    public Task SetModeAsync(AnalysisMode mode) => CommandAsync(() => { engine.SetMode(mode); return Task.CompletedTask; });
    public Task RetryAsync() => CommandAsync(() => { engine.Retry(); return Task.CompletedTask; });
    public Task AbandonFailedRequestAsync() => CommandAsync(() => { engine.AbandonFailedRequest(); return Task.CompletedTask; }, reportError: false);
    public async Task<bool> AnswerAsync(string instruction = "")
    {
        if (Phase == SessionPhase.Stopping) return false;
        if (engine.RollingContextSeconds is not null)
        {
            if (closing || Phase is SessionPhase.Starting or SessionPhase.Pausing or SessionPhase.Closed) return false;
            // Model acceptance has its own serialized gate. A capture checkpoint must not move the click cutoff.
            return engine.Answer(instruction);
        }
        bool accepted = false;
        await CommandAsync(() => { accepted = engine.Answer(instruction); return Task.CompletedTask; });
        return accepted;
    }
    public void Pulse()
    {
        // Повторные тики не накапливаются за длинной командой stop/start.
        if (closing || !commands.Wait(0)) return;
        try { Analyze(engine.Pulse); }
        finally { commands.Release(); }
    }
    private void StartCheckpoints() => checkpointSchedule = checkpointScheduler.Start(CheckpointAsync);
    private void StopCheckpoints()
    {
        checkpointSchedule?.Dispose();
        checkpointSchedule = null;
    }
    private async Task CheckpointAsync()
    {
        // Не более одного запроса, включая ожидающий общей очереди. Тики не образуют backlog.
        if (closing || Interlocked.CompareExchange(ref checkpointPending, 1, 0) != 0) return;
        await commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (closing || Phase != SessionPhase.Capturing) return;
            try { Duration.Checkpoint(); }
            catch (Exception error)
            {
                try { await FailureCoreAsync("Не сохранена длительность захвата: " + error.Message).ConfigureAwait(false); }
                catch (Exception stopError) { Error += "\nОшибка остановки: " + stopError.Message; }
            }
        }
        finally { commands.Release(); Interlocked.Exchange(ref checkpointPending, 0); }
    }
    public Task ReportFailureAsync(string error) => CommandAsync(() => FailureCoreAsync(error));
    private async Task FailureCoreAsync(string error)
    {
        StopCheckpoints();
        Error ??= error;
        (source as BufferedTranscriptSource)?.ReportFailure(error);
        try { Duration.Stop(uncertain: true); }
        finally
        {
            // Отказ диска не должен мешать физически остановить источники.
            try { await StopCoreAsync(); }
            finally { Phase = SessionPhase.Faulted; }
        }
    }
    public ValueTask DisposeAsync()
    {
        if (source is BufferedTranscriptSource { StopStatus.State: TranscriptStopState.Finishing } finishing) finishing.Abort();
        lock (commands) { closing = true; return new(shutdown ??= CloseAsync()); }
    }
    private async Task CloseAsync()
    {
        // Ожидание команды, хвоста STT и каждого освобождения ограничено отдельно.
        bool acquired = false;
        try
        {
            // Остановка может ждать источники и ограниченный хвост STT последовательно.
            acquired = await commands.WaitAsync(timeout + transcriptionTimeout).ConfigureAwait(false);
            if (!acquired) throw new TimeoutException("Команда сессии не завершилась при закрытии.");
            // Отказ отдельной команды (например, устаревший source_id) не является ошибкой закрытия.
            await StopCoreAsync().ConfigureAwait(false);
        }
        catch (Exception error) { Error = "Не весь остаток подтверждён при закрытии: " + error.Message; }
        finally
        {
            StopCheckpoints();
            Phase = SessionPhase.Closing;
            engine.Changed -= Notify;
            await ReleaseAsync(() => engine.DisposeAsync().AsTask());
            if (source is IAsyncDisposable disposable) await ReleaseAsync(() => disposable.DisposeAsync().AsTask());
            await ReleaseAsync(() => capture.DisposeAsync().AsTask());
            Phase = SessionPhase.Closed;
            if (acquired) commands.Release();
            Notify();
        }
    }
    private async Task ReleaseAsync(Func<Task> release)
    {
        try { await release().WaitAsync(timeout).ConfigureAwait(false); }
        catch (Exception error) { Error = (Error is null ? "" : Error + "\n") + "Завершение компонента: " + error.Message; }
    }
}
