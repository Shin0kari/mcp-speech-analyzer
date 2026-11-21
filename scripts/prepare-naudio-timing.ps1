#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$work=Join-Path $root 'artifacts/capture-measurement-20261103'
$pin=Join-Path $work 'upstream/NAudio-4f8889e2bd34c3978a25652c6c172fa1502ccba3/src/NAudio.Wasapi'
$patched=Join-Path $work 'patched'
New-Item -ItemType Directory -Force $patched | Out-Null
function Replace-Required([string]$source,[string]$old,[string]$new) {
    if(!$source.Contains($old)){throw "Не найден фрагмент exact pin: $old"}
    return $source.Replace($old,$new)
}
$recorder=(Get-Content -LiteralPath (Join-Path $pin 'WasapiRecorder.cs') -Raw).Replace("`r`n","`n")
$recorder="using SpeechAnalyzer.Core;`n"+$recorder
$recorder=Replace-Required $recorder '    private void CaptureThread()' @'
    // Наблюдатель задаётся до Start; запись и native lease алгоритм не меняются.
    public CaptureTimingLane? TimingLane { get; set; }
    private void CaptureThread()
'@
$recorder=Replace-Required $recorder "    private void CaptureThread()`n    {" "    private void CaptureThread()`n    {`n        using var measurementBinding = CaptureTiming.Bind(TimingLane);"
Set-Content -LiteralPath (Join-Path $patched 'WasapiRecorder.cs') -Value $recorder -NoNewline
$client=(Get-Content -LiteralPath (Join-Path $pin 'CoreAudioApi/AudioCaptureClient.cs') -Raw).Replace("`r`n","`n")
$client="using SpeechAnalyzer.Core;`n"+$client
$old=@'
        CoreAudioException.ThrowIfFailed(CaptureClient.GetBuffer(
            out var bufferPointer, out var numFramesToRead, out var bufferFlags,
            out var devicePosition, out var qpcPosition));
        return new CaptureBufferLease(this, bufferPointer, numFramesToRead, bytesPerFrame, bufferFlags, devicePosition, qpcPosition);
'@
$new=@'
        long entry = CaptureTiming.Now;
        IntPtr bufferPointer; int numFramesToRead; AudioClientBufferFlags bufferFlags;
        long devicePosition, qpcPosition; int outcome;
        try { outcome = CaptureClient.GetBuffer(out bufferPointer, out numFramesToRead, out bufferFlags, out devicePosition, out qpcPosition); }
        catch (Exception failure) { CaptureTiming.Current?.Acquired(entry, CaptureTiming.Now, 0, 0, 0, 0, 0, failure.HResult); throw; }
        long returned = CaptureTiming.Now;
        CaptureTiming.Current?.Acquired(entry, returned, numFramesToRead, numFramesToRead * bytesPerFrame, (int)bufferFlags, devicePosition, qpcPosition, outcome);
        CoreAudioException.ThrowIfFailed(outcome);
        return new CaptureBufferLease(this, bufferPointer, numFramesToRead, bytesPerFrame, bufferFlags, devicePosition, qpcPosition);
'@
$client=Replace-Required $client $old $new
$client=Replace-Required $client @'
        CoreAudioException.ThrowIfFailed(CaptureClient.GetNextPacketSize(out var numFramesInNextPacket));
        return numFramesInNextPacket;
'@ @'
        long entry = CaptureTiming.Now; int count, outcome;
        try { outcome = CaptureClient.GetNextPacketSize(out count); }
        catch (Exception failure) { CaptureTiming.Current?.Write(CaptureTimingKind.NextPacketSize, entry, CaptureTiming.Now, failure.HResult); throw; }
        CaptureTiming.Current?.Write(CaptureTimingKind.NextPacketSize, entry, CaptureTiming.Now, outcome, count);
        CoreAudioException.ThrowIfFailed(outcome);
        return count;
'@
$client=Replace-Required $client '        CoreAudioException.ThrowIfFailed(CaptureClient.ReleaseBuffer(numFramesRead));' @'
        long entry = CaptureTiming.Now; int outcome;
        try { outcome = CaptureClient.ReleaseBuffer(numFramesRead); }
        catch (Exception failure) { CaptureTiming.Current?.Write(CaptureTimingKind.Release, entry, CaptureTiming.Now, failure.HResult, numFramesRead); throw; }
        CaptureTiming.Current?.Write(CaptureTimingKind.Release, entry, CaptureTiming.Now, outcome, numFramesRead);
        CoreAudioException.ThrowIfFailed(outcome);
'@
Set-Content -LiteralPath (Join-Path $patched 'AudioCaptureClient.cs') -Value $client -NoNewline
# Патч сохраняется отдельно от pristine archive и может быть просмотрен без сборки.
$diff=@(); foreach($name in @('WasapiRecorder.cs','AudioCaptureClient.cs')) {
    $original=if($name -eq 'AudioCaptureClient.cs'){Join-Path $pin ('CoreAudioApi/'+$name)}else{Join-Path $pin $name}
    $diff+= & git diff --no-index -- $original (Join-Path $patched $name)
    if($LASTEXITCODE -gt 1){throw 'Не удалось сохранить patch'}
}
$diff | Set-Content -LiteralPath (Join-Path $work 'naudio-timing.patch')
Write-Host 'Exact pin подготовлен; только два файла инструментированы, capture не запускается.'
