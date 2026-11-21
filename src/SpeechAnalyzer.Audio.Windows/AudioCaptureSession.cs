using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SpeechAnalyzer.Core;

namespace SpeechAnalyzer.Audio.Windows;

public sealed record MicrophoneChoice(string Id, string Name);
public sealed record ProcessChoice(int Id, string Name, bool HasWindow = false)
{
    public string Label => $"{Name} · PID {Id}";
}
public sealed record AudioPacket(AudioChannel Channel, long QpcStart100ns, float[] Samples, bool Discontinuity,
    long DevicePosition = 0, bool TimestampError = false, bool Silent = false, long? ArrivalQpc100ns = null);

// Сбор источников и создание объектов не запускают запись.
public sealed class AudioCaptureSession : IAsyncDisposable
{
    private readonly AudioDiagnosticJournal? diagnostics;
    private readonly CaptureCallbackObservations callbackObservations;
    private CaptureTimingRun? measurement = null;
    private bool measurementJoined, measurementExported;
    public string? MeasurementError { get; private set; }
    public AudioCaptureSession(AudioDiagnosticJournal? diagnostics=null){this.diagnostics=diagnostics;callbackObservations=new(diagnostics);}
    public void WriteDiagnosticSummary(string phase)=>callbackObservations.WriteSummary(phase);
    private readonly MMDeviceEnumerator enumerator = new();
    private MMDevice? microphoneDevice;
    private WasapiRecorder? microphone, processAudio;
    private int processId;
    private volatile bool stopping;
    private volatile bool accepting;
    public event Action<AudioPacket>? Packet;
    public event Action<string>? Failed;
    public float YouPeak { get; private set; }
    public float OtherPeak { get; private set; }
    public bool IsRunning { get; private set; }
    public static IReadOnlyList<MicrophoneChoice> Microphones()
    {
        using var devices = new MMDeviceEnumerator();
        return devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => { using (device) return new MicrophoneChoice(device.ID, device.FriendlyName); }).ToArray();
    }
    public static IReadOnlyList<ProcessChoice> Processes() => Process.GetProcesses().Select(process =>
    {
        using (process) { try { return new ProcessChoice(process.Id, process.ProcessName, process.MainWindowHandle != IntPtr.Zero); } catch { return null; } }
    }).OfType<ProcessChoice>().OrderBy(p => p.Name).ThenBy(p => p.Id).ToArray();

    public async Task StartAsync(string microphoneId, int targetProcessId, CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("Захват уже запущен.");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) == false)
            throw new PlatformNotSupportedException("Process loopback требует Windows build 20348 или новее.");
        stopping = false; processId = targetProcessId;
#if CAPTURE_MEASUREMENT
        measurement = Environment.GetEnvironmentVariable("SPEECH_ANALYZER_CAPTURE_MEASUREMENT") == "1" ? new() : null;
        measurementJoined = measurementExported = false;
        MeasurementError = null;
