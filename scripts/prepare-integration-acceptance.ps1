#requires -Version 7.0
param(
    [string]$WorkspaceRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$BuildReceipt,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [ValidateSet('v1','v2','v3','v4','v5')][string]$CandidateVersion = 'v1'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$package = [IO.Path]::GetFullPath($PackageDirectory)
if (![IO.Path]::IsPathFullyQualified($PackageDirectory) -or (Test-Path -LiteralPath $package) -or
    (Test-Path -LiteralPath "$package-manifest.json")) { throw 'Use a fresh absolute package directory.' }
$source = (Get-Content -LiteralPath $BuildReceipt -Raw).Trim()
git -C "$PSScriptRoot/.." cat-file -e "$source^{commit}"
if ($LASTEXITCODE -or $source -notmatch '^[0-9a-f]{40}$') { throw 'Invalid build checkpoint.' }
$baseline = Join-Path $root 'artifacts/SpeechAnalyzer-rolling-asr-2n-v1-20261112'
$baselineManifestHash = 'C152553253DEB2AB4CBEDE86D6D5597C5BE03E8DA2D7FACDD6A622AEA7930F0C'
if ((Get-FileHash -LiteralPath "$baseline-manifest.json").Hash -ne $baselineManifestHash) {
    throw 'The preserved dependency manifest differs.'
}
$baselineManifest = Get-Content -LiteralPath "$baseline-manifest.json" -Raw | ConvertFrom-Json
$inherited = @($baselineManifest.files | Where-Object {
    $_.path -match '^(cuda-bin\\|(?:stt-worker\\)?runtimes\\cuda\\win-x64\\|mcp\\speech-analyzer-mcp\.exe$|NAudio-LICENSE\.txt$)'
})
if ($inherited.Count -ne 15) { throw 'Unexpected inherited dependency inventory.' }
foreach ($file in $inherited) {
    if ((Get-FileHash -LiteralPath (Join-Path $baseline $file.path)).Hash -ne $file.sha256 -or
        (Test-Path -LiteralPath (Join-Path $publish $file.path))) { throw "Conflicting dependency: $($file.path)" }
}
foreach ($file in 'SpeechAnalyzer.Core.dll', 'SpeechAnalyzer.Audio.Windows.dll', 'NAudio.Wasapi.dll') {
    if ((Get-FileHash -LiteralPath "$publish/$file").Hash -ne (Get-FileHash -LiteralPath "$publish/stt-worker/$file").Hash) {
        throw "Published app and worker differ: $file"
    }
}
foreach ($file in 'SpeechAnalyzer.WinUI.exe','SpeechAnalyzer.WinUI.dll','App.xbf','MainWindow.xbf',
    'SpeechAnalyzer.WinUI.pri','stt-worker/SttWorker.exe','stt-worker/SttWorker.dll') {
    if (!(Test-Path -LiteralPath "$publish/$file" -PathType Leaf)) { throw "Missing exact publish output: $file" }
}
$null = New-Item -ItemType Directory -Path $package
Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $package -Recurse
foreach ($file in $inherited) {
    $destination = Join-Path $package $file.path
    $null = [IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    Copy-Item -LiteralPath (Join-Path $baseline $file.path) -Destination $destination
}
$identity = [ordered]@{
    packageId = "integration-acceptance-$CandidateVersion-20261121"
    sourceCommit = $source
    dependencyManifestSha256 = $baselineManifestHash
    workflow = switch ($CandidateVersion) { 'v4' { 7 }; 'v5' { 8 }; default { 6 } }
    resultContract = 5
    asrInputVersion = 2
    chunkSeconds = 30
    rollingScheduleVersion = 2
    defaultRollingSeconds = 30
    flags = @('--gpu-stt-v1','--pre-gap-drain','--pcm16-floor-guard','--rolling-asr-v1')
    modelSha256 = '64D182B440B98D5203C4F9BD541544D84C605196C4F7B845DFA11FB23594D1E2'
    saveAudioDefault = $false
}
if ($identity.workflow -ge 7) { $identity.modelAdmission = 'observational' }
$identity | ConvertTo-Json | Set-Content -LiteralPath "$package/acceptance-package.json"
$files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ path=[IO.Path]::GetRelativePath($package,$_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName).Hash; bytes=$_.Length }
})
@{files=$files} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$package-manifest.json"
Write-Output "Packaged $($files.Count) files from $source. Added $($inherited.Count) independently pinned dependencies and license."
