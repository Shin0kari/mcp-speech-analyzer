using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

// Домен часов не сохраняется как разрешение повторного inference после рестарта.
public interface IPcmCaptureClock : IClock
{
    TimeSpan FromPacketQpc(long qpc100ns);
}
public sealed record PcmRangeProof(long Start, long End, long PacketQpc100ns, TimeSpan Born, TimeSpan Expires, bool Trusted);
public sealed record SttJobDescriptor(Guid JobId, AudioChannel Channel, Guid Generation, long Start, long End,
    int Samples, string InputSha256, ImmutableArray<PcmRangeProof> Provenance)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? CandidateInput { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? DecoderStart { get; init; }
    public TimeSpan Expires => Provenance.IsDefaultOrEmpty ? TimeSpan.MinValue : Provenance.Min(p => p.Expires);
    public bool Valid(Guid generation, long fence, TimeSpan now)
    {
        long inputStart = DecoderStart ?? Start;
        if (inputStart < 0 || inputStart > Start || Start >= End || DecoderStart.HasValue &&
            (CandidateInput != true || Start - inputStart > 4 * BufferedTranscriptSource.SampleRate
                || End - Start > 2 * BufferedTranscriptSource.SampleRate)) return false;
        if (Generation != generation || JobId == Guid.Empty || Samples < 1 || Samples > (CandidateInput == true ? SttInputLimits.MaxSamples : SttInputLimits.LegacyMaxSamples) || End - inputStart != Samples
            || Start < 0 || End > fence || InputSha256.Length != 64 || Provenance.IsDefaultOrEmpty || Provenance.Length > (CandidateInput == true ? SttInputLimits.ProofCapacity : SttInputLimits.LegacyProofCapacity)) return false;
        long cursor = inputStart;
        foreach (var p in Provenance)
        {
            if (!p.Trusted || p.Start != cursor || p.End <= p.Start || p.End > End || p.Born > now
                || p.Expires - p.Born != TimeSpan.FromSeconds(CandidateInput == true ? SttInputLimits.PcmLifetimeSeconds : SttInputLimits.LegacyPcmLifetimeSeconds) || p.Expires <= now) return false;
            cursor = p.End;
        }
        return cursor == End;
    }
}
public enum SttDrainPhase { Open, Draining, Drained, Failed, Stopped, Interrupted }
public enum SttJobFate { Pending, Finalized, Empty, Ineligible, Expired, Interrupted }
public sealed record SttUnknownInterval(int Version, Guid Generation, AudioChannel Channel, long Start, long End,
    long Fence, string Reason);
public sealed record SttRecoveryJob(SttJobDescriptor Descriptor, SttJobFate Fate);
public sealed record PcmInputClassification(string Classifier, PcmInputClass InputClass, SttJobDescriptor Descriptor);
public sealed record SttRecoverySnapshot(int Version, Guid Generation, SttDrainPhase Phase, SttUnknownInterval? Unknown,
    long ConfirmedThroughMs, long[] IncompleteFrom, SttRecoveryJob[] Jobs, string? TranscriptFingerprint = null,
    TranscriptStopStatus? Completion = null)
{
    public ContinuityCheckpoint? Continuity { get; init; }
}
public interface ISttRecoveryStore
{
    // Adapters for the same underlying store must return equal stable identities.
    object PersistenceIdentity => this;
    string TranscriptFingerprint => "";
    SttRecoverySnapshot? ReadSttRecovery();
    void SaveSttRecovery(SttRecoverySnapshot snapshot);
    // До подтверждения coverage; store без поддержки не может молча подтвердить пропуск.
    void SaveInputClassification(PcmInputClassification decision) => throw new IOException("Store не поддерживает provenance пропуска PCM.");
}
// Контракт bounded cancellation: переданный token ограничивает startup, input и ожидание результата.
// Native allocations этим интерфейсом не аттестуются.
public interface IExpiringAudioTranscriber : ISttDiagnosticTranscriber
{
    Task<string> TranscribeExpiringAsync(float[] samples, SttJobDescriptor descriptor, IClock clock, CancellationToken cancellationToken, SttJobDiagnostics? diagnostics);
}

public sealed partial class SessionJournal : ISttRecoveryStore
{
    public object PersistenceIdentity => Path.GetFullPath(DirectoryPath).ToUpperInvariant();
    public void SaveInputClassification(PcmInputClassification decision)
    {
        lock (gate)
        {
            if (decision.Classifier != PcmSilence.ClassifierVersion || decision.InputClass == PcmInputClass.Uncertain
                || decision.Descriptor.Provenance.Length is < 1 or > SttInputLimits.ProofCapacity)
                throw new IOException("Недостоверное решение о пропуске PCM.");
            // Отдельная неизменяемая bounded запись нужна: recovery snapshot удаляет завершённые jobs.
            // Запись выполняет STT worker, никогда не capture callback.
            string directory = Path.Combine(DirectoryPath, "pcm-classifications");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, decision.Descriptor.JobId.ToString("N") + ".json");
            string text = JsonSerializer.Serialize(decision);
            if (File.Exists(path))
            {
                if (File.ReadAllText(path) != text) throw new IOException("Конфликт решения PCM.");
                return;
            }
            try { WriteAtomic(path, text); }
            catch when (File.Exists(path) && File.ReadAllText(path) == text) { }
        }
    }
    public SttRecoverySnapshot? ReadSttRecovery()
    {
        lock (gate)
        {
            string path = Path.Combine(DirectoryPath, "stt-recovery.json");
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("Превышен размер recovery snapshot.");
            var value = JsonSerializer.Deserialize<SttRecoverySnapshot>(File.ReadAllText(path)) ?? throw new IOException("Пустой recovery snapshot.");
            if (value.Version is not (1 or 2) || value.Jobs.Length > 9 || value.IncompleteFrom.Length != 2
                || value.Jobs.Any(j => j.Descriptor.Provenance.Length > SttInputLimits.ProofCapacity)) throw new IOException("Неверный recovery snapshot.");
            return value;
        }
    }
    public void SaveSttRecovery(SttRecoverySnapshot snapshot)
    {
        using (CaptureTiming.Lock(gate, CaptureTimingKind.RecoveryLock))
        {
            if (snapshot.Version is not (1 or 2) || snapshot.Jobs.Length > 9) throw new IOException("Превышен recovery snapshot.");
            string path = Path.Combine(DirectoryPath, "stt-recovery.json"), text;
            using (CaptureTiming.Phase(CaptureTimingKind.RecoverySerialize))
                text = JsonSerializer.Serialize(snapshot);
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 8 * 1024 * 1024) throw new IOException("Превышен размер recovery snapshot.");
            // Потерянный ACK атомарной записи сверяется по полному принятому значению.
            try { using var writeTiming = CaptureTiming.Phase(CaptureTimingKind.RecoveryWrite); WriteAtomic(path, text); }
            catch when (File.Exists(path) && File.ReadAllText(path) == text) { }
        }
    }
}
