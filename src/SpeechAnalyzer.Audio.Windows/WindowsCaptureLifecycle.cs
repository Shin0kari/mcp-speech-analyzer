using System.Diagnostics;
using SpeechAnalyzer.Core;

namespace SpeechAnalyzer.Audio.Windows;

// Выбор устройств фиксируется до старта; смена выполняется только в остановленной сессии.
public sealed class WindowsCaptureLifecycle : ICaptureLifecycle
{
    private readonly AudioCaptureSession capture;
    private readonly AudioDiagnosticJournal? diagnostics;
    private readonly bool ownsDiagnostics;
    public AudioDiagnosticStatus? DiagnosticStatus=>diagnostics?.Status;
    private readonly AudioTimelineAdapter timeline;
    private readonly AudioSourceSelection? selection;
    private readonly string? diagnosticMicrophone;
    private readonly int diagnosticProcess;
    private ApplicationWindow? target;
    private readonly TestSessionAudioRecorder? recording;
    private bool transcriptionFailed;
    public event Action<string>? AnalysisFailed;
    public AudioRecordingStatus? RecordingStatus => recording?.Status;
    public event Action<string>? Failed;
    public float YouPeak => capture.YouPeak;
    public float OtherPeak => capture.OtherPeak;
    public WindowsCaptureLifecycle(AudioTimelineAdapter timeline, AudioSourceSelection selection, TestSessionAudioRecorder? recording = null, AudioDiagnosticJournal? diagnostics=null, bool ownsDiagnostics=true)
    {
        this.timeline = timeline; this.selection = selection; this.recording = recording;
        this.diagnostics=diagnostics;this.ownsDiagnostics=ownsDiagnostics;capture=new(diagnostics);
        capture.Packet += packet =>
        {
            // Сначала отдельный retained writer; STT не владеет его буфером и жизненным циклом.
            using (CaptureTiming.Phase(CaptureTimingKind.RecorderSubmit))
                recording?.Submit(packet.Channel, packet.QpcStart100ns, packet.Samples, packet.Discontinuity, packet.TimestampError,
                    packet.DevicePosition, packet.Silent, packet.ArrivalQpc100ns);
            if(transcriptionFailed)return;
            try { using var timing = CaptureTiming.Phase(CaptureTimingKind.TimelinePush);
                timeline.Push(packet.Channel, packet.QpcStart100ns, packet.DevicePosition,
                packet.Samples, packet.Discontinuity, packet.TimestampError, packet.Silent, PcmEncoding.SignedPcm16Div32768); }
            catch(Exception e){ReportAnalysisFailure(e.Message);}
        };
        capture.Failed += error => Failed?.Invoke(error);
    }
    // Прежний явный PID доступен только отдельному диагностическому инструменту AudioChecks.
    public WindowsCaptureLifecycle(AudioTimelineAdapter timeline, string microphoneId, int processId) : this(timeline, null!)
    {
        diagnosticMicrophone = microphoneId; diagnosticProcess = processId;
    }
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (selection is null)
        {
            timeline.Begin((long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency)));
            await Task.Run(() => capture.StartAsync(diagnosticMicrophone!, diagnosticProcess, cancellationToken), cancellationToken).ConfigureAwait(false);
            return;
        }
        var resolved = selection.RequireTarget();
        target = resolved.Window;
        long origin=(long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency));
        recording?.Begin(origin);
        SessionEventLog.Current?.Write("capture_sources",new{resolved.MicrophoneDeviceId,resolved.Window,captureScope="process_tree"});
        if(!transcriptionFailed)try{timeline.Begin(origin);}catch(Exception e){ReportAnalysisFailure(e.Message);}
        await Task.Run(async () =>
        {
            // Повторная проверка после перехода на рабочий поток, до активации loopback.
            if (!selection.IsCurrent(resolved.Window)) throw new IOException("Выбранное окно изменилось перед захватом.");
            await capture.StartAsync(resolved.MicrophoneDeviceId, resolved.Window.Identity.ProcessId, cancellationToken);
            if (!selection.IsCurrent(resolved.Window))
            {
                await capture.StopAsync();
                throw new IOException("Выбранное окно исчезло во время запуска захвата.");
            }
        }, cancellationToken).ConfigureAwait(false);
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await capture.StopAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        recording?.End((long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency)));
        if(recording is not null)
            try { await recording.FlushAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (IOException) when (recording.Status.Error is not null)
            {
                // Capture has stopped; a recorder failure must not prevent the
                // independent timeline/healthy STT from completing its tails.
                SessionEventLog.Current?.Write("recorder_stop_incomplete", recording.Status.Failure);
            }
        await capture.SaveMeasurementAsync().ConfigureAwait(false);
        if(!transcriptionFailed)try{timeline.Complete();}catch(Exception e){ReportAnalysisFailure(e.Message);}
        capture.WriteDiagnosticSummary("stop");
        if(diagnostics is not null)await diagnostics.FlushAsync().ConfigureAwait(false);
    }
    public void Pulse()
    {
        if (target is not null && capture.IsRunning && selection is not null && !selection.IsCurrent(target))
            Failed?.Invoke("Выбранное окно закрыто или изменилось. Остановите захват и явно возобновите после выбора источника.");
        capture.CheckProcess();
        if(!transcriptionFailed){timeline.Pulse();if(timeline.Snapshot().Error is {} error)ReportAnalysisFailure(error);}
    }
    private void ReportAnalysisFailure(string error)
    {
        if(transcriptionFailed)return;transcriptionFailed=true;
        capture.WriteDiagnosticSummary("analysis_failure");
        if(recording is null){Failed?.Invoke(error);return;}
        SessionEventLog.Current?.Write("transcription_disabled_recording_continues",new{error});AnalysisFailed?.Invoke(error);
    }
    public async ValueTask DisposeAsync()
    {
        try{await capture.DisposeAsync();}
        finally{try{
            if(recording is not null)
                try { await recording.DisposeAsync(); }
                catch (IOException) when (recording.Status.Error is not null) { SessionEventLog.Current?.Write("recorder_dispose_incomplete", recording.Status.Failure); }
            await capture.SaveMeasurementAsync();
        }finally{if(ownsDiagnostics && diagnostics is not null)await diagnostics.DisposeAsync();}}
    }
}
