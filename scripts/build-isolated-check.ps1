#requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('ArtifactIsolationChecks','RecorderFilesystemChecks','RequestRecoveryChecks','RequestRegressionChecks','RecorderStallChecks')][string]$Runner,
    [Parameter(Mandatory)][string]$BinaryDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedCoreSha256,
    [Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$binary=(Resolve-Path -LiteralPath $BinaryDirectory).Path
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $evidence){throw 'Existing run rejected before output mutation.'}
if((Get-FileHash -LiteralPath "$binary/SpeechAnalyzer.Core.dll").Hash -ne $ExpectedCoreSha256){throw 'Wrong candidate identity.'}
$null=New-Item -ItemType Directory -Path $evidence
$null=New-Item -ItemType Directory -Path "$evidence/empty-feed"
$null=New-Item -ItemType Directory -Path "$evidence/temp"
$priorTemp=$env:TEMP; $priorTmp=$env:TMP
try {
    $env:TEMP="$evidence/temp"; $env:TMP="$evidence/temp"
    $project=Join-Path $root "tools/$Runner/$Runner.csproj"
    $props=@("-p:StopBinaryDirectory=$binary","-p:BaseIntermediateOutputPath=$evidence/obj/","-p:OutputPath=$evidence/bin/",'-p:Configuration=Release')
    & dotnet restore $project @props --source "$evidence/empty-feed" -p:NuGetAudit=false *> "$evidence/restore.log"
    if($LASTEXITCODE){throw 'Offline restore failed; see preserved log.'}
    $metadata=& dotnet msbuild $project @props -t:ResolveReferences -getItem:ReferenceCopyLocalPaths,ProjectReference,Compile -verbosity:quiet
    if($LASTEXITCODE){throw 'Reference inspection failed.'}
    $metadata | Set-Content -LiteralPath "$evidence/references.json"
    $items=($metadata -join "`n" | ConvertFrom-Json).Items
    $refs=@($items.ReferenceCopyLocalPaths | Where-Object Extension -eq '.dll')
    if(@($items.ProjectReference).Count -or $refs.Count -ne 1 -or $refs[0].FullPath -ne "$binary\SpeechAnalyzer.Core.dll"){throw 'Unexpected production references.'}
    [xml]$definition=Get-Content -LiteralPath $project -Raw
    $allow=@($definition.Project.ItemGroup.Compile | ForEach-Object {[IO.Path]::GetFullPath((Join-Path (Split-Path $project) $_.Include))})
    $allow+=[IO.Path]::GetFullPath("$evidence/obj/Release/net10.0/$Runner.GlobalUsings.g.cs")
    if(Compare-Object $allow @($items.Compile.FullPath)){throw 'Unexpected fixture sources.'}
    $items.Compile | ForEach-Object {[pscustomobject]@{path=$_.FullPath;sha256=(Get-FileHash -LiteralPath $_.FullPath).Hash}} | ConvertTo-Json | Set-Content -LiteralPath "$evidence/sources.json"
    & dotnet build $project @props --no-restore -p:UseSharedCompilation=false *> "$evidence/build.log"
    if($LASTEXITCODE){throw 'Fixture build failed; see preserved log.'}
    if((Get-FileHash -LiteralPath "$evidence/bin/SpeechAnalyzer.Core.dll").Hash -ne $ExpectedCoreSha256){throw 'Output Core mismatch.'}
    Write-Output "Built allowlisted $Runner against $ExpectedCoreSha256; no tests started."
} finally {$env:TEMP=$priorTemp; $env:TMP=$priorTmp}
