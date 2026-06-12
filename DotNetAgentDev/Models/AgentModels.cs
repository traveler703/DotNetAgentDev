using System.Text.Json;

namespace DotNetAgentDev.Models;

public sealed record ChatMessage
{
    public required string Role { get; init; }
    public string? Content { get; init; }
    public string? ToolCallId { get; init; }
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }
}

public sealed record LlmToolCall(
    string Id,
    string Name,
    string Arguments);

public sealed record LlmResponse(
    string? Content,
    IReadOnlyList<LlmToolCall> ToolCalls,
    string FinishReason,
    string Model);

public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters);

public sealed record ToolExecutionResult(
    bool Success,
    string Content,
    string? Error = null);

public sealed record AgentRunResult(
    string AgentName,
    string FinalAnswer,
    IReadOnlyDictionary<string, string> Observations,
    IReadOnlyList<AgentTraceStep> Trace,
    int ToolCallCount,
    string ModelMode);

public sealed record AgentTaskContext(
    TravelRequest Request,
    string Task,
    IReadOnlyDictionary<string, string>? SharedContext = null);

public sealed record PlanningStreamEvent
{
    public required string Type { get; init; }
    public string Agent { get; init; } = "系统";
    public string Phase { get; init; } = "Progress";
    public required string Title { get; init; }
    public string Detail { get; init; } = string.Empty;
    public int? Percent { get; init; }
    public TravelPlan? Plan { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
