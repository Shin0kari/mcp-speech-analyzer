using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

public enum ModelOperationPhase
{
    Prepared,
    RequestSentUnknown,
    RemoteAccepted,
    ResultReceived,
    Completed,
    ReconciliationRequired,
    // Добавлено в конец: числовые значения старого журнала сохраняются.
    Accepted,
    // Явная отмена запроса с известным исходом; исходный текст и шаги сохраняются.
    Abandoned,
    // Terminal local validation failure; the received result is retained, never committed.
    ValidationRejected
}

public sealed record ModelOperationVersions(string Context, string Attachments, string Settings, string? ReasoningEffort);
public sealed record DurableModelOperation(Guid OperationId, ProcessingRequest Request, ModelOperationVersions Versions,
    ModelOperationPhase Phase, string? RemoteOperationId, ProcessingResult? Result, DateTimeOffset UpdatedAt,
    IReadOnlyList<Guid>? RetrievedSourceIds = null);

public interface IModelOperationStore
{
    IReadOnlyList<DurableModelOperation> ModelOperations { get; }
    // Новая Accepted/Prepared либо уточнение Accepted → Prepared с той же принятой границей и настройками.
    void PrepareModelOperation(DurableModelOperation operation);
    void AdvanceModelOperation(Guid operationId, ModelOperationPhase phase, string? remoteOperationId = null, ProcessingResult? result = null);
    void RecordSourceRead(Guid operationId, IReadOnlyList<Guid> segmentIds) => throw new NotSupportedException("Хранилище не поддерживает журнал чтения оригиналов.");
}

public static class OperationVersionHash
{
    public static string Of(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
