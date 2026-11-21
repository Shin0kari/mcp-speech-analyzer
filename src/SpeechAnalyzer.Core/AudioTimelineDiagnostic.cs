namespace SpeechAnalyzer.Core;

// Геометрия, флаги и пиковый уровень пакетов. Текст и PCM в диагностику не входят.
public sealed record AudioPacketTiming(long Sequence, AudioChannel Channel, long Qpc100ns,
    long DevicePosition, int Samples, long RawStart, long ExpectedBefore, long DeliveredBefore,
    bool Discontinuity, bool TimestampError, long RequestedMs, double ArrivedMs,
    bool Silent = false, float Peak = 0);

public sealed record AudioTimelineDiagnostic(int Version, string Reason, long OriginQpc100ns,
    long OriginSample, long PacketsSeen, AudioPacketTiming[] FirstPackets, AudioPacketTiming[] RecentPackets);
