using System.Diagnostics;

namespace SpeechAnalyzer.Core;

public enum AudioChannel { You, Other }
public enum CaptureStatus { Stopped, Capturing, Paused }
public enum AnalysisMode { Manual, Automatic }
public enum OperationKind { Background, Automatic, Answer }
public enum QuestionStatus { Open, AnsweredByParticipant, Displayed, Obsolete }
public enum SegmentMeaning { Fact, Question, ParticipantAnswer, Commitment, ObsoleteQuestion }

public sealed record TranscriptSegment(Guid Id, AudioChannel Channel, long StartMs, long EndMs,
    DateTimeOffset CapturedAt, string Text, bool IsFinal, SegmentMeaning Meaning = SegmentMeaning.Fact,
    string? QuestionId = null);
public sealed record TrackedQuestion(string Id, string Text, QuestionStatus Status);
public sealed record ConversationState(long ProcessedThroughMs, IReadOnlyList<string> Facts,
    IReadOnlyList<string> Commitments, IReadOnlyList<TrackedQuestion> Questions)
{
    public static ConversationState Empty => new(0, [], [], []);
}
public sealed record ChatMessage(Guid OperationId, DateTimeOffset CreatedAt, string Text, string? UserInstruction);
public sealed record StoreSnapshot(ConversationState State, IReadOnlyList<ChatMessage> Messages);
public sealed record ProcessingRequest(Guid OperationId, OperationKind Kind, long BoundaryMs,
    ConversationState Previous, IReadOnlyList<TranscriptSegment> Segments, string UserInstruction,
    string FallbackInstruction, string Context);
public sealed record ProcessingResult(ConversationState State, string? DisplayText);
public sealed record ProcessingCommit(Guid OperationId, ProcessingResult Result, ChatMessage? Message);
public sealed record EngineSnapshot(CaptureStatus Capture, AnalysisMode Mode, long CaptureBoundaryMs,
    long FinalizedThroughMs, long ProcessedThroughMs, TimeSpan? NextAnalysisIn, bool IsBusy,
    string Activity, string? Error, bool CanRetry, int PendingOperations, StoreSnapshot Conversation);

public interface IClock
{
    TimeSpan Now { get; }
    DateTimeOffset UtcNow { get; }
}
public sealed class SystemClock : IClock
{
    private readonly long origin = Stopwatch.GetTimestamp();
    public TimeSpan Now => Stopwatch.GetElapsedTime(origin);
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
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
