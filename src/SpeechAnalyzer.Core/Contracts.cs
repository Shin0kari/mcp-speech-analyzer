using System.Diagnostics;
using System.Text.Json.Serialization;

namespace SpeechAnalyzer.Core;

public enum AudioChannel { You, Other }
public enum CaptureStatus { Stopped, Capturing, Paused }
public enum AnalysisMode { Manual, Automatic }
public enum OperationKind { Background, Automatic, Answer }
public enum QuestionStatus { Open, AnsweredByParticipant, Displayed, Obsolete }
public enum ResultOutcome { Summary, NoChange, FullAnswer, PartialAnswer, Clarification, Unusable }
public enum SegmentMeaning { Fact, Question, ParticipantAnswer, Commitment, ObsoleteQuestion }

public sealed record TranscriptSegment(Guid Id, AudioChannel Channel, long StartMs, long EndMs,
    DateTimeOffset CapturedAt, string Text, bool IsFinal, SegmentMeaning Meaning = SegmentMeaning.Fact,
    string? QuestionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecognitionEvidence? Recognition = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StartSample { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EndSample { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SttInputEvidence? SttInput { get; init; }
}
public sealed record TrackedQuestion(string Id, string Text, QuestionStatus Status);
public sealed record ConversationState(long ProcessedThroughMs, IReadOnlyList<string> Facts,
    IReadOnlyList<string> Commitments, IReadOnlyList<TrackedQuestion> Questions)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvidenceCursor? ConsumedEvidence { get; init; }
    public static ConversationState Empty => new(0, [], [], []);
}
public sealed record ChatMessage(Guid OperationId, DateTimeOffset CreatedAt, string Text, string? UserInstruction)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnresolvedPublicationKey { get; init; }
}
public sealed record StoreSnapshot(ConversationState State, IReadOnlyList<ChatMessage> Messages);
public sealed record ProcessingRequest(Guid OperationId, OperationKind Kind, long BoundaryMs,
    ConversationState Previous, IReadOnlyList<TranscriptSegment> Segments, string UserInstruction,
    string FallbackInstruction, string Context)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ChannelGap>? CaptureGaps { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RollingContextSeconds { get; init; }
}
public sealed record AnalysisSourceReference(string ItemId, string Text, IReadOnlyList<Guid> SourceSegmentIds)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<InterpretationQuote>? Evidence { get; init; }
}
public sealed record QuestionSourceReference(string QuestionId, IReadOnlyList<Guid> SourceSegmentIds)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<InterpretationQuote>? Evidence { get; init; }
}
public sealed record ProcessingResult(ConversationState State, string? DisplayText,
    IReadOnlyList<AnalysisSourceReference>? SourceReferences = null,
    IReadOnlyList<QuestionSourceReference>? QuestionReferences = null,
    IReadOnlyList<string>? DisplayedQuestionIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ContextualInterpretation>? Interpretations = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResultOutcome? Outcome { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ResultContractVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Limitations { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? OpenQuestionIds { get; init; }
}
public sealed record ProcessingCommit(Guid OperationId, ProcessingResult Result, ChatMessage? Message);
public sealed record EngineSnapshot(CaptureStatus Capture, AnalysisMode Mode, long CaptureBoundaryMs,
    long FinalizedThroughMs, long ProcessedThroughMs, TimeSpan? NextAnalysisIn, bool IsBusy,
    string Activity, string? Error, bool CanRetry, int PendingOperations, StoreSnapshot Conversation,
    bool CanAbandonFailedRequest = false);

public interface IClock
{
    TimeSpan Now { get; }
    DateTimeOffset UtcNow { get; }
}
public sealed class SystemClock : IPcmCaptureClock
{
    private readonly long origin = Stopwatch.GetTimestamp();
    public TimeSpan Now => Stopwatch.GetElapsedTime(origin);
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    // Ceil origin и запас1мс не делают исходный capture моложе из-за округления QPC.
    public TimeSpan FromPacketQpc(long qpc100ns) => TimeSpan.FromTicks(checked(qpc100ns - (long)Math.Ceiling(origin * (10_000_000m / Stopwatch.Frequency)) - 10_000));
}

public interface ITranscriptSource
{
    CaptureStatus Status { get; }
    long CaptureBoundaryMs { get; }
    long FinalizedThroughMs { get; }
    IReadOnlyList<TranscriptSegment> Segments { get; }
    void Start();
    void Pause();
    void Resume();
    void Stop();
    void Advance();
    // Фиксирует срез и закрывает текущие аудиоблоки: новые блоки начинаются после него.
    long SnapshotBoundary();
    // Источник обязан финализировать/разделить сегменты по границе в обоих каналах.
    // Более позднее аудио не должно менять уже завершённую часть расшифровки.
    Task WaitForFinalizationAsync(long boundaryMs, CancellationToken cancellationToken);
}
public interface IModelBackend
{
    Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken cancellationToken);
}
public interface IDurableModelBackend : IModelBackend
{
    Task<ProcessingResult> ProcessDurableAsync(ProcessingRequest request, ModelOperationVersions versions,
        Func<ModelOperationPhase, string?, Task> persistPhase, CancellationToken cancellationToken);
}
public interface IConversationStore
{
    StoreSnapshot Read();
    // Атомарно сохраняет состояние, видимый ответ и ID операции. Повтор ID идемпотентен.
    Task CommitAsync(ProcessingCommit commit, CancellationToken cancellationToken);
}

public sealed class MemoryConversationStore : IConversationStore
{
    private readonly object gate = new();
    private StoreSnapshot snapshot = new(ConversationState.Empty, []);
    private readonly HashSet<Guid> committed = [];
    public StoreSnapshot Read() { lock (gate) return snapshot; }
    public Task CommitAsync(ProcessingCommit commit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (committed.Contains(commit.OperationId)) return Task.CompletedTask;
            if (commit.Result.State.ProcessedThroughMs < snapshot.State.ProcessedThroughMs)
                throw new InvalidOperationException("Граница состояния не может двигаться назад.");
            snapshot = new(commit.Result.State, commit.Message is null
                ? snapshot.Messages : snapshot.Messages.Append(commit.Message).ToArray());
            committed.Add(commit.OperationId);
        }
        return Task.CompletedTask;
    }
}
