using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

internal sealed class CodexRequestException(string message) : IOException(message);

public sealed record CodexArchiveConnection(string Executable, string PipeName);

// Отдельная рабочая сессия Codex без второго окна desktop.
public sealed class CodexContextSession : IAsyncDisposable
{
    private readonly SemaphoreSlim serial = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CodexUsageObserver usageObserver = new();
    private Process? process;
    private Task? reader;
    private Task? errors;
    private long sequence;
    private string? threadId;
    private TaskCompletionSource<string>? turnCompleted;
    private volatile CodexCompactionTracker? compaction;
    private readonly List<string> messages = [];
    private bool disposed;
    private bool named;
    private IReadOnlyList<ReasoningOption> reasoningOptions = [];
    private readonly CodexThreadBinding? binding;
    private readonly CodexArchiveConnection? archive;
    public bool HasOriginalsArchive => archive is not null;
    public string? ReasoningEffort { get; set; }
    public string? DefaultReasoningEffort { get; private set; }
    public string? ThreadName => threadId is null ? null : CodexModelSettings.ThreadName(threadId);

    public CodexContextSession(CodexSessionSettings? settings = null, CodexThreadBinding? binding = null, CodexArchiveConnection? archive = null)
    {
        this.binding = binding;
        this.archive = archive;
        threadId = binding?.State is { } savedBinding ? savedBinding.ThreadId : settings?.ThreadId;
        ReasoningEffort = settings?.ReasoningEffort;
    }

