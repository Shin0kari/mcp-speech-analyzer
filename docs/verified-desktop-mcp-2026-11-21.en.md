# Desktop and MCP source snapshot

November 21, 2026. Version 1.

The production WinUI, Core, Windows audio and worker source comes from checkpoint `b24520ac8d70587e9b21b3e42f8777352bfefde2`. The Rust server source and Cargo lockfile come from the separate retained checkpoint `7c60dc92b0d9e47a65d3572b005f82b074af0428`. Packaging helpers accept a workspace root for locating external inputs. This snapshot carries one new commit above the existing GitHub default-branch history.

## Exact v5 package evidence

The prior user-operated trial used workflow 8, result contract 5, local Whisper Large V3, rolling 2n with n=30, two independent audio sources, audio retention off, and GPT-6.1 Terra through Codex CLI 0.167.0-alpha.3.2 over app-server stdio. One silent Background committed through 30 seconds. An Answer accepted during Background retrieved the historical original from the requested application channel, grounded its answer in that original and saved one message. Reopen preserved one unique answer and the journal and transcript identities. Reopen made zero capture starts and zero remote starts. Two short application-audio timing gaps remain unexplained. Browser-tab isolation, general audio continuity and connected MCP control were outside that trial.

The exact-package offline evidence was 14/14 desktop model cases, 11/11 headless WinUI launcher cases, 39/39 rolling integration cases, 16/16 recovery cases and 40/40 request regressions. Package validation matched 336 files and the external Large V3 model. The broad suite has known failures in `BoundedEvents` and `InvalidModelResult`. Usage is observational in ordinary desktop sessions. Experiment-only admission controls are still supported.

| Historical v5 item | SHA-256 |
| --- | --- |
| Manifest | `0E8F75566B9959F503BF34639D5FEA45F544EA0B663656DF49F5756D8190B2ED` |
| WinUI DLL | `E0270ED885B61F5CCBE55F8BD30E961B7E4283321F09CB543F7C6E94E145760D` |
| Core DLL | `5DCC3C79A82D51B97F0291C1705E53DCAB25285DCFFAE132790CD8CD8AA519EA` |
| Windows audio DLL | `078B056FB065ACD21737C4269C2C4610D8AF4637F2B33E6792B3A89ED8E8899D` |
| Owned worker DLL | `A8BE6FF5B26829AB0B5E16004E20687D45D7F75E83D832618CF939D1248F2262` |
| Local Large V3 weights | `64D182B440B98D5203C4F9BD541544D84C605196C4F7B845DFA11FB23594D1E2` |

## Separate MCP evidence

An earlier isolated regression exercised all 12 tools through Rust stdio and the application's CurrentUserOnly named pipe. The main matrix had 29 successful calls and 16 expected failures. The separate journal matrix passed 21 compatibility scenarios with 420 successful calls. These used synthetic application sessions. A later connected readiness probe reached the app in simulation with capture stopped. Those checks predate the v5 desktop trial. The source smoke check in the README only initializes the fresh server and enumerates its tool schemas.

The November 21 source check ran locked offline Cargo check and build on Windows x64 GNU with Rust 1.99.1. The fresh binary (SHA-256 `20184E56AA6D410587EC4B4AD234C4FA59BE8E465BDCAB8C45469E2ABC56636F`) initialized MCP protocol `2025-11-25` and enumerated all 12 expected tools. The smoke check sent zero capture commands and made no application-pipe call.
