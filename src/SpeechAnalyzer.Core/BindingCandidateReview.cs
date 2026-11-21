namespace SpeechAnalyzer.Core;

// Любое редактирование кандидата аннулирует даже ещё не завершившееся чтение.
public sealed class BindingCandidateReview
{
    private long revision;
    public ChatBindingEvidence? Evidence { get; private set; }
    public long Invalidate() { Evidence = null; return ++revision; }
    public bool Complete(long startedRevision, ChatBindingEvidence evidence)
    {
        if (startedRevision != revision) return false;
        Evidence = evidence; return true;
    }
}
