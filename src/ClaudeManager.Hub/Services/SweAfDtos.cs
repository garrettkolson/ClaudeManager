using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeManager.Hub.Services;

// ── AgentField API request/response shapes ─────────────────────────────────

internal record ExecuteResponse(
    [property: JsonPropertyName("execution_id")] string ExecutionId,
    [property: JsonPropertyName("status")]       string Status);

internal record ExecutionStatusResponse(
    [property: JsonPropertyName("execution_id")] string      ExecutionId,
    [property: JsonPropertyName("status")]       string      Status,
    [property: JsonPropertyName("result")]       JsonElement? Result);

// ── Observability webhook payload ──────────────────────────────────────────

public record ObservabilityBatch(
    [property: JsonPropertyName("batch_id")]    string                BatchId,
    [property: JsonPropertyName("event_count")] int                   EventCount,
    [property: JsonPropertyName("events")]      List<ObservabilityEvent> Events,
    [property: JsonPropertyName("timestamp")]   DateTimeOffset        Timestamp);

public record ObservabilityEvent(
    [property: JsonPropertyName("event_type")]   string         EventType,
    [property: JsonPropertyName("event_source")] string         EventSource,
    [property: JsonPropertyName("timestamp")]    DateTimeOffset Timestamp,
    [property: JsonPropertyName("data")]         JsonElement    Data);
