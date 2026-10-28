using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// Короткий диагностический захват только дерева указанного PID.
// PCM обрабатывается в памяти; аудиофайл и расшифровка не создаются.
internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--pid" || args[2] != "--seconds" ||
            !int.TryParse(args[1], out int pid) || pid <= 0 ||
            !int.TryParse(args[3], out int seconds) || seconds is < 1 or > 30)
        {
            Console.Error.WriteLine("Usage: ProcessAudioProbe --pid <PID> --seconds <1..30>");
            return 2;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            if (Environment.OSVersion.Version.Build < 20348)
                throw new PlatformNotSupportedException("Process loopback requires Windows build 20348 or later.");
            Write(new { status = "activating", pid, process = process.ProcessName, seconds,
                scope = "include-process-tree", retainsAudio = false });
            using var probe = new ProcessLoopback();
            probe.Run(pid, seconds, process);
            return 0;
        }
        catch (Exception error)
        {
            Write(new { status = "error", hresult = $"0x{error.HResult:X8}", error = error.Message });
            return 1;
        }
    }

    internal static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
}

internal sealed class ProcessLoopback : IDisposable
{
    private IAudioClient? client;
    private IAudioCaptureClient? capture;
    private readonly AutoResetEvent sampleReady = new(false);
    private bool started;

    public void Run(int pid, int seconds, Process target)
    {
        var activation = new ActivationParams { ActivationType = 1, TargetProcessId = (uint)pid, ProcessLoopbackMode = 0 };
        IntPtr activationPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        var handler = new ActivationHandler { ActivationPointer = activationPointer };
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            Marshal.StructureToPtr(activation, activationPointer, false);
            var variant = new PropVariant { Type = 65, BlobSize = (uint)Marshal.SizeOf<ActivationParams>(), BlobData = activationPointer };
            Guid iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, ref variant, handler, out operation));
            // Параметры активации остаются живыми до завершения callback.
            if (!handler.Completed.WaitOne(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("ActivateAudioInterfaceAsync did not complete in 10 seconds.");
            Check(handler.Result);
            client = (IAudioClient)(handler.Activated ?? throw new InvalidOperationException("No audio client returned."));
        }
        finally
        {
            // При успешном старте память освобождает callback, в том числе после таймаута.
            if (operation is null) handler.ReleaseActivationParams();
            if (operation is not null) Marshal.ReleaseComObject(operation);
            // После таймаута утилита завершается; event остаётся жив для позднего callback.
            GC.KeepAlive(handler);
        }

        // Фиксированный формат как в официальном Microsoft sample, без GetMixFormat.
        var format = new WaveFormat { FormatTag = 1, Channels = 2, SampleRate = 44100,
            AverageBytesPerSecond = 176400, BlockAlign = 4, BitsPerSample = 16 };
        const uint flags = 0x00020000 | 0x00040000 | 0x80000000;
        Check(client.Initialize(0, flags, 0, 0, ref format, IntPtr.Zero));
        Guid captureId = typeof(IAudioCaptureClient).GUID;
        Check(client.GetService(ref captureId, out object captureObject));
        capture = (IAudioCaptureClient)captureObject;
        Check(client.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle()));
        Check(client.Start());
        started = true;

        long totalFrames = 0;
        long signalFrames = 0;
        int discontinuities = 0;
        double maximumPeak = 0;
        double sumSquares = 0;
        long sampleCount = 0;
        double intervalPeak = 0;
        byte[] buffer = new byte[44100 * 4];
        var clock = Stopwatch.StartNew();
        long lastMeter = 0;
        Program.Write(new { status = "capturing", sampleRate = 44100, channels = 2, bitsPerSample = 16 });
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                target.Refresh();
                if (target.HasExited) throw new InvalidOperationException("Target process exited.");
                sampleReady.WaitOne(100);
                Check(capture.GetNextPacketSize(out uint available));
                while (available > 0)
                {
                    Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint packetFlags, out _, out _));
                    try
                    {
                        totalFrames += frames;
                        if ((packetFlags & 1) != 0) discontinuities++;
                        int bytes = checked((int)frames * format.BlockAlign);
                        double packetPeak = 0;
                        sampleCount += frames * format.Channels;
                        if ((packetFlags & 2) == 0)
                        {
                            if (buffer.Length < bytes) Array.Resize(ref buffer, bytes);
                            Marshal.Copy(data, buffer, 0, bytes);
                            for (int i = 0; i < bytes; i += 2)
                            {
                                double sample = (short)(buffer[i] | (buffer[i + 1] << 8)) / 32768.0;
                                packetPeak = Math.Max(packetPeak, Math.Abs(sample));
                                sumSquares += sample * sample;
                            }
                            Array.Clear(buffer, 0, bytes);
                        }
                        if (packetPeak > 0.0001) signalFrames += frames;
                        intervalPeak = Math.Max(intervalPeak, packetPeak);
                        maximumPeak = Math.Max(maximumPeak, packetPeak);
                    }
                    finally { Check(capture.ReleaseBuffer(frames)); }
                    Check(capture.GetNextPacketSize(out available));
                }
                if (clock.ElapsedMilliseconds - lastMeter >= 500)
                {
                    Program.Write(new { status = "meter", elapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
                        peak = Math.Round(intervalPeak, 6), frames = totalFrames });
                    intervalPeak = 0;
                    lastMeter = clock.ElapsedMilliseconds;
                }
            }
        }
        finally
        {
            Check(client.Stop());
            started = false;
            Array.Clear(buffer);
        }
        Program.Write(new { status = "completed", pid, seconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
            totalFrames, signalFrames, discontinuities, maximumPeak = Math.Round(maximumPeak, 6),
            rms = sampleCount == 0 ? 0 : Math.Round(Math.Sqrt(sumSquares / sampleCount), 6),
            signalDetected = signalFrames > 0, retainsAudio = false });
    }

    internal static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    public void Dispose()
    {
        if (started) client?.Stop();
        if (capture is not null) { Marshal.ReleaseComObject(capture); capture = null; }
        if (client is not null) { Marshal.ReleaseComObject(client); client = null; }
        sampleReady.Dispose();
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid riid,
        ref PropVariant activationParams, IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ActivationParams { public uint ActivationType, TargetProcessId, ProcessLoopbackMode; }

// PROPVARIANT/VT_BLOB layout для x64. Проект принудительно собирается под x64.
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public uint BlobSize;
    [FieldOffset(16)] public IntPtr BlobData;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat
{
    public ushort FormatTag, Channels;
    public uint SampleRate, AverageBytesPerSecond;
    public ushort BlockAlign, BitsPerSample, ExtraSize;
}

[ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAgileObject { }

[ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public readonly ManualResetEvent Completed = new(false);
    internal IntPtr ActivationPointer;
    public int Result = unchecked((int)0x80004005);
    public object? Activated;
    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        try
        {
            int hr = operation.GetActivateResult(out int result, out object activated);
            Result = hr < 0 ? hr : result;
            Activated = activated;
        }
        catch (Exception error) { Result = error.HResult; }
        finally { ReleaseActivationParams(); Completed.Set(); }
        return 0;
    }
    internal void ReleaseActivationParams()
    {
        IntPtr pointer = Interlocked.Exchange(ref ActivationPointer, IntPtr.Zero);
        if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
    }
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormat format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestFormat);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
