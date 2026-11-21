using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed record BindingResolution(Guid CreationId, string WorkingDirectory, string? AcceptedThreadId, DateTimeOffset DecidedAt,
    ChatBindingCandidate? Evidence = null);
public sealed record ChatBindingCandidate(string ThreadId, string WorkingDirectory, string? Name, string? Model, int Turns, bool HistoryComplete);
public sealed record ChatBindingEvidence(Guid CreationId, ChatBindingCandidate Candidate);
public sealed record CodexThreadBindingState(int Version, Guid CreationId, string WorkingDirectory, bool CreationPending, string? ThreadId,
    IReadOnlyList<BindingResolution>? Resolutions = null);

// Уникальный cwd связывает намерение создания с метаданными кандидата при явной сверке.
public sealed class CodexThreadBinding
{
    private readonly string path;
    private readonly string root;
    private readonly Action<string, CodexThreadBindingState> save;
    public CodexThreadBindingState? State { get; private set; }
    public CodexThreadBinding(string sessionDirectory, Action<string, CodexThreadBindingState>? save = null)
    {
        root = Path.GetFullPath(sessionDirectory); path = Path.Combine(root, "codex-binding.json");
        this.save = save ?? AudioSelectionFile.SaveJson;
        if (File.Exists(path))
        {
            State = JsonSerializer.Deserialize<CodexThreadBindingState>(File.ReadAllText(path)) ?? throw new IOException("Пустая привязка чата.");
            if (State.Version != 1 || string.IsNullOrWhiteSpace(State.WorkingDirectory)) throw new IOException("Неверная версия привязки чата.");
        }
    }
    private void Persist(CodexThreadBindingState next) { save(path, next); State = next; }
    public void Reset()
    {
        if (State?.CreationPending == true) throw new InvalidOperationException("Сначала сверьте незавершённое создание рабочего чата.");
        var id = Guid.NewGuid();
        Persist(new(1, id, Path.Combine(root, "codex-workspace-" + id.ToString("N")), false, null, State?.Resolutions));
    }
    public ChatBindingEvidence ValidateCandidate(string requestedId, ChatBindingCandidate candidate)
    {
        var state = State ?? throw new InvalidOperationException("Нет намерения создания чата для сверки.");
        if (!state.CreationPending || state.ThreadId is not null) throw new InvalidOperationException("Неопределённая привязка отсутствует.");
        if (string.IsNullOrWhiteSpace(requestedId) || candidate.ThreadId != requestedId || !candidate.HistoryComplete
            || !string.Equals(candidate.WorkingDirectory, state.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
            || candidate.Model is { } model && model != CodexModelSettings.Model
            || candidate.Name is { Length: > 0 } name && name != CodexModelSettings.ThreadName(requestedId))
            throw new IOException("Соответствие чата сессии не подтверждено. Неопределённая привязка сохранена.");
        return new(state.CreationId, candidate);
    }
    public void AcceptCandidate(ChatBindingEvidence evidence)
    {
        if (State?.CreationId != evidence.CreationId) throw new InvalidOperationException("Сведения сверки устарели.");
        ValidateCandidate(evidence.Candidate.ThreadId, evidence.Candidate);
        Persist(State! with { CreationPending = false, ThreadId = evidence.Candidate.ThreadId,
            Resolutions = [.. State!.Resolutions ?? [], new(State.CreationId, State.WorkingDirectory, evidence.Candidate.ThreadId, DateTimeOffset.UtcNow, evidence.Candidate)] });
    }
    // Только отдельное явное решение UI. Неизвестный прежний исход остаётся в том же атомарном файле.
    public void ReplaceUncertainCreation()
    {
        if (State is not { CreationPending: true } state) throw new InvalidOperationException("Нет неопределённого создания.");
        var id = Guid.NewGuid();
        Persist(new(1, id, Path.Combine(root, "codex-workspace-" + id.ToString("N")), false, null,
            [.. state.Resolutions ?? [], new(state.CreationId, state.WorkingDirectory, null, DateTimeOffset.UtcNow)]));
    }
    public async Task<string> ResolveAsync(string? legacyThreadId, Func<string, Task<string>> create,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State?.ThreadId is { Length: > 0 } existing) return existing;
        if (State is null) Reset();
        if (legacyThreadId is { Length: > 0 } legacy && !State!.CreationPending)
        { Persist(State with { ThreadId = legacy }); return legacy; }
        if (State!.CreationPending)
        {
            throw new IOException("Требуется сверка создания чата: укажите известный ID и явно примите проверенную привязку. Автопоиск и повторное создание не выполняются.");
        }
        Directory.CreateDirectory(State.WorkingDirectory);
        Persist(State with { CreationPending = true }); // До отправки, включая окно потери удалённого ID.
        string created = await create(State.WorkingDirectory);
        if (string.IsNullOrWhiteSpace(created)) throw new IOException("Codex не вернул ID созданного чата.");
        Persist(State with { CreationPending = false, ThreadId = created });
        return created;
    }
}
