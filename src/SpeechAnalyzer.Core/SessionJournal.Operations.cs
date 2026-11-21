using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed partial class SessionJournal
{
    // Request/Versions наследуются из снимка; null в delta не очищает сохранённое поле.
    private sealed record OperationChange(int JournalVersion, Guid OperationId, string PreviousHash, string OperationHash,
        ModelOperationPhase Phase, DateTimeOffset UpdatedAt, string? RemoteOperationId,
        ProcessingResult? Result, IReadOnlyList<Guid>? RetrievedSourceIds);

    private void ReplayOperation(string line)
    {
        using var document = JsonDocument.Parse(line);
        DurableModelOperation operation;
        if (document.RootElement.TryGetProperty("JournalVersion", out var version))
        {
            if (version.GetInt32() != 2) throw new IOException("Неизвестная версия журнала модельных операций.");
            var change = JsonSerializer.Deserialize<OperationChange>(line) ?? throw new IOException("Пустое изменение модельной операции.");
            if (!operations.TryGetValue(change.OperationId, out var previous))
                throw new IOException("Нет базового снимка для изменения модельной операции.");
            // Потеря подтверждения целой append-строки не требует повторного применения.
            if (operationHashes[change.OperationId] == change.OperationHash
                && operationEntryHashes[change.OperationId] == OperationVersionHash.Of(line)) return;
            if (change.PreviousHash != operationHashes[change.OperationId])
                throw new IOException("Нарушена цепочка изменений модельной операции.");
            if (!Enum.IsDefined(change.Phase) || previous.Phase != change.Phase && !Allowed(previous.Phase, change.Phase))
                throw new IOException("Неверная фаза изменения модельной операции.");
            operation = previous with { Phase = change.Phase, UpdatedAt = change.UpdatedAt,
                RemoteOperationId = change.RemoteOperationId ?? previous.RemoteOperationId,
                Result = change.Result ?? previous.Result,
                RetrievedSourceIds = change.RetrievedSourceIds ?? previous.RetrievedSourceIds };
            if (OperationVersionHash.Of(JsonSerializer.Serialize(operation)) != change.OperationHash)
                throw new IOException("Контрольный хеш изменения модельной операции не совпадает.");
        }
        else operation = JsonSerializer.Deserialize<DurableModelOperation>(line)
            ?? throw new IOException("Неверная запись журнала модельных операций.");
        if (operation.Request is null || operation.Versions is null || operation.Request.OperationId != operation.OperationId)
            throw new IOException("В журнале модельных операций нет корректного снимка запроса.");
        if (operations.TryGetValue(operation.OperationId, out var old) && operation.UpdatedAt < old.UpdatedAt)
            throw new IOException("Хронология журнала модельных операций нарушена.");
        operations[operation.OperationId] = operation;
        operationHashes[operation.OperationId] = OperationVersionHash.Of(JsonSerializer.Serialize(operation));
        operationEntryHashes[operation.OperationId] = OperationVersionHash.Of(line);
    }
}
