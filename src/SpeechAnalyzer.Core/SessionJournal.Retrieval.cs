using System.Text;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed partial class SessionJournal
{
    private string RetrievalPath(Guid operationId) => Path.Combine(DirectoryPath, "retrieval", operationId.ToString("N") + ".json");
    public RetrievalWorkflow? ReadRetrieval(Guid operationId)
    {
        lock (gate)
        {
            string path = RetrievalPath(operationId);
            return File.Exists(path) ? JsonSerializer.Deserialize<RetrievalWorkflow>(File.ReadAllText(path))
                ?? throw new IOException("Пустой журнал поиска оригиналов.") : null;
        }
    }
    public IReadOnlyList<TranscriptSegment> ReadOriginalTranscript() => Transcript;
    public void RecordSuppliedOriginals(Guid operationId, IReadOnlyList<Guid> ids) => RecordSourceRead(operationId, ids);
    public void SaveRetrieval(RetrievalWorkflow workflow)
    {
        lock (gate)
        {
            var operation = operations[workflow.OperationId];
            string hash = OperationVersionHash.Of(JsonSerializer.Serialize(new { request = operation.Request, versions = operation.Versions }));
            if (operation.Phase is ModelOperationPhase.Completed or ModelOperationPhase.Abandoned or ModelOperationPhase.ValidationRejected || !ApplicationRetrievalBackend.SupportsWorkflow(workflow.Version) || workflow.BoundaryMs != operation.Request.BoundaryMs
                || workflow.RequestHash != hash || workflow.Steps.Count is < 1 or > ApplicationRetrievalBackend.MaxRounds + 1
                || workflow.Batches.Count > ApplicationRetrievalBackend.MaxRounds || workflow.Steps.Count < workflow.Batches.Count
                || workflow.Steps.Count > workflow.Batches.Count + 1 || workflow.Steps.Select(s => s.Id).Distinct().Count() != workflow.Steps.Count)
                throw new InvalidDataException("Неверный снимок поиска оригиналов.");
            foreach (var step in workflow.Steps)
                if (step.Id == workflow.OperationId || Encoding.UTF8.GetByteCount(step.Prompt) > 200_000
                    || step.Phase is not (ModelOperationPhase.Prepared or ModelOperationPhase.RequestSentUnknown or ModelOperationPhase.RemoteAccepted or ModelOperationPhase.ResultReceived)
                    || (step.Phase == ModelOperationPhase.ResultReceived) != (step.Response is not null)
                    || step.Response is { } response && Encoding.UTF8.GetByteCount(response) > 1024 * 1024)
                    throw new InvalidDataException("Неверный или слишком большой промежуточный шаг.");
            foreach (var batch in workflow.Batches)
            {
                if (batch.Query.OperationId != workflow.OperationId || batch.Passages.Count > ApplicationRetrievalBackend.MaxPassages
                    || batch.Passages.Sum(p => Encoding.UTF8.GetByteCount(p.Segment.Text)) > ApplicationRetrievalBackend.MaxTextBytes)
                    throw new InvalidDataException("Превышена граница выдачи оригиналов.");
                foreach (var passage in batch.Passages)
                    if (!transcript.TryGetValue(passage.Segment.Id, out var original) || JsonSerializer.Serialize(original) != JsonSerializer.Serialize(passage.Segment)
                        || original.EndMs > workflow.BoundaryMs || passage.Sha256 != OperationVersionHash.Of(original.Text))
                        throw new InvalidDataException("Выдача не соответствует неизменённому оригиналу или границе операции.");
            }
            var previous = ReadRetrieval(workflow.OperationId);
            if (previous is not null)
            {
                if (workflow.Version != previous.Version || workflow.Steps.Count < previous.Steps.Count || workflow.Batches.Count < previous.Batches.Count)
                    throw new InvalidDataException("Нельзя удалить промежуточный шаг или выданные оригиналы.");
                for (int i = 0; i < previous.Steps.Count; i++)
                {
                    var old = previous.Steps[i]; var next = workflow.Steps[i];
                    if (old.Id != next.Id || old.Prompt != next.Prompt || (int)next.Phase < (int)old.Phase
                        || old.Response is not null && old.Response != next.Response
                        || old.RemoteTurnId is not null && old.RemoteTurnId != next.RemoteTurnId)
                        throw new InvalidDataException("Нельзя заменить уже сохранённый модельный шаг.");
                }
                for (int i = 0; i < previous.Batches.Count; i++)
                    if (JsonSerializer.Serialize(previous.Batches[i]) != JsonSerializer.Serialize(workflow.Batches[i]))
                        throw new InvalidDataException("Нельзя заменить сохранённую выдачу оригиналов.");
            }
            string path = RetrievalPath(workflow.OperationId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicTextFile.Write(path, JsonSerializer.Serialize(workflow));
        }
    }
}
