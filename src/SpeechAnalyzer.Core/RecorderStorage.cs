namespace SpeechAnalyzer.Core;

// Storage seam only: admission, ordering, conversion, indexing and rotation stay
// in TestSessionAudioRecorder. Supplying a sink bypasses filesystem recovery.
public interface IRecorderStorage
{
    Stream Open(string path);
    void Flush(Stream stream);
    void CommitPart(string temporaryPath, string finalPath);
}

internal sealed class FileRecorderStorage : IRecorderStorage
{
    public Stream Open(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
    public void Flush(Stream stream) => ((FileStream)stream).Flush(true);
    public void CommitPart(string temporaryPath, string finalPath) => File.Move(temporaryPath, finalPath);
}
