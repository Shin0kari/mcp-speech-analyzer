using System.Text.Json;
using System.Threading.Channels;

namespace SpeechAnalyzer.Core;

public sealed record AudioDiagnosticStatus(string FilePath, int Pending, int Written, long Dropped, string? Error);

// Независимый ограниченный writer метаданных. Ошибка диагностики не является ошибкой PCM-recorder.
public sealed class AudioDiagnosticJournal : IAsyncDisposable
{
    public const int QueueLimit=8, RecordLimit=128, ByteLimit=1_048_576;
    private readonly object gate=new();
    private readonly Channel<Entry> queue=Channel.CreateBounded<Entry>(new BoundedChannelOptions(QueueLimit){SingleReader=true});
    private readonly Task writer;
    private int pending,written,submitted;
    private long dropped;
    private string? error;
    private bool disposed;
    private readonly string path;
    private sealed record Entry(string Name,object? Data,TaskCompletionSource<bool>? Barrier=null);
    public AudioDiagnosticJournal(string directory,Action<string>? diagnosticSink=null)
    {
        path=Path.Combine(directory,"capture-diagnostics-"+Guid.NewGuid().ToString("N")+".jsonl");
        writer=Task.Run(()=>WriteAsync(diagnosticSink));
    }
    public AudioDiagnosticStatus Status {get{lock(gate)return new(path,pending,written,dropped,error);}}
    public void NoteDropped() { lock(gate)dropped++; }
    public bool TryWrite(string name,object? data)
    {
        lock(gate)
        {
            if(disposed||error is not null||submitted>=RecordLimit||!queue.Writer.TryWrite(new(name,data)))
            {dropped++;return false;}
            pending++;submitted++;return true;
        }
    }
    private async Task WriteAsync(Action<string>? sink)
    {
        try
        {
            using var file=sink is null?new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read):null;
            await foreach(var entry in queue.Reader.ReadAllAsync())
            {
                try
                {
                    if(entry.Barrier is {} barrier){file?.Flush(true);barrier.TrySetResult(true);continue;}
                    byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(new{version=1,name=entry.Name,data=entry.Data});
                    if(bytes.Length>ByteLimit)throw new IOException("Diagnostic record limit");
                    if(sink is not null)sink(System.Text.Encoding.UTF8.GetString(bytes));
                    else{file!.Write(bytes);file.WriteByte(10);file.Flush(true);}
                    lock(gate)written++;
                }
                finally{lock(gate)pending--;}
            }
        }
        catch(Exception failure)
        {
            lock(gate)error??="Диагностика недоступна: "+failure.GetType().Name;
        }
        finally
        {
            lock(gate)
            {
                queue.Writer.TryComplete();
                while(queue.Reader.TryRead(out var entry)){pending--;dropped++;entry.Barrier?.TrySetResult(false);}
            }
        }
    }
    public async Task<bool> FlushAsync()
    {
        var barrier=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(gate)
        {
            if(disposed||error is not null||!queue.Writer.TryWrite(new("",null,barrier)))return false;
            pending++;
        }
        try
        {
            var finished=await Task.WhenAny(barrier.Task,writer).WaitAsync(TimeSpan.FromSeconds(2));
            return finished==barrier.Task&&await barrier.Task;
        }
        catch(TimeoutException){lock(gate)error??="Диагностика не завершена за 2 секунды.";return false;}
    }
    public async ValueTask DisposeAsync()
    {
        lock(gate){disposed=true;queue.Writer.TryComplete();}
        try{await writer.WaitAsync(TimeSpan.FromSeconds(2));}
        catch(TimeoutException){lock(gate)error??="Диагностика не завершена за 2 секунды.";}
    }
}
