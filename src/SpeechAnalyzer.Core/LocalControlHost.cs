using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

// Локальный IPC между Rust MCP и владельцем состояния. Доступ только текущему пользователю Windows.
public sealed class LocalControlHost : IAsyncDisposable
{
    public static string DefaultPipeName => "speech-analyzer-" + string.Concat(Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<string, JsonElement, Task<object>> handler;
    private readonly Task listener;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    public string? Error { get; private set; }
    public string PipeName { get; }
    public LocalControlHost(Func<string, JsonElement, Task<object>> handler, string? pipeName = null)
    {
        this.handler = handler;
        PipeName = pipeName ?? DefaultPipeName;
        listener = Task.Run(ListenAsync);
    }
    private async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            bool listening = false;
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                listening = true;
                ready.TrySetResult();
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                string line = await ReadBoundedLineAsync(pipe, timeout.Token);
                object response;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    string method = json.RootElement.GetProperty("method").GetString() ?? "";
                    SessionEventLog.Current?.Write("mcp_command",new{method});
                    var parameters = json.RootElement.TryGetProperty("params", out var p) ? p.Clone() : JsonSerializer.SerializeToElement(new { });
                    response = new { ok = true, result = await handler(method, parameters).WaitAsync(timeout.Token) };
                }
                catch (StaleAudioCatalogException error)
                {
                    response = new { ok = false, error = new { code = "stale_catalog", currentCatalogVersion = error.CurrentVersion,
                        message = "Каталог изменился. Повторно вызовите list_audio_sources и выберите источники из нового списка." } };
                }
                catch (StaleTranscriptCursorException error)
                { response = new { ok = false, error = new { code = "stale_cursor", message = error.Message } }; }
                catch (Exception error) { SessionEventLog.Current?.Write("mcp_error",new{type=error.GetType().Name}); response = new { ok = false, error = error.Message }; }
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response) + "\n");
                if (bytes.Length > 1_048_576) bytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"Result exceeds 1 MiB; narrow the request\"}\n");
                await pipe.WriteAsync(bytes, timeout.Token);
                await pipe.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception error) when (listening && error is IOException or OperationCanceledException or JsonException) { /* Отключение клиента не завершает приложение. */ }
            catch (Exception error)
            {
                Error = error.Message;
                ready.TrySetException(error);
                break; // Ошибка создания сервера не должна вызывать бесконечный цикл.
            }
        }
    }
    private static async Task<string> ReadBoundedLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) throw new IOException("Client disconnected before request completed.");
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            memory.Write(buffer, 0, newline < 0 ? count : newline);
            if (memory.Length > 65_536) throw new IOException("Request exceeds 64 KiB.");
            if (newline >= 0) return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        ready.TrySetCanceled();
        await listener.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
