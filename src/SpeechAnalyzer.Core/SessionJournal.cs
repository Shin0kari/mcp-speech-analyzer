using System.Text;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

// Состояние и ID commit заменяются одним атомарным файлом; полный окончательный текст — отдельный журнал.
public sealed partial class SessionJournal : IConversationStore, IModelOperationStore, IApplicationRetrievalStore
{
    private readonly object gate = new();
    private readonly string statePath, transcriptPath, operationPath;
    private Stored stored;
    private string transcriptFingerprint = "empty";
    public string TranscriptFingerprint { get { lock (gate) return transcriptFingerprint; } }
    private readonly Dictionary<Guid, TranscriptSegment> transcript = [];
    private readonly Dictionary<Guid, DurableModelOperation> operations = [];
    private readonly Dictionary<Guid, string> operationHashes = [], operationEntryHashes = [];
    private sealed record Stored(ConversationState State, ChatMessage[] Messages, Guid[] Committed);
    public string DirectoryPath { get; }
    public string? RecoveryNotice { get; private set; }
    public SessionJournal(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        // Reject a second write-capable adapter before constructor recovery can repair files.
        OrderedSttPersistence.VerifyStoreAvailable(PersistenceIdentity);
        Directory.CreateDirectory(DirectoryPath);
        statePath = Path.Combine(DirectoryPath, "state.json"); transcriptPath = Path.Combine(DirectoryPath, "transcript.jsonl");
        operationPath = Path.Combine(DirectoryPath, "model-operations.jsonl");
        stored = File.Exists(statePath) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(statePath)) ?? throw new IOException("Пустое состояние сессии.") : new(ConversationState.Empty, [], []);
        if (File.Exists(transcriptPath))
        {
            byte[] bytes = File.ReadAllBytes(transcriptPath);
            int last = Array.LastIndexOf(bytes, (byte)'\n');
            if (last != bytes.Length - 1)
            {
                // Сохраняем оборванный хвост отдельно; подтверждённые строки не изменяются.
                File.WriteAllBytes(Path.Combine(DirectoryPath, "transcript.partial-" + Guid.NewGuid().ToString("N")), bytes[(last + 1)..]);
                using var repair = new FileStream(transcriptPath, FileMode.Open, FileAccess.Write, FileShare.None);
                repair.SetLength(last + 1); repair.Flush(true);
                RecoveryNotice = "Сохранён оборванный хвост журнала; восстановлены только полностью записанные строки.";
            }
            foreach (string line in Encoding.UTF8.GetString(bytes, 0, last + 1).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = JsonSerializer.Deserialize<TranscriptSegment>(line) ?? throw new IOException("Неверная строка расшифровки.");
                if (!segment.IsFinal) throw new IOException("В журнале обнаружен неокончательный сегмент.");
                InterpretationValidation.ValidateRecognition(segment);
                if (transcript.TryGetValue(segment.Id, out var previous) && JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(segment)) throw new IOException("Конфликт сегмента расшифровки.");
                transcript[segment.Id] = segment;
                transcriptFingerprint = OperationVersionHash.Of(transcriptFingerprint + line + "\n");
            }
        }
        if (File.Exists(operationPath))
        {
            byte[] bytes = File.ReadAllBytes(operationPath);
            int last = Array.LastIndexOf(bytes, (byte)'\n');
            if (last != bytes.Length - 1)
            {
                File.WriteAllBytes(Path.Combine(DirectoryPath, "model-operations.partial-" + Guid.NewGuid().ToString("N")), bytes[(last + 1)..]);
                using var repair = new FileStream(operationPath, FileMode.Open, FileAccess.Write, FileShare.None);
                repair.SetLength(last + 1); repair.Flush(true);
                RecoveryNotice = "Сохранён оборванный хвост журнала модельных операций.";
            }
            foreach (string line in Encoding.UTF8.GetString(bytes, 0, last + 1).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                ReplayOperation(line);
            }
        }
        int unresolved = operations.Values.Count(operation => operation.Phase == ModelOperationPhase.ReconciliationRequired);
        if (Directory.EnumerateFiles(DirectoryPath, "transcript.partial-*").Any())
            RecoveryNotice = (RecoveryNotice is null ? "" : RecoveryNotice + " ") + "В каталоге есть оборванный хвост расшифровки; часть текста могла быть утрачена.";
        if (unresolved > 0)
            RecoveryNotice = (RecoveryNotice is null ? "" : RecoveryNotice + " ") + $"Требуется сверка {unresolved} модельных операций; автоматический повтор не выполняется.";
    }
    public IReadOnlyList<TranscriptSegment> Transcript { get { lock (gate) return transcript.Values.OrderBy(s => s.StartMs).ThenBy(s => s.Channel).ToArray(); } }
    public StoreSnapshot Read() { lock (gate) return new(stored.State, stored.Messages); }
    public IReadOnlyList<DurableModelOperation> ModelOperations { get { lock (gate) return operations.Values.ToArray(); } }
    public void PrepareModelOperation(DurableModelOperation operation)
    {
        lock (gate)
        {
            if (operations.TryGetValue(operation.OperationId, out var previous))
            {
                if (previous.Phase != ModelOperationPhase.Accepted || operation.Phase != ModelOperationPhase.Prepared)
                    throw new InvalidOperationException("Нельзя заменить уже подготовленную модельную операцию.");
                if (previous.Request.BoundaryMs != operation.Request.BoundaryMs || previous.Versions != operation.Versions
                    || previous.Request.Kind != operation.Request.Kind || previous.Request.RollingContextSeconds != operation.Request.RollingContextSeconds
                    || previous.Request.FallbackInstruction != operation.Request.FallbackInstruction
                    || previous.Request.UserInstruction != operation.Request.UserInstruction || previous.Request.Context != operation.Request.Context)
                    throw new InvalidOperationException("Принятый снимок запроса нельзя изменить.");
            }
            if (operation.Phase is not (ModelOperationPhase.Accepted or ModelOperationPhase.Prepared) || operation.Request.OperationId != operation.OperationId)
                throw new InvalidOperationException("Неверная начальная запись модельной операции.");
            AppendOperation(operation, snapshot: true); operations[operation.OperationId] = operation;
            SessionEventLog.Current?.Write("model_operation_prepared",new{operation.OperationId,operation.Phase,operation.Request.BoundaryMs,
                segments=operation.Request.Segments.Select(s=>new{s.Id,s.Channel,s.StartMs,s.EndMs}),operation.Request.Kind});
        }
    }
    public void AdvanceModelOperation(Guid operationId, ModelOperationPhase phase, string? remoteOperationId = null, ProcessingResult? result = null)
    {
        lock (gate)
        {
            if (!operations.TryGetValue(operationId, out var previous)) throw new InvalidOperationException("Операция не подготовлена.");
            if (previous.Phase == ModelOperationPhase.Completed) return;
            if (previous.Phase is ModelOperationPhase.Abandoned or ModelOperationPhase.ValidationRejected)
            {
                if (phase == previous.Phase) return;
                throw new InvalidOperationException("Отменённый запрос нельзя возобновить.");
            }
            if (!Allowed(previous.Phase, phase)) throw new InvalidOperationException($"Недопустимый переход операции: {previous.Phase} → {phase}.");
            var next = previous with
            {
                Phase = phase,
                RemoteOperationId = remoteOperationId ?? previous.RemoteOperationId,
                Result = result ?? previous.Result,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            AppendOperation(next); operations[operationId] = next;
            SessionEventLog.Current?.Write("model_operation_phase",new{operationId,phase,remoteOperationId});
        }
    }
    private void AppendOperation(DurableModelOperation operation, bool snapshot = false)
    {
        string full = JsonSerializer.Serialize(operation);
        string hash = OperationVersionHash.Of(full);
        string line = snapshot ? full : JsonSerializer.Serialize(new OperationChange(2, operation.OperationId,
            operationHashes[operation.OperationId], hash, operation.Phase, operation.UpdatedAt,
            operation.RemoteOperationId == operations[operation.OperationId].RemoteOperationId ? null : operation.RemoteOperationId,
            ReferenceEquals(operation.Result, operations[operation.OperationId].Result) ? null : operation.Result,
            ReferenceEquals(operation.RetrievedSourceIds, operations[operation.OperationId].RetrievedSourceIds) ? null : operation.RetrievedSourceIds));
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        using var stream = new FileStream(operationPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(true);
        operationHashes[operation.OperationId] = hash;
        operationEntryHashes[operation.OperationId] = OperationVersionHash.Of(line);
    }
    public void RecordSourceRead(Guid operationId, IReadOnlyList<Guid> segmentIds)
    {
        lock (gate)
        {
            var previous = operations[operationId];
            if (previous.Phase is not (ModelOperationPhase.Prepared or ModelOperationPhase.RequestSentUnknown or ModelOperationPhase.RemoteAccepted))
                throw new InvalidOperationException("Чтение оригиналов допустимо только во время активного запроса модели.");
            var ids = (previous.RetrievedSourceIds ?? []).Concat(segmentIds).Distinct().ToArray();
            if (ids.Length == (previous.RetrievedSourceIds?.Count ?? 0)) return;
            if (ids.Any(id => !transcript.TryGetValue(id, out var segment) || segment.EndMs > previous.Request.BoundaryMs))
                throw new InvalidOperationException("Оригинал отсутствует в принятой границе расшифровки.");
            var next = previous with { RetrievedSourceIds = ids, UpdatedAt = DateTimeOffset.UtcNow };
            AppendOperation(next); operations[operationId] = next;
        }
    }
    private static bool Allowed(ModelOperationPhase from, ModelOperationPhase to) => from switch
    {
        ModelOperationPhase.Accepted => to is ModelOperationPhase.Abandoned,
        ModelOperationPhase.Prepared => to is ModelOperationPhase.RequestSentUnknown or ModelOperationPhase.ResultReceived or ModelOperationPhase.Completed or ModelOperationPhase.ReconciliationRequired or ModelOperationPhase.Abandoned or ModelOperationPhase.ValidationRejected,
        ModelOperationPhase.RequestSentUnknown => to is ModelOperationPhase.RemoteAccepted or ModelOperationPhase.ResultReceived or ModelOperationPhase.ReconciliationRequired or ModelOperationPhase.Prepared or ModelOperationPhase.ValidationRejected,
        ModelOperationPhase.RemoteAccepted => to is ModelOperationPhase.ResultReceived or ModelOperationPhase.ReconciliationRequired or ModelOperationPhase.Prepared or ModelOperationPhase.ValidationRejected,
        ModelOperationPhase.ResultReceived => to is ModelOperationPhase.Completed or ModelOperationPhase.ValidationRejected,
        ModelOperationPhase.ReconciliationRequired => to is ModelOperationPhase.ResultReceived or ModelOperationPhase.Prepared or ModelOperationPhase.ValidationRejected,
        _ => false
    };
    public void AppendTranscript(TranscriptSegment segment)
    {
        InterpretationValidation.ValidateRecognition(segment);
        if (!segment.IsFinal) throw new ArgumentException("Сохраняется только окончательный текст.");
        lock (gate)
        {
            if (transcript.TryGetValue(segment.Id, out var previous))
            {
                if (JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(segment)) throw new InvalidOperationException("Окончательный текст нельзя заменить молча.");
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(segment) + "\n");
            using var stream = new FileStream(transcriptPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            stream.Write(bytes); stream.Flush(true); transcript[segment.Id] = segment;
            transcriptFingerprint = OperationVersionHash.Of(transcriptFingerprint + Encoding.UTF8.GetString(bytes));
            SessionEventLog.Current?.Write("transcript_final_saved",new{segment.Id,segment.Channel,segment.StartMs,segment.EndMs});
        }
    }
    public Task CommitAsync(ProcessingCommit commit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (stored.Committed.Contains(commit.OperationId)) return Task.CompletedTask;
            if (ReadSttRecovery() is { } recovery && (recovery.Unknown is { } gap && commit.Result.State.ProcessedThroughMs > gap.Fence / 16
                || recovery.IncompleteFrom.Any(at => at < commit.Result.State.ProcessedThroughMs * 16)
                || recovery.Phase == SttDrainPhase.Failed
                || recovery.Phase == SttDrainPhase.Stopped && !(recovery.Continuity is { } coverage &&
                    coverage.EvidenceThroughMs >= commit.Result.State.ProcessedThroughMs &&
                    recovery.TranscriptFingerprint == TranscriptFingerprint && recovery.Completion?.State is
                        TranscriptStopState.Finishing or TranscriptStopState.StoppedWithGaps)))
                throw new SttCoverageException("Нельзя сохранить модельную границу через неполное аудио/recovery failure.");
            if (operations.TryGetValue(commit.OperationId, out var operation) && operation.Phase is ModelOperationPhase.Abandoned or ModelOperationPhase.ValidationRejected)
                throw new InvalidOperationException("Отменённый запрос нельзя сохранить как успешный результат.");
            if (commit.Result.State.ProcessedThroughMs < stored.State.ProcessedThroughMs) throw new InvalidOperationException("Граница состояния не может двигаться назад.");
            var next = new Stored(commit.Result.State, commit.Message is null ? stored.Messages : [.. stored.Messages, commit.Message], [.. stored.Committed, commit.OperationId]);
            WriteAtomic(statePath, JsonSerializer.Serialize(next)); stored = next;
        }
        return Task.CompletedTask;
    }
    public void SaveContext(string context, string? threadId)
    {
        lock (gate) WriteAtomic(Path.Combine(DirectoryPath, "context.json"), JsonSerializer.Serialize(new SessionContext(context, threadId)));
    }
    public SessionContext? LoadContext()
    {
        string path = Path.Combine(DirectoryPath, "context.json");
        lock (gate) return File.Exists(path) ? JsonSerializer.Deserialize<SessionContext>(File.ReadAllText(path)) : null;
    }
    public void SaveCodexSettings(CodexSessionSettings settings)
    {
        lock (gate) WriteAtomic(Path.Combine(DirectoryPath, "codex-session.json"), JsonSerializer.Serialize(settings));
    }
    public CodexSessionSettings? LoadCodexSettings()
    {
        string path = Path.Combine(DirectoryPath, "codex-session.json");
        lock (gate) return File.Exists(path) ? JsonSerializer.Deserialize<CodexSessionSettings>(File.ReadAllText(path))
            ?? throw new IOException("Пустые настройки Codex.") : null;
    }
    private static void WriteAtomic(string path, string text)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(Encoding.UTF8.GetBytes(text)); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
public sealed record SessionContext(string Text, string? CodexThreadId);
