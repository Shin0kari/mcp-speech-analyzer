using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpeechAnalyzer.Core;

internal static class OperationJournalV2Tests
{
    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "speech-operation-v2-" + Guid.NewGuid().ToString("N"));
        public string PathName => Path.Combine(DirectoryPath, "model-operations.jsonl");
        public readonly TranscriptSegment Original = new(Guid.NewGuid(), AudioChannel.Other, 0, 1000, DateTimeOffset.UnixEpoch, "Точный оригинал: ёж 🦊.\nВторая строка.", true);
        public SessionJournal Journal;
        public DurableModelOperation Initial;
        public Fixture(string fact = "Прежний факт")
        {
            Journal = new(DirectoryPath); Journal.AppendTranscript(Original);
            var request = new ProcessingRequest(Guid.NewGuid(), OperationKind.Answer, 1000, new(0, [fact], [], []), [Original], "Вопрос", "Итоги", "Контекст");
            Initial = new(request.OperationId, request, new("context", "files", "settings", "high"), ModelOperationPhase.Prepared, null, null, DateTimeOffset.UtcNow);
            Journal.PrepareModelOperation(Initial);
        }
        public DurableModelOperation Current => Journal.ModelOperations.Single();
        public ProcessingResult Result => new(new(1000, ["Новый факт"], [], []), Original.Text, [new("fact", Original.Text, [Original.Id])]);
        public void Advance(ModelOperationPhase phase, ProcessingResult? result = null)
            => Journal.AdvanceModelOperation(Initial.OperationId, phase, phase == ModelOperationPhase.RemoteAccepted ? "test-turn" : null, result);
        public void Reopen() => Journal = new(DirectoryPath);
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Json(object? value) => JsonSerializer.Serialize(value);
    private static void MustReject(string directory)
    {
        try { _ = new SessionJournal(directory); }
        catch (IOException) { return; }
        throw new Exception("Повреждённый v2-журнал принят.");
    }

    public static Task PhasesSourcesAndRestart()
    {
        using var fixture = new Fixture();
        foreach (var phase in new[] { ModelOperationPhase.RequestSentUnknown, ModelOperationPhase.RemoteAccepted,
            ModelOperationPhase.ReconciliationRequired, ModelOperationPhase.Prepared, ModelOperationPhase.ResultReceived, ModelOperationPhase.Completed })
        {
            if (phase == ModelOperationPhase.RequestSentUnknown)
                fixture.Journal.RecordSuppliedOriginals(fixture.Initial.OperationId, [fixture.Original.Id]);
            fixture.Advance(phase, phase == ModelOperationPhase.ResultReceived ? fixture.Result : null);
            string expected = Json(fixture.Current);
            fixture.Reopen();
            Check(Json(fixture.Current) == expected, "Восстановление потеряло поля операции " + phase);
            Check(fixture.Current.RetrievedSourceIds?.Single() == fixture.Original.Id, "Утеряна выдача оригинала.");
        }
        var lines = File.ReadAllLines(fixture.PathName);
        Check(lines.Skip(1).All(line => !JsonNode.Parse(line)!.AsObject().ContainsKey("Request")), "Переход повторяет Request.");
        Check(lines.Count(line => JsonNode.Parse(line)!["Result"] is not null) == 1, "Result повторяется на последующих фазах.");
        return Task.CompletedTask;
    }

    public static Task LegacyAndMixed()
    {
        using var fixture = new Fixture();
        var original = fixture.Initial;
        var accepted = original with { Phase = ModelOperationPhase.Accepted };
        string legacy = Json(accepted) + "\n" + Json(original) + "\n";
        File.WriteAllText(fixture.PathName, legacy, new UTF8Encoding(false));
        fixture.Reopen();
        fixture.Advance(ModelOperationPhase.ResultReceived, fixture.Result);
        fixture.Advance(ModelOperationPhase.Completed);
        string expected = Json(fixture.Current); fixture.Reopen();
        Check(Json(fixture.Current) == expected && File.ReadAllText(fixture.PathName).StartsWith(legacy, StringComparison.Ordinal), "Изменён старый префикс журнала.");
        // Другая новая операция может идти после старых и v2-записей в том же файле.
        var request = original.Request with { OperationId = Guid.NewGuid() };
        fixture.Journal.PrepareModelOperation(original with { OperationId = request.OperationId, Request = request, Phase = ModelOperationPhase.Accepted });
        fixture.Journal.PrepareModelOperation(original with { OperationId = request.OperationId, Request = request });
        fixture.Journal.AdvanceModelOperation(request.OperationId, ModelOperationPhase.Abandoned);
        var restored = new SessionJournal(fixture.DirectoryPath).ModelOperations;
        Check(restored.Count == 2 && restored.Count(operation => operation.Phase == ModelOperationPhase.Abandoned) == 1, "Утеряна отмена в смешанном журнале.");
        return Task.CompletedTask;
    }

    public static Task PartialTailAndDuplicate()
    {
        using var fixture = new Fixture();
        fixture.Advance(ModelOperationPhase.RequestSentUnknown);
        fixture.Advance(ModelOperationPhase.RemoteAccepted);
        fixture.Advance(ModelOperationPhase.ResultReceived, fixture.Result);
        fixture.Advance(ModelOperationPhase.Completed);
        string[] lines = File.ReadAllLines(fixture.PathName);
        for (int row = 1; row < lines.Length; row++)
        {
            foreach (int length in new[] { 1, lines[row].Length / 2, lines[row].Length })
            {
                string directory = Path.Combine(fixture.DirectoryPath, $"partial-{row}-{length}"); Directory.CreateDirectory(directory);
                string prefix = string.Join("\n", lines.Take(row)) + "\n";
                string path = Path.Combine(directory, "model-operations.jsonl");
                File.WriteAllText(path, prefix + lines[row][..length], new UTF8Encoding(false));
                var restored = new SessionJournal(directory);
                Check(File.ReadAllText(path) == prefix && Directory.GetFiles(directory, "model-operations.partial-*").Length == 1,
                    "Оборванный переход не сохранён отдельно.");
                Check(Json(restored.ModelOperations.Single()) == Json(new SessionJournal(PrefixDirectory(row, prefix)).ModelOperations.Single()), "Подтверждена незавершённая строка.");
            }
        }
        File.AppendAllText(fixture.PathName, lines[^1] + "\n", new UTF8Encoding(false));
        fixture.Reopen(); Check(fixture.Current.Phase == ModelOperationPhase.Completed, "Повтор целой строки не идемпотентен.");
        return Task.CompletedTask;

        string PrefixDirectory(int row, string prefix)
        {
            string directory = Path.Combine(fixture.DirectoryPath, "prefix-" + row); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "model-operations.jsonl"), prefix, new UTF8Encoding(false)); return directory;
        }
    }

    public static Task CorruptionAndMissingPredecessor()
    {
        using var fixture = new Fixture();
        fixture.Advance(ModelOperationPhase.RequestSentUnknown); fixture.Advance(ModelOperationPhase.RemoteAccepted);
        fixture.Advance(ModelOperationPhase.ResultReceived, fixture.Result);
        string[] lines = File.ReadAllLines(fixture.PathName);
        foreach (string scenario in new[] { "no-base", "missing-change", "version", "hash", "result", "phase" })
        {
            string directory = Path.Combine(fixture.DirectoryPath, scenario); Directory.CreateDirectory(directory);
            var changed = lines.ToList();
            if (scenario == "no-base") changed.RemoveAt(0);
            else if (scenario == "missing-change") changed.RemoveAt(1);
            else
            {
                var node = JsonNode.Parse(changed[^1])!;
                if (scenario == "version") node["JournalVersion"] = 99;
                if (scenario == "hash") node["PreviousHash"] = "wrong";
                if (scenario == "phase") node["Phase"] = 99;
                if (scenario == "result") node["Result"]!["DisplayText"] = "Подменённая цитата";
                changed[^1] = node.ToJsonString();
            }
            string path = Path.Combine(directory, "model-operations.jsonl");
            File.WriteAllText(path, string.Join("\n", changed) + "\n", new UTF8Encoding(false));
            byte[] before = File.ReadAllBytes(path); MustReject(directory);
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Отказ чтения изменил целые строки.");
        }
        return Task.CompletedTask;
    }

    public static Task WriteFailureAndSize()
    {
        using var fixture = new Fixture(new string('ф', 16000));
        string before = Json(fixture.Current);
        using (File.Open(fixture.PathName, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            bool rejected = false;
            try { fixture.Advance(ModelOperationPhase.RequestSentUnknown); }
            catch (IOException) { rejected = true; }
            Check(rejected && Json(fixture.Current) == before, "Отказ append изменил память.");
        }
        var snapshots = new List<string> { Json(fixture.Current) };
        foreach (var phase in new[] { ModelOperationPhase.RequestSentUnknown, ModelOperationPhase.RemoteAccepted, ModelOperationPhase.ResultReceived, ModelOperationPhase.Completed })
        {
            fixture.Advance(phase, phase == ModelOperationPhase.ResultReceived ? fixture.Result : null); snapshots.Add(Json(fixture.Current));
        }
        fixture.Reopen();
        long legacySize = Encoding.UTF8.GetByteCount(string.Join("\n", snapshots) + "\n");
        Check(new FileInfo(fixture.PathName).Length < legacySize * .6 && fixture.Current.Phase == ModelOperationPhase.Completed,
            "Переходы не уменьшили повторение большого состояния или не восстановились.");
        return Task.CompletedTask;
    }
}