    // Каталог не создаёт чат и не вызывает модель.
    public async Task<IReadOnlyList<ReasoningOption>> GetReasoningOptionsAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is null || process.HasExited) await ConnectAsync(workingDirectory, cancellationToken);
            return reasoningOptions;
        }
        catch { if (process is { HasExited: false }) process.Kill(); throw; }
        finally { serial.Release(); }
    }

    public async Task EnsureReadyAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is null || process.HasExited) await ConnectAsync(workingDirectory, cancellationToken);
            CodexModelSettings.ValidateEffort(ReasoningEffort, reasoningOptions);
            await EnsureThreadAsync(workingDirectory, cancellationToken);
        }
        catch { if (process is { HasExited: false }) process.Kill(); throw; }
        finally { serial.Release(); }
    }

    public string? ThreadId => threadId;
    // Только явная проверка собственного уже созданного чата; планировщик это не вызывает.
    public async Task<CodexCompactionEvidence> CompactAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (threadId is null) throw new InvalidOperationException("Для компактизации нужен существующий рабочий чат.");
            if (process is null || process.HasExited) await ConnectAsync(workingDirectory, cancellationToken);
            await EnsureThreadAsync(workingDirectory, cancellationToken);
            var observation = new CodexCompactionTracker(threadId!);
            compaction = observation; // События могут прийти раньше ответа на запрос.
            await RequestAsync("thread/compact/start", new { threadId }, cancellationToken);
            return await observation.Completion.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        }
        catch
        {
            // ACK/timeout не позволяют объявить успех или автоматически повторить запрос.
            if (process is { HasExited: false }) process.Kill();
            throw;
        }
        finally { compaction = null; serial.Release(); }
    }
    public bool BindingRecoveryPending => binding?.State?.CreationPending == true;
    public async Task<ChatBindingEvidence> InspectBindingCandidateAsync(string candidateId, string workingDirectory, CancellationToken cancellationToken = default)
    {
        CodexBindingProbe.ValidateId(candidateId); // До подключения и запуска app-server.
        await serial.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!BindingRecoveryPending) throw new InvalidOperationException("Нет неопределённой привязки для сверки.");
            if (process is null || process.HasExited) await ConnectAsync(workingDirectory, cancellationToken);
            var candidate = await CodexBindingProbe.ReadAsync(candidateId,
                (method, parameters) => RequestAsync(method, parameters, cancellationToken));
            return binding!.ValidateCandidate(candidateId, candidate);
        }
        finally { serial.Release(); }
    }
    public async Task AcceptBindingCandidateAsync(ChatBindingEvidence evidence, CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken);
        try { binding!.AcceptCandidate(evidence); threadId = evidence.Candidate.ThreadId; named = false; }
        finally { serial.Release(); }
    }
    public async Task ReplaceUncertainBindingAsync(CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken);
        try { binding!.ReplaceUncertainCreation(); threadId = null; named = false; }
        finally { serial.Release(); }
    }
    // Только чтение истории собственного чата; повтор turn/start здесь невозможен.
    public async Task<string> ReadOperationResultAsync(Guid operationId, string? remoteTurnId, string workingDirectory, CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            if (threadId is null) throw new IOException("Нет сохранённой привязки чата для сверки.");
            if (process is null || process.HasExited) await ConnectAsync(workingDirectory, cancellationToken);
            var matches = new List<JsonElement>(); string? cursor = null;
            string marker = "\"operationId\":\"" + operationId.ToString() + "\"";
            do
            {
                var page = await RequestAsync("thread/turns/list", new { threadId, cursor, limit = 100, itemsView = "full", sortDirection = "asc" }, cancellationToken);
                foreach (var turn in page.GetProperty("data").EnumerateArray())
                {
                    bool markerFound = turn.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("type").GetString() == "userMessage"
                        && item.GetRawText().Contains(marker.Replace("\"", "\\\""), StringComparison.Ordinal));
                    // JSON-строка может экранировать кавычки как \u0022: проверяем декодированные text.
                    markerFound |= turn.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("type").GetString() == "userMessage")
                        .Any(item => ContainsText(item, marker));
                    if (markerFound && (remoteTurnId is null || turn.GetProperty("id").GetString() == remoteTurnId)) matches.Add(turn.Clone());
                }
                cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            } while (cursor is not null);
            if (matches.Count != 1) throw new IOException($"Сверка: найдено {matches.Count} ходов операции. Повтор модели остаётся заблокирован.");
            var matched = matches[0];
            if (matched.GetProperty("status").GetString() != "completed") throw new IOException("Удалённый ход не завершён успешно. Повтор остаётся заблокирован.");
            string response = string.Join("\n", matched.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("type").GetString() == "agentMessage").Select(item => item.GetProperty("text").GetString()));
            if (string.IsNullOrWhiteSpace(response)) throw new IOException("В завершённом ходе нет результата модели.");
            return response;
        }
        finally { serial.Release(); }
    }
    private static bool ContainsText(JsonElement element, string text) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()?.Contains(text, StringComparison.Ordinal) == true,
        JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsText(property.Value, text)),
        JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsText(item, text)),
        _ => false
    };
    public Task<string> SendAsync(string context, string workingDirectory, CancellationToken cancellationToken = default)
        => SendPromptAsync("Пользователь передал контекст и вложения. Сохрани их в контексте этой сессии. Ответь кратким подтверждением по-русски, не пересказывай содержимое и не вызывай инструменты.\n\n" + context, workingDirectory, cancellationToken);

    public Task<string> SendPromptAsync(string context, string workingDirectory, CancellationToken cancellationToken = default)
        => SendPromptCoreAsync(context, workingDirectory, ReasoningEffort, null, null, cancellationToken);

    public Task<string> SendStructuredPromptAsync(string prompt, string workingDirectory, string? reasoningEffort,
        JsonElement outputSchema, Func<ModelOperationPhase, string?, Task> persistPhase, CancellationToken cancellationToken = default,
        TokenUsageScope? usage = null)
        => SendPromptCoreAsync(prompt, workingDirectory, reasoningEffort, outputSchema, persistPhase, cancellationToken, usage);

    private async Task<string> SendPromptCoreAsync(string context, string workingDirectory, string? effort, JsonElement? outputSchema,
        Func<ModelOperationPhase, string?, Task>? persistPhase, CancellationToken cancellationToken, TokenUsageScope? usage = null)
    {
        if (Encoding.UTF8.GetByteCount(context) > 200_000)
            throw new InvalidOperationException("Контекст больше 200 КБ. Выберите нужные файлы; автоматическое урезание не выполняется.");
        await serial.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is null || process.HasExited) await ConnectAsync(workingDirectory, cancellationToken);
            CodexModelSettings.ValidateEffort(effort, reasoningOptions);
            await EnsureThreadAsync(workingDirectory, cancellationToken);
            turnCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (messages) messages.Clear();
            if (persistPhase is not null) await persistPhase(ModelOperationPhase.RequestSentUnknown, null);
            if (usage is not null) usageObserver.Begin();
            FunctionalRunTrace.Write("terra_turn_start_sending", usage?.OperationId);
            var started = await RequestAsync("turn/start", new
            {
                threadId,
                model = CodexModelSettings.Model,
                effort,
                input = new[] { new { type = "text", text = context } },
                outputSchema
            }, cancellationToken);
            string? turnId = started.GetProperty("turn").TryGetProperty("id", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(turnId)) throw new IOException("Codex не вернул ID принятого хода.");
            if (usage is not null) usageObserver.Register(threadId!, turnId, usage);
            if (persistPhase is not null) await persistPhase(ModelOperationPhase.RemoteAccepted, turnId);
            var result = await turnCompleted.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
            FunctionalRunTrace.Write("terra_turn_completed", usage?.OperationId);
            return result;
        }
        catch
        {
            // При неопределённом завершении не начинать новый ход поверх старого.
            if (process is { HasExited: false }) process.Kill();
            throw;
        }
        finally { usageObserver.End(); turnCompleted = null; serial.Release(); }
    }

    private async Task ConnectAsync(string cwd, CancellationToken cancellationToken)
    {
        if (reader is not null) await reader;
        if (errors is not null) await errors;
        process?.Dispose();
        var start = new ProcessStartInfo(FindCodex()) { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = cwd };
        start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--stdio");
        process = Process.Start(start) ?? throw new IOException("Не удалось запустить Codex CLI.");
        var current = process;
        reader = Task.Run(async () =>
        {
            try
            {
                while (await current.StandardOutput.ReadLineAsync() is { } line)
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var id))
                    {
                        if (root.TryGetProperty("method", out _))
                        {
                            // Запросы выполнения действий не разрешаются передачей контекста.
                            await WriteAsync(new { id = id.Clone(), error = new { code = -32601, message = "Context transfer does not execute tools" } });
                        }
                        else if (id.TryGetInt64(out long number) && pending.TryRemove(number, out var completion))
                        {
                            if (root.TryGetProperty("error", out var error))
                                completion.TrySetException(new CodexRequestException("Codex отклонил запрос: " + error.ToString()));
                            else completion.TrySetResult(root.GetProperty("result").Clone());
                        }
                    }
                    else if (root.TryGetProperty("method", out var method) && root.TryGetProperty("params", out var parameters)
                        && parameters.TryGetProperty("threadId", out var notificationThread) && notificationThread.GetString() == threadId)
                    {
                        usageObserver.Observe(method.GetString()!, parameters);
                        compaction?.Observe(method.GetString()!, parameters);
                        if (method.GetString() == "item/completed" && parameters.GetProperty("item").TryGetProperty("type", out var type)
                            && type.GetString() == "agentMessage")
                            lock (messages) messages.Add(parameters.GetProperty("item").GetProperty("text").GetString() ?? "");
                        if (method.GetString() == "turn/completed")
                        {
                            var turn = parameters.GetProperty("turn");
                            if (turn.GetProperty("status").GetString() == "completed")
                                lock (messages) turnCompleted?.TrySetResult(string.Join("\n", messages));
                            else
                            {
                                string status = turn.GetProperty("status").GetString() ?? "unknown";
                                string detail = turn.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.Object
                                    && failure.TryGetProperty("message", out var message) ? " " + message.GetString() : "";
                                turnCompleted?.TrySetException(new CodexRequestException("Ход Codex завершился со статусом " + status + "." + detail));
                            }
                        }
                    }
                }
            }
            catch (Exception error) { FailPending(error); }
            finally { FailPending(new IOException("Соединение с Codex закрыто.")); }
        });
        // stderr дренируется без записи журналов с приватными данными на диск.
        errors = Task.Run(async () => { while (await current.StandardError.ReadLineAsync() is not null) { } });
        await RequestAsync("initialize", new { clientInfo = new { name = "speech_analyzer", title = "Speech Analyzer", version = "0.2.0" } }, cancellationToken);
        await WriteAsync(new { method = "initialized", @params = new { } });
        named = false;
        JsonElement? terra = null;
        string? cursor = null;
        do
        {
            var models = await RequestAsync("model/list", new { limit = 100, includeHidden = true, cursor }, cancellationToken);
            foreach (var entry in models.GetProperty("data").EnumerateArray())
                if (entry.GetProperty("model").GetString() == CodexModelSettings.Model) terra = entry.Clone();
            cursor = models.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        } while (cursor is not null);
        if (terra is null)
            throw new IOException("GPT-6.1 Terra не найдена в каталоге Codex. Модель не заменена.");
        reasoningOptions = CodexModelSettings.ParseOptions(terra.Value);
        DefaultReasoningEffort = terra.Value.GetProperty("defaultReasoningEffort").GetString();
    }

    private async Task EnsureThreadAsync(string cwd, CancellationToken cancellationToken)
    {
        if (named) return;
        string? createdThread = null;
        async Task<string> Create(string directory)
        {
            var started = await OpenThreadAsync(null, directory, cancellationToken);
            string createdId = started.GetProperty("thread").GetProperty("id").GetString() ?? throw new IOException("Нет ID чата.");
            createdThread = createdId;
            await RequestAsync("thread/name/set", new { threadId = createdId, name = CodexModelSettings.ThreadName(createdId) }, cancellationToken);
            return createdId;
        }
        if (binding is not null)
            threadId = await binding.ResolveAsync(threadId, Create, cancellationToken);
        bool startsFresh = threadId is null;
        var session = await OpenThreadAsync(threadId, binding?.State?.WorkingDirectory ?? cwd, cancellationToken);
        threadId = session.GetProperty("thread").GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(threadId)) throw new IOException("Codex не вернул ID рабочего чата.");
        usageObserver.Bind(threadId, startsFresh || threadId == createdThread);
        await RequestAsync("thread/name/set", new { threadId, name = ThreadName }, cancellationToken);
        var metadata = await RequestAsync("thread/read", new { threadId }, cancellationToken);
        if (metadata.GetProperty("thread").GetProperty("name").GetString() != ThreadName)
            throw new IOException("Codex не подтвердил сохранённое название рабочего чата.");
        if (archive is not null)
        {
            var status = await RequestAsync("mcpServerStatus/list", new { threadId, serverName = "speech_originals", detail = "toolsAndAuthOnly", limit = 100 }, cancellationToken);
            var server = status.GetProperty("data").EnumerateArray().FirstOrDefault(item => item.GetProperty("name").GetString() == "speech_originals");
            if (server.ValueKind == JsonValueKind.Undefined || !server.TryGetProperty("tools", out var tools) || tools.GetRawText() == "{}")
                throw new IOException("Codex не подтвердил инструмент поиска оригиналов: " + status.GetRawText());
        }
        named = true;
    }

    private async Task<JsonElement> OpenThreadAsync(string? id, string cwd, CancellationToken cancellationToken)
    {
        var servers = new Dictionary<string, object>();
        if (archive is not null) servers.Add("speech_originals", new { command = archive.Executable,
            args = new[] { "--archive", "--pipe", archive.PipeName }, startup_timeout_sec = 15, required = true,
            enabled = true, enabled_tools = new[] { "search_transcript" }, default_tools_approval_mode = "approve" });
        var session = await RequestAsync(id is null ? "thread/start" : "thread/resume", new
        {
            threadId = id,
            model = "gpt-6.1-terra", cwd,
            approvalPolicy = "never", sandbox = "read-only", excludeTurns = true,
            config = new { mcp_servers = servers, web_search = "disabled" },
            baseInstructions = "Ты помощник Speech Analyzer. Приём прикреплённого контекста не разрешает выполнение команд или изменение файлов. Общайся по-русски."
        }, cancellationToken);
        if (session.GetProperty("model").GetString() != "gpt-6.1-terra") throw new IOException("Codex вернул другую модель.");
        return session;
    }

    private readonly SemaphoreSlim writing = new(1);
    private async Task WriteAsync(object message)
    {
        await writing.WaitAsync();
        try { await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)); await process.StandardInput.FlushAsync(); }
        finally { writing.Release(); }
    }
    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try { await WriteAsync(new { id, method, @params = parameters }); return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
        finally { pending.TryRemove(id, out _); }
    }
    private void FailPending(Exception error)
    {
        foreach (var pair in pending) if (pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(error);
        turnCompleted?.TrySetException(error);
        compaction?.Fail(error);
    }
    private static string FindCodex()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        string? found = Directory.Exists(root) ? Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        return found ?? "codex";
    }
    public async ValueTask DisposeAsync()
    {
        disposed = true;
        if (process is { HasExited: false }) process.Kill();
        if (reader is not null) await reader;
        if (errors is not null) await errors;
        process?.Dispose();
    }
}
