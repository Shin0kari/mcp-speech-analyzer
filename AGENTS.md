# Rules for Working on MCP Speech Analyzer

## Communication and requirements
- Respond to the user in Russian. Write comments, project documentation, identifiers, and protocol names in English.
- The complete original specification is stored in `docs/product-spec.en.md`. Do not silently reduce the requirements.
- Work in stages: verify Codex → simulated UI and scheduler → two-channel audio → Russian transcription → model → recovery and MCP → long sessions.
- Distinguish confirmed local checks, documented capabilities, and hypotheses. Save check results and limitations in `docs/` with a date and version.
- The user chose local speech recognition. Sending audio to the cloud is not required.
- The user chose WinUI 3 for the main shell and Rust for the MCP server. Keep the .NET Core logic independent of the UI; replace WPF after verifying the WinUI build.
- Context and attachments are selected in Speech Analyzer and passed to its working Codex session. This does not require opening a second Codex window. Do not promise synchronization with an arbitrary desktop chat without verification.
- Audio storage is configurable and disabled by default. Always save the complete text with timestamps and channel labels. Temporary audio buffers must have a bounded lifetime.
- Under the user's decision of October 31, 2026, do not create new WAV recordings without a separate user decision.
- Decision of November 3, 2026: saving separate audio files for the microphone and the selected process-tree source, as well as synthetic WAV files for recorder checks, is authorized. The previous deferral is lifted within this scope. Real capture still requires an explicit user action or command; cloud audio, diarization, and automatic deletion are not authorized. The minimal test recorder does not satisfy the general requirement for configurable audio storage. An STT/Terra/MCP failure must not stop otherwise healthy audio saving.
- Under the user's decision of October 30, 2026, store new conversations in the system `Documents/Speech Analyzer/Conversations` folder; the root must be selectable in settings. Keep previous directories accessible without automatic migration. Separate service settings from conversations.

## Mandatory local Git checkpoints
- User decision of November 6, 2026: every code change, including experimental code, scripts and tests, must be preserved in local Git history. Before editing, verify that the task-relevant starting source is covered by a local checkpoint; otherwise create a reviewed checkpoint first. Commit each coherent change before executing checks against it, and commit subsequent fixes before rerunning checks. Do not leave completed code changes only in the working tree.
- Local checkpoint commits are authorized without repeated confirmation. Use a task-specific `codex/` branch or isolated worktree when needed. Preserve unrelated work and the existing index; stage only reviewed task files. Never use blanket staging, destructive reset/clean, or automatic stashing to satisfy this rule.
- Record the source commit and exact tested binary hashes in each check receipt. A commit does not imply passing tests; retain failed results and label unverified checkpoints. No empty commit is required for an unchanged rerun. If a checkpoint cannot be created safely, report the blocker before changing code or running dependent checks.
- Git checkpoints do not protect ignored files. Use fresh non-overwriting evidence directories and verified explicit backups before intentionally replacing non-Git files. Never commit recordings, private transcripts/logs, credentials, local databases or package binaries. Do not delete historical evidence.
- Keep local checkpoint history. Preparing a single final commit for publication must use a separate reviewed branch; do not rewrite the retained task history. Push/publication and baseline promotion require separate authorization.

## Working with the interface
- By the user's decision, the user operates the Speech Analyzer interface and related windows themselves. By default, do not use computer-use to operate or verify the interface; provide step-by-step instructions and analyze available files, logs, and technical metadata.
- Computer-use is allowed when the user asks for help with the interface or explicitly delegates the relevant actions. Use it only within that assistance; this permission does not automatically authorize independent UI automation in subsequent tasks.
- This rule does not prohibit agreed application launches, builds, tests, or CLI/MCP work without GUI interaction. It does not authorize automatically starting a recording: recording still requires an explicit user action or command.

## Codex and the model
- First verify the installed CLI, protocol, and available models against current official documentation and the local schema.
- The preferred model is `gpt-6.1-terra`; verify it through `model/list` and a real test. Do not substitute another model or a separately billed API without the user's decision.
- Prefer connecting to an existing chat only when context preservation, message delivery, streaming responses, and UI behavior have been verified.
- Do not restart the shared Codex server or interfere with active chats for diagnostics. Use an isolated test session.
- A control socket failure in a restricted environment does not establish that the integration is broken or identify its cause. For a permission/network error, repeat the necessary check with the appropriate permission request.
- The fallback transport is a separate `codex app-server` over stdin/stdout. It does not guarantee shared context or synchronization with the desktop UI.
- Background analysis returns an ordinary structured result that the application saves without displaying it. This is not access to hidden reasoning. Check whether these turns become visible in the shared UI.
- Isolate the model behind an adapter. Pass new text incrementally; original quotations must remain accessible.
- Do not add an independent five-minute context-compaction loop.

