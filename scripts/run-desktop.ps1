$ErrorActionPreference = 'Stop'
$desktopProject = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/SpeechAnalyzer.Desktop/SpeechAnalyzer.Desktop.csproj'
dotnet run --project $desktopProject
exit $LASTEXITCODE
