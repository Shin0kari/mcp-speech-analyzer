using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;

namespace SpeechAnalyzer.Core;

public enum SttWorkerPhase { RuntimeSelected = 1, HeaderAccepted, InputReadCompleted, ManagedProcessingEntered,
    ManagedProcessingExited, WhisperEnumerationEntered, WhisperEnumerationExited, ResultSerialized, ResultSendCompleted, OutputClosed,
    ResultSerializationEntered }
public enum SttRuntimeKind { Unknown, Cpu, CpuNoAvx, Cuda, Cuda12, Vulkan, CoreML, OpenVino }
public sealed record SttRuntimeIdentity(SttRuntimeKind Kind, string? LibraryBasename = null, string? WrapperVersion = null);
public sealed record SttWorkerObservation(int Version, long Sequence, long Ordinal, SttWorkerPhase Phase,
    long Ticks, long Frequency, long Dropped, SttRuntimeIdentity? Runtime = null);

// Отдельный канал метаданных: ready/result и PCM-протокол остаются прежними.
// Писатель никогда не вызывается синхронно из recognition. Полный stderr не передаётся.
public sealed class SttObservationEmitter : IAsyncDisposable
{
    public const string EnvironmentKey = "SPEECH_ANALYZER_STT_OBSERVATIONS_V1";
    public const int QueueLimit = 32, EventLimit = 128, FrameLimit = 1024;
    private readonly Channel<SttWorkerObservation> queue = Channel.CreateBounded<SttWorkerObservation>(QueueLimit);
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly Task writer;
    private long attempted, dropped;
    private bool closed;
    public long Dropped { get { lock (gate) return dropped; } }
    public SttObservationEmitter(Func<CancellationToken, Task<Stream>> connect)
    {
        writer = Task.Run(async () =>
        {
            try
            {
                await using var stream = await connect(stop.Token).ConfigureAwait(false);
                await foreach (var value in queue.Reader.ReadAllAsync(stop.Token))
                    await WriteFrameAsync(stream, value, stop.Token).ConfigureAwait(false);
                SttWorkerObservation final;
                lock (gate) final = New(++attempted, 0, SttWorkerPhase.OutputClosed, null);
                await WriteFrameAsync(stream, final, stop.Token).ConfigureAwait(false);
            }
            catch { lock (gate) { closed = true; dropped++; } }
            finally { while (queue.Reader.TryRead(out _)) lock (gate) dropped++; }
        });
    }
    public static SttObservationEmitter? FromEnvironment()
    {
        string? name = Environment.GetEnvironmentVariable(EnvironmentKey);
        if (name is null || !name.StartsWith("sa-stt-", StringComparison.Ordinal) || name.Length != 39) return null;
        return new(async token =>
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(1000, token).ConfigureAwait(false); return pipe; }
            catch { pipe.Dispose(); throw; }
        });
    }
    private SttWorkerObservation New(long sequence, long ordinal, SttWorkerPhase phase, SttRuntimeIdentity? runtime) =>
        new(1, sequence, ordinal, phase, Stopwatch.GetTimestamp(), Stopwatch.Frequency, dropped, runtime);
    public void Emit(long ordinal, SttWorkerPhase phase, SttRuntimeIdentity? runtime = null)
    {
        lock (gate)
        {
            long sequence = ++attempted;
            if (closed || sequence > EventLimit || !queue.Writer.TryWrite(New(sequence, ordinal, phase, runtime))) dropped++;
        }
    }
    public static async Task WriteFrameAsync(Stream stream, SttWorkerObservation value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > FrameLimit) throw new IOException("Превышен diagnostic frame.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task<SttWorkerObservation?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token).ConfigureAwait(false);
        int count = BitConverter.ToInt32(header);
        if (count is < 1 or > FrameLimit) throw new IOException("Неверный diagnostic frame.");
        byte[] bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize<SttWorkerObservation>(bytes) ?? throw new IOException("Пустой diagnostic frame.");
        if (value.Version != 1 || value.Sequence < 1 || value.Ordinal < 0 || value.Dropped < 0 || value.Ticks < 0
            || value.Frequency <= 0 || !Enum.IsDefined(value.Phase)) throw new IOException("Неизвестная версия/значение diagnostic frame.");
        // Непроверенное имя runtime/path никогда не попадает в журнал приложения.
        if (value.Runtime is { } runtime)
            value = value with { Runtime = new(Enum.IsDefined(runtime.Kind) ? runtime.Kind : SttRuntimeKind.Unknown,
                runtime.LibraryBasename == "whisper.dll" ? "whisper.dll" : null,
                runtime.WrapperVersion is { Length: > 0 and <= 32 } version && version.All(c => char.IsAsciiDigit(c) || c == '.') ? version : null) };
        return value;
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { closed = true; queue.Writer.TryComplete(); }
        // Только teardown; inference не ожидает отправку диагностики.
        try { await writer.WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false); }
        catch (TimeoutException) { stop.Cancel(); }
        // CTS принадлежит writer, пока возможен асинхронный I/O.
        _ = writer.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
