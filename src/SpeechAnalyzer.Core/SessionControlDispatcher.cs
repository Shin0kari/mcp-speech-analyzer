using System.Text.Json;

namespace SpeechAnalyzer.Core;

// Один разбор внешних команд для окна и изолированных интеграционных проверок.
// Владение UI/захватом остаётся у приложения; callbacks не создают второй writer.
public sealed class SessionControlDispatcher(
    Func<SessionCoordinator> session,
    Func<TranscriptQueries> transcripts,
    Func<object> status,
    Func<Task<object>> listSources,
    Func<string?, string?, WindowMatchPolicy, long, Task<object>> selectSources,
    Func<string, Task> setContext,
    Func<Task> startCapture,
    Func<Task> resumeCapture)
{
    public async Task<object> HandleAsync(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "list_audio_sources": return await listSources();
            case "set_audio_sources":
                string? microphoneId = parameters.GetProperty("microphoneSourceId").GetString();
                string? windowId = parameters.GetProperty("windowSourceId").GetString();
                var policy = parameters.GetProperty("policy").GetString() switch
                {
                    "specific_window" => WindowMatchPolicy.SpecificWindow,
                    "application" => WindowMatchPolicy.Application,
                    _ => throw new ArgumentException("policy: specific_window или application")
                };
                return await selectSources(string.IsNullOrEmpty(microphoneId) ? null : microphoneId,
                    string.IsNullOrEmpty(windowId) ? null : windowId, policy,
                    parameters.GetProperty("catalogVersion").GetInt64());
            case "start_capture": await startCapture(); break;
            case "pause_capture": await session().PauseAsync(); break;
            case "resume_capture": await resumeCapture(); break;
            case "stop_capture": await session().StopAsync(); break;
            case "get_status": break;
            case "get_analysis_state": return session().Snapshot().Conversation.State;
            case "get_open_questions": return session().Snapshot().Conversation.State.Questions.Where(q => q.Status == QuestionStatus.Open).ToArray();
            case "get_transcript":
            case "search_transcript":
                long from = parameters.TryGetProperty("fromMs", out var start) ? start.GetInt64() : 0;
                int limit = parameters.TryGetProperty("limit", out var size) ? size.GetInt32() : 100;
                string? cursor = parameters.TryGetProperty("cursor", out var cursorValue) ? cursorValue.GetString() : null;
                string query = parameters.TryGetProperty("text", out var queryValue) ? queryValue.GetString() ?? "" : "";
                long? to = parameters.TryGetProperty("toMs", out var toValue) && toValue.ValueKind != JsonValueKind.Null ? toValue.GetInt64() : null;
                AudioChannel? channel = parameters.TryGetProperty("channel", out var channelValue) && channelValue.ValueKind != JsonValueKind.Null
                    ? Enum.Parse<AudioChannel>(channelValue.GetString()!, false) : null;
                Guid? segmentId = parameters.TryGetProperty("segmentId", out var idValue) && idValue.ValueKind != JsonValueKind.Null ? idValue.GetGuid() : null;
                return transcripts().Read(new(query, from, to, channel, segmentId), limit, cursor);
            case "set_context":
                string text = parameters.GetProperty("text").GetString() ?? "";
                if (text.Length > 30_000) throw new ArgumentException("Контекст превышает 30 000 символов.");
                await setContext(text);
                return new { prepared = true, sentToCodex = false };
            default: throw new ArgumentException("Неизвестная команда управления.");
        }
        return status();
    }
}