#endif
        callbackObservations.Reset();
        try
        {
            using var target = Process.GetProcessById(targetProcessId);
            if (target.HasExited) throw new IOException("Выбранный процесс уже завершился.");
            microphoneDevice = enumerator.GetDevice(microphoneId);
            microphone = new WasapiRecorderBuilder().WithDevice(microphoneDevice).WithBufferLength(50)
                .WithFormat(new WaveFormat(16000, 16, 1)).Build();
            processAudio = await new WasapiRecorderBuilder().WithProcessLoopback((uint)targetProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithBufferLength(50).WithFormat(new WaveFormat(16000, 16, 1)).BuildAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
#if CAPTURE_MEASUREMENT
            microphone.TimingLane = measurement?.You;
            processAudio.TimingLane = measurement?.Other;
#endif
            CaptureAudioFormat Format(WaveFormat format)=>new(format.SampleRate,format.Channels,format.BitsPerSample,format.BlockAlign,format.Encoding.ToString());
            diagnostics?.TryWrite("capture_formats",new CaptureFormatMetadata(Format(new WaveFormat(16000,16,1)),Format(microphone.WaveFormat),Format(processAudio.WaveFormat)));
            microphone.DataAvailable += (buffer, flags, device, qpc) => Receive(AudioChannel.You, buffer, flags, device, qpc);
            processAudio.DataAvailable += (buffer, flags, device, qpc) => Receive(AudioChannel.Other, buffer, flags, device, qpc);
            microphone.RecordingStopped += Stopped; processAudio.RecordingStopped += Stopped;
            accepting = true;
            microphone.StartRecording(); processAudio.StartRecording(); IsRunning = true;
        }
        catch { await StopAsync(); throw; }
    }
    private void Receive(AudioChannel channel, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long device, long qpc)
    {
        using var timing = CaptureTiming.Phase(CaptureTimingKind.Receive);
        if (!accepting) return;
        long arrivalQpc100ns=(long)(Stopwatch.GetTimestamp()*(10_000_000d/Stopwatch.Frequency));
        if(buffer.IsEmpty)
        {
            callbackObservations.Observe(new(channel,qpc,device,arrivalQpc100ns,(int)flags));
        }
        float[] samples = new float[buffer.Length / 2]; float peak = 0;
        if ((flags & AudioClientBufferFlags.Silent) == 0)
        {
            var pcm = MemoryMarshal.Cast<byte, short>(buffer);
            for (int i = 0; i < samples.Length; i++) { samples[i] = pcm[i] / 32768f; peak = Math.Max(peak, Math.Abs(samples[i])); }
        }
        if (channel == AudioChannel.You) YouPeak = peak; else OtherPeak = peak;
        try { Packet?.Invoke(new(channel, qpc, samples, (flags & AudioClientBufferFlags.DataDiscontinuity) != 0,
            device, (flags & AudioClientBufferFlags.TimestampError) != 0,
            (flags & AudioClientBufferFlags.Silent) != 0, arrivalQpc100ns)); }
        finally { Array.Clear(samples); } // Получатель копирует только нужный ограниченный буфер.
    }
    private void Stopped(object? sender, StoppedEventArgs e)
    {
        if (!stopping) Failed?.Invoke(e.Exception is { } error ? "Аудиоисточник недоступен: " + error.Message
            : "Аудиоисточник неожиданно остановился. Запись прекращена; проверьте оба источника.");
    }
    public void CheckProcess()
    {
        if (!IsRunning) return;
        try { using var target = Process.GetProcessById(processId); if (target.HasExited) throw new IOException(); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException)
        { Failed?.Invoke("Выбранный процесс завершился. Остановите захват и выберите окно приложения заново."); }
    }
    public async Task StopAsync()
    {
        stopping = true; IsRunning = false;
        // Обоим источникам сначала отправляется stop; допустимые callbacks принимаются до завершения Dispose.
        microphone?.StopRecording(); processAudio?.StopRecording();
        bool microphoneJoined = microphone is null, otherJoined = processAudio is null;
        try
        {
            if (microphone is { } mic) { microphone = null; await mic.DisposeAsync(); microphoneJoined = true; }
        }
        finally
        {
            try { if (processAudio is { } app) { processAudio = null; await app.DisposeAsync(); otherJoined = true; } }
            finally { accepting = false; measurementJoined = microphoneJoined && otherJoined; }
        }
        microphoneDevice?.Dispose(); microphoneDevice = null; YouPeak = OtherPeak = 0;
    }
    public async Task SaveMeasurementAsync()
    {
        if (measurement is null || measurementExported || diagnostics is null) return;
        if (!measurementJoined)
        {
            MeasurementError = "Native capture join is not confirmed; timing snapshot was not read.";
            diagnostics.TryWrite("capture_timing_export", new { error = MeasurementError, afterJoin = false });
            return;
        }
        measurementExported = true;
        string path = Path.Combine(Path.GetDirectoryName(diagnostics.Status.FilePath)!, "capture-timing-" + Guid.NewGuid().ToString("N") + ".json");
        bool flushed = await diagnostics.FlushAsync().ConfigureAwait(false);
        var snapshot = measurement.SnapshotAfterJoin() with
        {
            DiagnosticFlushSucceeded = flushed, DiagnosticStatusAtExport = diagnostics.Status,
            PackageId = Environment.GetEnvironmentVariable("SPEECH_ANALYZER_MEASUREMENT_PACKAGE_ID"),
            PackageManifestSha256 = Environment.GetEnvironmentVariable("SPEECH_ANALYZER_MEASUREMENT_MANIFEST_SHA256")
        };
        MeasurementError = await CaptureTimingRun.SaveAfterJoinAsync(snapshot, path).ConfigureAwait(false);
        diagnostics.TryWrite("capture_timing_export", new { path, error = MeasurementError, afterJoin = true });
    }
    public async ValueTask DisposeAsync() { await StopAsync(); enumerator.Dispose(); }
}
