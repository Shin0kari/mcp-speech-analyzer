$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    dotnet build SpeechAnalyzer.sln --ignore-failed-sources --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project tests/SpeechAnalyzer.Core.Tests/SpeechAnalyzer.Core.Tests.csproj --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    foreach ($scriptName in @('codex-rpc.mjs', 'probe-codex.mjs', 'verify-codex-session.mjs')) {
        node --check (Join-Path $PSScriptRoot $scriptName)
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally { Pop-Location }
