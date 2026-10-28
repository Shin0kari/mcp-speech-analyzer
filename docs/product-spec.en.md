I want to build a Windows desktop application with an MCP server that listens to a conversation, transcribes Russian speech, and uses GPT-6.1 Terra through Codex to help answer questions.

Please inspect the existing environment, verify the Codex integration first, then implement the application incrementally. Use current official documentation and distinguish verified capabilities from assumptions.

PRODUCT GOAL

Before a conference, I open the application, provide background context in Codex or the application, select audio sources, and start recording.

The application continuously transcribes the conversation. Depending on the selected mode, it either analyzes the conversation silently and displays answers when I press a button, or automatically decides when to display useful answers or recommendations.

A separate application window is acceptable, but first investigate whether integration with an existing chat in the Codex desktop application works.

AUDIO CAPTURE

Use two independent audio sources:
1. A selected microphone, labeled “You”.
2. A selected application’s playback audio, such as a browser, labeled “Other participant”.

Capture and transcribe the channels separately, then merge transcript segments chronologically.

Requirements:
- Russian speech recognition.
- Audio-source selection and level meters.
- Start, pause/resume, and stop controls.
- Clear handling of source disappearance or application restarts.
- Preserve timestamps, channel labels, and transcript text.
- Distinguish provisional transcription from finalized segments.

Do not implement speaker diarization in the first version. Multiple people in the application-audio channel may all be labeled “Other participant”. Keep the architecture extensible for future diarization.

Capture the selected application rather than all system audio. Windows application loopback capture can target a process and its children. Do not assume selecting a browser isolates a specific tab. Verify browser-process behavior and OS compatibility.

MANUAL MODE

Transcription runs continuously.

Every 60 seconds, if there is new speech:
- Send the unprocessed finalized transcript to the model.
- Update the conversation state silently.
- Do not display the background analysis in the user-facing chat.

Maintain structured state such as:
- Important facts.
- Decisions and commitments.
- Open questions.
- Questions answered by participants.
- Questions whose assistant answers have already been displayed.
- Questions that are no longer relevant.
- The transcript boundary successfully processed.

“Silent analysis” means normal structured model output that the application stores without displaying. It does not mean accessing hidden chain-of-thought.

When the user presses “Answer”:
1. Snapshot the capture boundary at the time of the click.
2. Finish transcribing pending audio up to that boundary.
3. Process any transcript not covered by a successful previous analysis.
4. Use the existing conversation context and updated state to answer all currently relevant, unanswered questions.
5. If there are no relevant questions, execute a user-configurable fallback instruction, such as a brief analysis of the conversation.
6. Display the answer.
7. Reset the background-analysis timer immediately at the time of the click. The next scheduled analysis is 60 seconds after that click.

Example:
- Analysis successfully covers the conversation through 13:34:00.
- The user presses “Answer” at 13:34:20.
- Process the unprocessed speech through 13:34:20 and produce answers using the previously analyzed context.
- Next scheduled background analysis: 13:35:20.

Speech after the click continues to be captured and belongs to subsequent processing.

Handle concurrency carefully:
- Serialize updates to conversation state.
- If background analysis is already running, reuse its successful result and process the remaining transcript.
- Do not advance the processed boundary until the result is successfully saved.
- Retry failed processing without losing transcript segments.
- Coalesce repeated clicks and timer events rather than creating an unbounded request queue.
- Skip background calls when there is no new speech.

AUTOMATIC MODE

Check for new transcript approximately every five seconds.

When there is new text:
- Update the conversation state.
- Let the model decide whether a useful answer or recommendation should be displayed.
- Allow “nothing to display” as a valid result.
- Suppress duplicate and obsolete recommendations.
- If processing takes longer than the interval, accumulate new text for the next request rather than overlapping state updates.

A five-second interval is a scheduling target, not a guarantee of a completed model response every five seconds.

QUESTION HANDLING

By default, answer only currently relevant questions that have not already been answered by the participants or displayed by the assistant.

Keep historical questions and their statuses.

An explicit typed user request overrides this default, for example:
“Answer the earlier question about pricing again.”

Do not identify questions solely by question marks. Account for context, indirect questions, and follow-up questions.

USER INTERFACE

Provide a separate desktop panel/window containing:
- Microphone selector.
- Application-audio source selector.
- Audio level meters.
- Start, pause/resume, and stop controls.
- Manual/automatic mode switch.
- “Answer” button.
- Text field for custom user questions and instructions.
- Configurable fallback instruction.
- Scrollable chat history.
- Recording, transcription, model-processing, connection, and error status.
- Time until the next scheduled analysis.
- Optionally a transcript view.

