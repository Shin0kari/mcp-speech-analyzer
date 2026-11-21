#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$BinaryDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedCoreSha256,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedAudioSha256,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw 'Use a fresh evidence directory.' }
$names = @('SpeechAnalyzer.Core.dll', 'SpeechAnalyzer.Audio.Windows.dll')
if ((Get-FileHash -LiteralPath (Join-Path $binary $names[0])).Hash -ne $ExpectedCoreSha256 -or
    (Get-FileHash -LiteralPath (Join-Path $binary $names[1])).Hash -ne $ExpectedAudioSha256) {
    throw 'Candidate identity mismatch.'
}
$null = New-Item -ItemType Directory -Path "$evidence/feed", "$evidence/temp"
$sourceCommit = git -C $root rev-parse HEAD
if ($LASTEXITCODE -or (git -C $root status --porcelain --untracked-files=no)) { throw 'A clean source checkpoint is required.' }
$sourceCommit | Set-Content -LiteralPath "$evidence/source-commit.txt"
$project = Join-Path $root 'tools/RollingIntegrationChecks/RollingIntegrationChecks.csproj'
$properties = @("-p:CandidateDirectory=$binary", "-p:BaseIntermediateOutputPath=$evidence/obj/",
    "-p:OutputPath=$evidence/bin/", '-p:Configuration=Release')
$prior = @{}
foreach ($key in 'TEMP', 'TMP', 'ROLLING_EXPECTED_CORE', 'ROLLING_EXPECTED_AUDIO') {
    $prior[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    $env:TEMP = "$evidence/temp"; $env:TMP = "$evidence/temp"
    $env:ROLLING_EXPECTED_CORE = $ExpectedCoreSha256; $env:ROLLING_EXPECTED_AUDIO = $ExpectedAudioSha256
    & dotnet restore $project @properties -m:1 --source "$evidence/feed" -p:NuGetAudit=false *> "$evidence/restore.log"
    if ($LASTEXITCODE) { throw 'Offline fixture restore failed.' }
    $metadata = & dotnet msbuild $project @properties -m:1 -t:ResolveReferences -getItem:ReferenceCopyLocalPaths,ProjectReference,Compile -verbosity:quiet
    if ($LASTEXITCODE) { throw 'Cannot inspect fixture build inputs.' }
    $metadata | Set-Content -LiteralPath "$evidence/references.json"
    $items = ($metadata -join [Environment]::NewLine | ConvertFrom-Json).Items
    $allow = @('Program.cs', 'RollingCases.cs', 'AudioCases.cs', 'PackageDebugIdentity.cs', '../TestSupport/TestRunOutput.cs' |
        ForEach-Object { [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $project) $_)) })
    $allow += [IO.Path]::GetFullPath("$evidence/obj/Release/net10.0/RollingIntegrationChecks.GlobalUsings.g.cs")
    $dependencies = @($items.ReferenceCopyLocalPaths | Where-Object Extension -eq '.dll' | ForEach-Object FullPath)
    if ($items.ProjectReference.Count -or (Compare-Object $allow @($items.Compile.FullPath)) -or
        (Compare-Object @($names | ForEach-Object { Join-Path $binary $_ }) $dependencies)) {
        throw 'Fixture inputs are outside the explicit allowlist.'
    }
    & dotnet build $project @properties --no-restore -m:1 -p:UseSharedCompilation=false *> "$evidence/build.log"
    if ($LASTEXITCODE) { throw 'Fixture build failed.' }
    Get-ChildItem -LiteralPath "$evidence/bin" -File | Get-FileHash | ConvertTo-Json | Set-Content -LiteralPath "$evidence/hashes.json"
    & "$evidence/bin/RollingIntegrationChecks.exe" rolling --output-root "$evidence/scenarios" *> "$evidence/cases.log"
    if ($LASTEXITCODE) { throw 'Rolling integration fixture failed.' }
    $passed = @(Get-Content -LiteralPath "$evidence/cases.log" | Where-Object { $_ -like 'PASS *' })
    if ($passed.Count -ne 39 -or @($passed | Sort-Object -Unique).Count -ne 39) { throw 'Unexpected fixture case count.' }
    if (Get-ChildItem -LiteralPath $evidence -Recurse -File | Where-Object Name -Match '\.wav(?:\.partial)?$') { throw 'Unexpected WAV.' }
    Write-Output 'Rolling integration 39/39. Controlled clocks, fake decoder and fake model.'
} finally {
    foreach ($key in $prior.Keys) { [Environment]::SetEnvironmentVariable($key, $prior[$key]) }
}