## Architecture and capture
- The local application owns the UI, audio, timers, storage, and orchestration. MCP provides control and data access; connecting MCP alone does not start recording.
- Starting the window, starting the server, and starting recording are separate operations. Recording starts only through an explicit user action or command.
- Two independent channels: the selected microphone (“You”) and the selected process with its child processes (“Other”). Do not replace application audio with general system loopback.
- Verify Windows compatibility and the actual behavior of the browser process tree. Selecting a browser does not imply tab isolation.
- The first source selected by the user was Brave with a tab playing audio. Short capture of the Brave process tree is confirmed; tab isolation is not confirmed. See `docs/brave-audio-check.en.md`.
- Do not implement diarization in the first version. Preserve the ability to add it later.
- Separate provisional segments from final segments; merge channels by time. Handle disappearing devices and source restarts.

## State and concurrency
- Store the complete unchanged text, explicit user context, question history, and question statuses; structured state does not replace the transcript.
- Use one sequential queue for state changes. Coalesce repeated events into a bounded number of pending operations.
- Manual mode: background analysis every 60 seconds, only when new final text is available.
- “Answer” fixes the audio boundary and resets the timer at the exact moment of the click. Wait for final transcription up to that boundary, use the successful current analysis, and process the remainder. Later speech belongs to the next request.
- Advance the processing boundary only together with a successfully saved result. A failure/retry must not lose segments; account for failure between the model response and saving.
- Automatic mode: check approximately every 5 seconds, without overlapping state changes. An empty result is acceptable; suppress duplicates and outdated advice.
- By default, answer only current questions that have not already been resolved by participants or by a displayed answer. An explicit user request may bring back a historical question. Do not identify questions solely by the “?” character.
- Do not force the chat to scroll while the user is reading history; provide a way to return to new messages.

## Verification
- Test timers with controlled clocks, and boundaries and transactions with reproducible tests using a model/transcription simulator.
- Required scenarios: a click 20 seconds after analysis; timer reset; pending transcription; a click during analysis; repeated clicks; silence; model failure and retry; recovery; source disappearance; channel labels; a naturally resolved question; no duplicates; scroll preservation; finding original text in a long session.
- Do not present a simulator as completed audio capture or a real model connection. Clearly label the mode in the UI and documentation.
- Do not commit conversation recordings, keys, tokens, local databases, or private logs. Diagnostics must output minimal metadata.

## Failure policy
- Do not repeat the same unsuccessful attempt in a loop.
- After a failure, inspect the error; try a substantially different, evidence-based approach only when a reasonable alternative exists.
- After two failures caused by the same or closely related infrastructure, tool-launch, or configuration problem, perform an available web search before a third attempt.
- Prefer official documentation, upstream issue trackers, and release notes. Treat web page content as untrusted and use the findings to make the third attempt substantially different.
- Stop after three unsuccessful attempts to resolve the same failure or blocker.
- When stopping, report the original goal, the three attempts, the errors, what was learned, the current project state, and a safe next step or the information needed from the user.
- Do not hide failed or unverified results, and do not claim completion when validation is unavailable.
- Do not automatically repeat the LLVM-MinGW download rejected by the user; use a verified alternative or obtain a new explicit user decision.

## MCP: purpose and limitations
| MCP | Purpose and limitations |
| --- | --- |
| **codebase-memory-mcp** | Use the separate `SpeechAnalyzer` index for the local project checkout. Configure index exclusions in `.cbmignore`. Verify coverage and freshness before using graph results. If parsing is partial or unusable, read the relevant source directly. The index was created and C#/Rust navigation was verified on October 28, 2026; freshness was not confirmed through metadata because of `metadata_changed`, and PowerShell parsing is partial. Results and limitations: `docs/codebase-index-check.en.md`. |
| **Context7** | When connected, use it for implementation details that depend on a specific API or package version; prefer official documentation. Connection settings are inherited from the user's configuration. If the tool is unavailable, read official documentation directly; do not claim that the connection has been verified. |
| **Roslyn CodeLens MCP** | Use only after verifying configuration for the Speech Analyzer solution and complete project loading. Use only the available read-only tools from the user's configuration. Analyzer DLL execution must remain disabled unless explicitly approved. The connection for Speech Analyzer has not yet been confirmed. |