Do not force-scroll when the user is reading older messages. Provide a way to return to the newest messages.

Connecting or starting the MCP server must not automatically start recording. Starting the panel and starting recording are separate actions. Codex may launch the panel or start recording when explicitly instructed.

CONTEXT AND STORAGE

Preserve the full transcript without lossy summarization, with timestamps and channel labels.

Use a persistent working Codex session when feasible. Send incremental transcript segments instead of resending the entire conversation every minute.

Codex already supports context compaction based on token thresholds. Do not add an independent five-minute summarization/compaction loop in the first version.

Maintain application-owned structured conversation state during normal analysis for:
- UI behavior.
- Question tracking.
- Recovery after restart.
- Reliable processing boundaries.

This state is not a replacement for the original transcript. Make it possible to retrieve original passages when exact wording, numbers, dates, or commitments matter.

Store user background context and instructions explicitly. Do not assume they are automatically available to a separate Codex session.

Decide explicitly whether to retain audio files; do not silently assume permanent audio retention.

CODEX INTEGRATION: INVESTIGATE FIRST

Preferred model: GPT-6.1 Terra.

The desired integration is with Codex, potentially an existing chat with prepared context. A separate chat window backed by Codex is acceptable.

Previously verified on this Windows machine:
- Codex CLI is installed.
- `codex queue --help` exists and accepts a thread ID/name and message.
- `codex app-server --help` exists.
- `codex app-server proxy --help` exists.
- App-server documentation describes initialization, thread resume, turn start, streaming events, and context compaction.

A read-only connection probe used:
`codex app-server proxy`

It failed connecting to:
C:\Users\mitya\.codex\app-server-control\app-server-control.sock

Error:
“An operation on a socket encountered a dead network” / Windows os error 10050.

The probe was run from a restricted execution environment. The cause has NOT been established. Do not claim that Codex integration is broken or that the sandbox is conclusively responsible.

No end-to-end message delivery, desktop-chat synchronization, context preservation, or Terra inference was verified.

Investigation sequence:
1. Inspect the installed version and supported commands.
2. Test connection from a normal user-level Windows process, subject to actual permissions.
3. Verify the running server and correct control endpoint without restarting or disrupting active chats.
4. Perform protocol initialization and read-only access checks.
5. Verify continuation of a test conversation and preservation of its context.
6. Verify message delivery and response streaming.
7. Check whether messages appear in the Codex desktop UI and how concurrent activity is handled.

Fallback:
Run a dedicated `codex app-server` process with stdin/stdout transport and display responses in our application. This avoids relying on the existing control socket, but does not automatically provide the existing desktop chat’s context or guarantee shared UI synchronization.

Keep the model connection behind an adapter so a direct API backend can be added if needed. Do not silently replace Codex with a separately billed API backend.

Silent background processing may require a separate working Codex session. Merely hiding output in our panel does not hide turns in the Codex desktop UI. Verify this behavior before choosing the final session arrangement.

MCP ROLE

The MCP server exposes application control and stored conversation data. The local application owns audio capture, scheduling, persistence, and UI.

Possible tools:
- start_capture
- pause_capture
- resume_capture
- stop_capture
- get_status
- get_transcript
- get_open_questions
- get_analysis_state
- set_context

Finalize tool names and schemas during implementation.

Do not assume MCP alone automatically starts model turns every minute or every five seconds. The application needs an explicit orchestration mechanism.

IMPLEMENTATION ORDER

1. Verify Codex connectivity and the session architecture.
2. Build a minimal UI and scheduling/state machine using simulated transcript input.
3. Implement two-channel audio capture.
4. Add Russian transcription and pending-segment handling.
5. Connect model analysis and answer generation.
6. Add persistence, recovery, background context, and MCP tools.
7. Validate long conversations and Codex compaction behavior.

IMPORTANT VALIDATION SCENARIOS

- Minute-based background analysis.
- Answer click 20 seconds after the previous analysis.
- Timer reset at click time.
- Pending transcription when clicking.
- Click while background analysis is running.
- Repeated clicks.
- Silence.
- Model or connection failure and retry.
- Application restart without losing processing state.
- Source application closing/restarting.
- Correct microphone/application channel labels.
- Questions answered naturally by participants.
- No duplicate answers.
- Scroll position preserved when new messages arrive.
- Long sessions retaining important facts and allowing original-text lookup.

RELEVANT DOCUMENTATION

Codex app-server:
https://learn.chatgpt.com/docs/app-server

Codex developer commands:
https://learn.chatgpt.com/docs/developer-commands

Codex configuration:
https://learn.chatgpt.com/docs/config-file/config-reference

Windows application loopback capture:
https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/

These are planning references, not proof that every desired integration works in the installed environment.