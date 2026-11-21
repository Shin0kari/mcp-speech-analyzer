using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SpeechAnalyzer.Core;

// Optional diagnostics never wait for a filesystem lock on the capture thread.
internal static class DiagnosticMetadataWriter
{
    private sealed record Entry(string Path, object Metadata, bool Flush);
    private static readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(1024)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static readonly Task owner = Task.Run(Run);
    private static long dropped;
    private static string? error;
    public static long Dropped => Interlocked.Read(ref dropped);
    public static string? Error => Volatile.Read(ref error);
    public static void Write(string path, object metadata, bool flush = false)
    {
        if (!queue.Writer.TryWrite(new(path, metadata, flush))) Interlocked.Increment(ref dropped);
    }
    private static async Task Run()
    {
        await foreach (var entry in queue.Reader.ReadAllAsync())
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry.Metadata) + "\n");
                using var file = new FileStream(entry.Path, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes); file.Flush(entry.Flush);
            }
            catch (Exception failure) { Interlocked.CompareExchange(ref error, failure.GetType().Name, null); }
    }
}
