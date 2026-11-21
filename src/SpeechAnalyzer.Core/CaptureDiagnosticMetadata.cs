namespace SpeechAnalyzer.Core;

public sealed record CaptureAudioFormat(int SampleRate,int Channels,int BitsPerSample,int BlockAlign,string Encoding);
public sealed record CaptureFormatMetadata(CaptureAudioFormat Requested,CaptureAudioFormat Microphone,CaptureAudioFormat Other,
    int BufferMs=50,string Source="selected_process_tree",CaptureAudioFormat? EndpointMixFormat=null,
    string DevicePositionUnits="unverified; do not assume output 16kHz samples",
    string ConversionConfiguration="microphone shared AutoConvertPcm/SrcDefaultQuality; process loopback explicit PCM (pinned NAudio source)",
    bool NativeConversionMeasured=false);
public sealed record EmptyCaptureCallback(AudioChannel Channel,long Qpc100ns,long DevicePosition,long ArrivalQpc100ns,int Flags);

// Сохраняются первые8 пустых callback каждого канала и последние32; счётчики не зависят от диска.
public sealed class CaptureCallbackObservations(AudioDiagnosticJournal? diagnostics)
{
    private readonly object gate=new();
    private readonly long[] counts=new long[2];
    private readonly Queue<EmptyCaptureCallback> recent=new();
    public void Reset(){lock(gate){Array.Clear(counts);recent.Clear();}}
    public void Observe(EmptyCaptureCallback observation)
    {
        lock(gate)
        {
            counts[(int)observation.Channel]++;
            if(recent.Count==32)recent.Dequeue();recent.Enqueue(observation);
            if(counts[(int)observation.Channel]<=8)diagnostics?.TryWrite("empty_callback",observation);
        }
    }
    public void WriteSummary(string phase)
    {
        lock(gate)diagnostics?.TryWrite("empty_callback_summary",new{phase,counts=counts.ToArray(),recent=recent.ToArray(),recentLimit=32});
    }
}
