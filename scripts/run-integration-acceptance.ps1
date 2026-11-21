#requires -Version 7.0
param(
    [string]$WorkspaceRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedManifestSha256,
    [Parameter(Mandatory,ParameterSetName='Validate')][switch]$ValidateOnly,
    [Parameter(Mandatory,ParameterSetName='Launch')][switch]$Launch,
    [Parameter(Mandatory,ParameterSetName='Launch')][string]$SessionRoot,
    [Parameter(ParameterSetName='Launch')][switch]$Reopen,
    [ValidateRange(30,120)][int]$RollingSeconds = 30,
    [ValidateSet('v1','v2','v3','v4','v5')][string]$CandidateVersion = 'v1'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$packageId = "integration-acceptance-$CandidateVersion-20261121"
$package = Join-Path $root "artifacts/SpeechAnalyzer-$packageId"
$manifestPath = "$package-manifest.json"
if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne $ExpectedManifestSha256) { throw 'Candidate manifest SHA-256 mismatch.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$identity = Get-Content -LiteralPath "$package/acceptance-package.json" -Raw | ConvertFrom-Json
$flags = @('--gpu-stt-v1','--pre-gap-drain','--pcm16-floor-guard','--rolling-asr-v1')
$workflow = switch ($CandidateVersion) { 'v4' { 7 }; 'v5' { 8 }; default { 6 } }
if ($identity.packageId -ne $packageId -or $identity.workflow -ne $workflow -or
    ($workflow -ge 7 -and $identity.modelAdmission -ne 'observational') -or
    $identity.resultContract -ne 5 -or $identity.rollingScheduleVersion -ne 2 -or
    $identity.chunkSeconds -ne 30 -or $identity.defaultRollingSeconds -ne 30 -or $identity.asrInputVersion -ne 2 -or
    $identity.saveAudioDefault -ne $false -or ($identity.flags -join '|') -ne ($flags -join '|')) {
    throw 'Unexpected candidate contract.'
}
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$items = @(Get-ChildItem -LiteralPath $package -Recurse -Force -File)
if ($items.Count -ne $manifest.files.Count) { throw 'Unexpected package inventory.' }
foreach ($entry in $manifest.files) {
    $resolved = [IO.Path]::GetFullPath((Join-Path $package $entry.path))
    if (!$resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or !$seen.Add($resolved)) {
        throw 'Duplicate manifest entry or path outside package.'
    }
    if ((Get-Item -LiteralPath $resolved).Length -ne $entry.bytes -or
        (Get-FileHash -LiteralPath $resolved).Hash -ne $entry.sha256) { throw "Package file differs: $($entry.path)" }
}
$model = Join-Path $root 'models/ggml-large-v3.bin'
if ((Get-FileHash -LiteralPath $model).Hash -ne $identity.modelSha256) { throw 'Local Large V3 model SHA-256 mismatch.' }
Write-Output "Verified $($identity.packageId), $($items.Count) files, local Large V3, n=$RollingSeconds, first eligibility=$($RollingSeconds*2)s."
if ($ValidateOnly) { return }
if (Get-Process SpeechAnalyzer.WinUI -ErrorAction SilentlyContinue) { throw 'Close the stopped Speech Analyzer window before launching the acceptance candidate.' }
if (![IO.Path]::IsPathFullyQualified($SessionRoot)) { throw 'Specify an absolute isolated acceptance root.' }
$session = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($SessionRoot))
$marker = Join-Path $session 'candidate.txt'
if ($Reopen) {
    if ((Get-Item -LiteralPath $session -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "The acceptance root must be a local directory." }
    if ((Get-Content -LiteralPath $marker -Raw).Trim() -ne $ExpectedManifestSha256) { throw 'The session belongs to another candidate.' }
} else {
    $null = New-Item -ItemType Directory -Path $session
    $file = [IO.File]::Open($marker, [IO.FileMode]::CreateNew)
    try { $file.Write([Text.Encoding]::UTF8.GetBytes($ExpectedManifestSha256.ToUpperInvariant())) } finally { $file.Dispose() }
}
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $package 'SpeechAnalyzer.WinUI.exe'))
$start.UseShellExecute = $false
$start.WorkingDirectory = $package
foreach ($flag in $flags) { $start.ArgumentList.Add($flag) }
foreach ($key in @($start.Environment.Keys | Where-Object { $_ -like 'SPEECH_ANALYZER_*' })) {
    $null = $start.Environment.Remove($key)
}
$null = $start.Environment.Remove('MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY')
$start.Environment['SPEECH_ANALYZER_ACCEPTANCE_ROOT'] = $session
$start.Environment['SPEECH_ANALYZER_CHANNEL_GAPS'] = '1'
$start.Environment['SPEECH_ANALYZER_ROLLING_SECONDS'] = [string]$RollingSeconds
$start.Environment['SPEECH_ANALYZER_STT_MODEL'] = $model
$start.Environment['SPEECH_ANALYZER_STT_PHASES'] = '1'
$start.Environment['SPEECH_ANALYZER_CAPTURE_MEASUREMENT'] = '1'
$start.Environment['SPEECH_ANALYZER_MEASUREMENT_PACKAGE_ID'] = $identity.packageId
$start.Environment['SPEECH_ANALYZER_MEASUREMENT_MANIFEST_SHA256'] = $ExpectedManifestSha256
$start.Environment['SPEECH_ANALYZER_STARTUP_DIAGNOSTIC'] = Join-Path $session ("startup-" + [Guid]::NewGuid().ToString('N') + '.txt')
$start.Environment['SPEECH_ANALYZER_METRICS_PATH'] = Join-Path $session ("metrics-" + [Guid]::NewGuid().ToString('N') + '.jsonl')
$process = [Diagnostics.Process]::Start($start)
try { Write-Output "App PID=$($process.Id). Capture starts through an explicit user action." } finally { $process.Dispose() }
