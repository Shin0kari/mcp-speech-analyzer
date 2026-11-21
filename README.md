# Speech Analyzer

Speech Analyzer is a Windows WinUI 3 application for Russian conversation transcription and contextual answers through an owned Codex app-server over stdio. It keeps the selected microphone (`You`) and selected application process tree (`Other`) separate. The Rust MCP server forwards tools to the running application's local named pipe. Opening the window or connecting MCP leaves capture stopped.

## Build from source

Use Windows x64, PowerShell 7, .NET SDK 10.0.401 and the Windows SDK and WinUI packages pinned by the projects. This snapshot targets Windows build 20348 or newer. Use Rust 1.88 or newer with a Windows linker matching the Rust target. Node.js is needed for the MCP stdio smoke check.

```powershell
dotnet publish src/SpeechAnalyzer.WinUI/SpeechAnalyzer.WinUI.csproj -c Release --self-contained false
cargo check --locked --manifest-path mcp/Cargo.toml
cargo build --locked --manifest-path mcp/Cargo.toml
node scripts/verify-mcp.mjs
```

The publish output contains the WinUI app and the owned transcription worker. Run the app from its publish directory. It requires a local Whisper model for recognition and an independently installed and authenticated Codex CLI with access to GPT-6.1 Terra for live answers. Configure the local model in the application or set `SPEECH_ANALYZER_STT_MODEL` to its path. Audio retention is configurable and off by default. Conversations live under `Documents/Speech Analyzer/Conversations` by default.

The historical v5 acceptance package adds optional capture measurement with the [pinned NAudio source](https://github.com/naudio/NAudio/commit/4f8889e2bd34c3978a25652c6c172fa1502ccba3). Extract the upstream tree to `artifacts/capture-measurement-20261103/upstream/NAudio-4f8889e2bd34c3978a25652c6c172fa1502ccba3` and run `scripts/prepare-naudio-timing.ps1` in a fresh checkout to generate the two instrumented source files. Publish with `-p:CaptureMeasurement=true` for that variant. The corresponding native Whisper CUDA runtime, CUDA libraries and local Large V3 weights must be provided separately. NAudio's license is preserved in [third-party/NAudio-LICENSE.txt](third-party/NAudio-LICENSE.txt). The acceptance packager reuses a hash-pinned historical dependency package from explicit local inputs. Ordinary source builds use the commands above.

## Verification

```powershell
dotnet run --project tests/SpeechAnalyzer.Core.Tests/SpeechAnalyzer.Core.Tests.csproj -c Release -- offline-no-audio
```

The broad suite has two known failures, `BoundedEvents` and `InvalidModelResult`. See the [dated verification summary](docs/verified-desktop-mcp-2026-11-21.en.md) for the narrower exact-package checks and MCP evidence. The full [product specification](docs/product-spec.en.md) includes requirements beyond this candidate.
