namespace SpeechAnalyzer.Core;

// Планировщик только запрашивает checkpoint; запись выполняет очередь SessionCoordinator.
public interface ICaptureCheckpointScheduler
{
    IDisposable Start(Func<Task> checkpoint);
}

public sealed class CaptureCheckpointScheduler : ICaptureCheckpointScheduler
{
    public IDisposable Start(Func<Task> checkpoint) => new Timer(
        state => { _ = checkpoint(); }, null,
        TimeSpan.FromMilliseconds(CaptureDuration.CheckpointIntervalMs),
        TimeSpan.FromMilliseconds(CaptureDuration.CheckpointIntervalMs));
}
