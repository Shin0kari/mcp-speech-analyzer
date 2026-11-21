use rmcp::{handler::server::wrapper::Parameters, model::{CallToolResult, ContentBlock}, schemars, tool, tool_router, ServiceExt, ErrorData};
use serde_json::{json, Value};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
use std::time::Duration;

// Повторяется только открытие занятого канала ДО отправки запроса.
// Ошибки доступа/отсутствия канала и сбой после отправки повторять нельзя.
#[cfg(windows)]
async fn connect_with_retry<T>(mut open: impl FnMut() -> std::io::Result<T>) -> std::io::Result<T> {
    for attempt in 0..50 {
        match open() {
            Ok(pipe) => return Ok(pipe),
            Err(error) if error.raw_os_error() == Some(231) && attempt < 49 => {
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
            Err(error) => return Err(error),
        }
    }
    unreachable!()
}

#[cfg(all(test, windows))]
mod connection_tests {
    use super::connect_with_retry;

    #[tokio::test]
    async fn busy_before_connection_then_success() {
        let mut calls = 0;
        let connected = connect_with_retry(|| {
            calls += 1;
            if calls < 3 { Err(std::io::Error::from_raw_os_error(231)) } else { Ok(7) }
        }).await.unwrap();
        assert_eq!((connected, calls), (7, 3));
    }

    #[tokio::test]
    async fn other_errors_are_not_retried() {
        for code in [2, 5, 109] {
            let mut calls = 0;
            let result = connect_with_retry::<()>(|| {
                calls += 1; Err(std::io::Error::from_raw_os_error(code))
            }).await;
            assert_eq!(result.unwrap_err().raw_os_error(), Some(code));
            assert_eq!(calls, 1);
        }
    }

    #[tokio::test]
    async fn persistent_busy_is_bounded() {
        let mut calls = 0;
        let result = connect_with_retry::<()>(|| {
            calls += 1; Err(std::io::Error::from_raw_os_error(231))
        }).await;
        assert_eq!(result.unwrap_err().raw_os_error(), Some(231));
        assert_eq!(calls, 50);
    }
}

#[derive(Clone)]
struct SpeechTools { pipe: String }

#[derive(Debug, serde::Deserialize, schemars::JsonSchema)]
struct ContextParams { text: String }

#[derive(Debug, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
enum WindowPolicy { SpecificWindow, Application }
#[derive(Debug, serde::Deserialize, schemars::JsonSchema)]
struct AudioSourcesParams {
    microphone_source_id: Option<String>,
    window_source_id: Option<String>,
    policy: WindowPolicy,
    catalog_version: u64,
}

#[derive(Debug, serde::Deserialize, schemars::JsonSchema)]
struct TranscriptParams {
    #[serde(default)] from_ms: u64,
    #[serde(default = "default_limit")] limit: u32,
    cursor: Option<String>,
    text: Option<String>,
    to_ms: Option<u64>,
    channel: Option<String>,
    segment_id: Option<String>,
}
fn default_limit() -> u32 { 100 }

#[derive(Clone)]
struct ArchiveTools { transport: SpeechTools }
#[derive(Debug, serde::Deserialize, schemars::JsonSchema)]
struct ArchiveParams {
    operation_id: String,
    text: Option<String>,
    segment_id: Option<String>,
    cursor: Option<String>,
    #[serde(default = "default_limit")] limit: u32,
}
// В режиме модели сервер физически не публикует инструменты записи или управления.
#[tool_router(server_handler)]
impl ArchiveTools {
    #[tool(description = "Read-only search of exact original transcript within the active operation boundary. Supply operation_id from the analysis input, text substring or segment_id UUID. Continue with nextCursor while keeping filters unchanged. Returns exact segment IDs, timestamps, channels and quotes.")]
    async fn search_transcript(&self, Parameters(args): Parameters<ArchiveParams>) -> Result<CallToolResult, ErrorData> {
        if !(1..=500).contains(&args.limit) { return Err(ErrorData::invalid_params("limit must be 1..500", None)); }
        self.transport.forward("model_search_transcript", json!({"operationId": args.operation_id, "text": args.text,
            "segmentId": args.segment_id, "cursor": args.cursor, "limit": args.limit})).await
    }
}

impl SpeechTools {
    async fn forward(&self, method: &str, params: Value) -> Result<CallToolResult, ErrorData> {
        let result = tokio::time::timeout(Duration::from_secs(10), self.exchange(method, params)).await
            .map_err(|_| ErrorData::internal_error("Speech Analyzer did not reply within 10 seconds", None))?
            .map_err(|error| ErrorData::internal_error(format!("Open Speech Analyzer first: {error}"), None))?;
        let mut tool_result = CallToolResult::success(vec![ContentBlock::json(result.clone())?]);
        if result.get("ok") == Some(&Value::Bool(false)) { tool_result.is_error = Some(true); }
        Ok(tool_result)
    }

    #[cfg(windows)]
    async fn exchange(&self, method: &str, params: Value) -> Result<Value, Box<dyn std::error::Error + Send + Sync>> {
        let path = format!(r"\\.\pipe\{}", self.pipe);
        let pipe = connect_with_retry(|| tokio::net::windows::named_pipe::ClientOptions::new().open(&path)).await?;
        let (read, mut write) = tokio::io::split(pipe);
        let request = serde_json::to_string(&json!({ "method": method, "params": params }))?;
        if request.len() > 65_535 { return Err("Request exceeds 64 KiB".into()); }
        write.write_all(request.as_bytes()).await?;
        write.write_all(b"\n").await?;
        write.flush().await?;
        let mut reader = BufReader::new(read);
        let mut response = Vec::new();
        loop {
            let available = reader.fill_buf().await?;
            if available.is_empty() { return Err("Application disconnected".into()); }
            let end = available.iter().position(|b| *b == b'\n');
            let amount = end.map_or(available.len(), |i| i + 1);
            if response.len() + amount > 1_048_576 { return Err("Response exceeds 1 MiB".into()); }
            response.extend_from_slice(&available[..amount]);
            reader.consume(amount);
            if end.is_some() { break; }
        }
        Ok(serde_json::from_slice(&response)?)
    }
}

// MCP только управляет приложением. Handshake и tools/list никогда не начинают захват.
#[tool_router(server_handler)]
impl SpeechTools {
    #[tool(description = "Read Speech Analyzer status. Does not start capture.")]
    async fn get_status(&self) -> Result<CallToolResult, ErrorData> { self.forward("get_status", json!({})).await }
    #[tool(description = "Refresh microphone and application-window catalog. Returns runtime source IDs; refresh invalidates previous IDs. Does not capture audio. Window selection captures the owning process tree, not an isolated browser tab.")]
    async fn list_audio_sources(&self) -> Result<CallToolResult, ErrorData> { self.forward("list_audio_sources", json!({})).await }
    #[tool(description = "Persist or clear microphone and application selection using IDs and catalog_version from the latest list_audio_sources. Requires stopped capture. Does not start capture; selects real audio mode. If the catalog is stale, call list_audio_sources again.")]
    async fn set_audio_sources(&self, Parameters(args): Parameters<AudioSourcesParams>) -> Result<CallToolResult, ErrorData> {
        let policy = match args.policy { WindowPolicy::SpecificWindow => "specific_window", WindowPolicy::Application => "application" };
        self.forward("set_audio_sources", json!({"microphoneSourceId": args.microphone_source_id, "windowSourceId": args.window_source_id, "policy": policy, "catalogVersion": args.catalog_version})).await
    }
    #[tool(description = "Start the selected capture mode only when the user explicitly requests recording or simulation.")]
    async fn start_capture(&self) -> Result<CallToolResult, ErrorData> { self.forward("start_capture", json!({})).await }
    #[tool(description = "Pause capture in Speech Analyzer.")]
    async fn pause_capture(&self) -> Result<CallToolResult, ErrorData> { self.forward("pause_capture", json!({})).await }
    #[tool(description = "Resume paused capture in Speech Analyzer.")]
    async fn resume_capture(&self) -> Result<CallToolResult, ErrorData> { self.forward("resume_capture", json!({})).await }
    #[tool(description = "Stop capture in Speech Analyzer.")]
    async fn stop_capture(&self) -> Result<CallToolResult, ErrorData> { self.forward("stop_capture", json!({})).await }
    #[tool(description = "Retrieve finalized original transcript segments with channel and timestamps. Continue the immutable snapshot with nextCursor, keeping filters unchanged. Cursor expires after five minutes or application/session restart; stale_cursor requires a new query. Does not start capture.")]
    async fn get_transcript(&self, Parameters(args): Parameters<TranscriptParams>) -> Result<CallToolResult, ErrorData> {
        if !(1..=500).contains(&args.limit) { return Err(ErrorData::invalid_params("limit must be 1..500", None)); }
        self.forward("get_transcript", json!({"fromMs": args.from_ms, "limit": args.limit, "cursor": args.cursor,
            "text": args.text, "toMs": args.to_ms, "channel": args.channel, "segmentId": args.segment_id})).await
    }
    #[tool(description = "Search unchanged original transcript by text, time range, channel (You or Other), or segment UUID. Uses the same immutable snapshot pagination as get_transcript; returns original segment IDs and exact quotes. Does not invoke the model or capture audio.")]
    async fn search_transcript(&self, Parameters(args): Parameters<TranscriptParams>) -> Result<CallToolResult, ErrorData> {
        if !(1..=500).contains(&args.limit) { return Err(ErrorData::invalid_params("limit must be 1..500", None)); }
        self.forward("search_transcript", json!({"fromMs": args.from_ms, "limit": args.limit, "cursor": args.cursor,
            "text": args.text, "toMs": args.to_ms, "channel": args.channel, "segmentId": args.segment_id})).await
    }
    #[tool(description = "Read currently open questions.")]
    async fn get_open_questions(&self) -> Result<CallToolResult, ErrorData> { self.forward("get_open_questions", json!({})).await }
    #[tool(description = "Read successfully committed structured analysis state.")]
    async fn get_analysis_state(&self) -> Result<CallToolResult, ErrorData> { self.forward("get_analysis_state", json!({})).await }
    #[tool(description = "Set user background context for future analysis. Does not start capture or a model turn.")]
    async fn set_context(&self, Parameters(args): Parameters<ContextParams>) -> Result<CallToolResult, ErrorData> {
        self.forward("set_context", json!({"text": args.text})).await
    }
}

fn pipe_name() -> Result<String, String> {
    let args: Vec<String> = std::env::args().skip(1).filter(|arg| arg != "--archive").collect();
    let name = if args.is_empty() {
        let user = std::env::var("USERNAME").map_err(|_| "USERNAME missing; supply --pipe <name>")?;
        format!("speech-analyzer-{}", user.chars().map(|c| if c.is_alphanumeric() { c } else { '_' }).collect::<String>())
    } else if args.len() == 2 && args[0] == "--pipe" { args[1].clone() }
    else { return Err("Usage: speech-analyzer-mcp [--pipe <name>]".into()); };
    if name.is_empty() || name.len() > 150 || !name.chars().all(|c| c.is_alphanumeric() || c == '-' || c == '_') {
        return Err("Invalid pipe name".into());
    }
    Ok(name)
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let pipe = pipe_name().map_err(std::io::Error::other)?;
    if std::env::args().any(|arg| arg == "--archive") {
        ArchiveTools { transport: SpeechTools { pipe } }.serve(rmcp::transport::stdio()).await?.waiting().await?;
    } else {
        SpeechTools { pipe }.serve(rmcp::transport::stdio()).await?.waiting().await?;
    }
    Ok(())
}
