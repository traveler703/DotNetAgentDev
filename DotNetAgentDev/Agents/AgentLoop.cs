using System.Text.Encodings.Web;
using System.Text.Json;
using DotNetAgentDev.Llm;
using DotNetAgentDev.Models;
using DotNetAgentDev.Options;
using DotNetAgentDev.Tools;
using Microsoft.Extensions.Options;

namespace DotNetAgentDev.Agents;

public sealed class AgentLoop
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ILlmClient _llmClient;
    private readonly ToolRegistry _toolRegistry;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentLoop> _logger;

    public AgentLoop(
        ILlmClient llmClient,
        ToolRegistry toolRegistry,
        IOptions<AgentOptions> options,
        ILogger<AgentLoop> logger)
    {
        _llmClient = llmClient;
        _toolRegistry = toolRegistry;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AgentRunResult> RunAsync(
        string agentName,
        string responsibility,
        AgentTaskContext context,
        IReadOnlyList<string> allowedTools,
        int sequenceStart,
        CancellationToken cancellationToken,
        Action<PlanningStreamEvent>? onProgress = null)
    {
        var tools = allowedTools.Select(_toolRegistry.GetDefinition).ToList();
        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "system",
                Content = BuildSystemPrompt(agentName, responsibility, allowedTools)
            },
            new()
            {
                Role = "user",
                Content = BuildUserPrompt(context)
            }
        };
        var trace = new List<AgentTraceStep>
        {
            CreateTrace(sequenceStart, agentName, "Thought", "分析子任务",
                $"根据用户约束分析“{context.Task}”，并选择必要工具获取事实数据。")
        };
        EmitTrace(onProgress, trace[0]);
        var observations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toolCallCount = 0;
        var calledToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sequence = sequenceStart + 1;
        var finalAnswer = string.Empty;

        for (var step = 0; step < _options.MaxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var responseMessageId = $"{NormalizeId(agentName)}-response-{sequence}-{step}";
            var remainingTools = tools
                .Where(tool => !calledToolNames.Contains(tool.Name))
                .ToList();
            var response = await _llmClient.CompleteStreamingAsync(
                messages,
                remainingTools,
                delta => onProgress?.Invoke(new PlanningStreamEvent
                {
                    Type = "delta",
                    MessageId = responseMessageId,
                    Agent = agentName,
                    Phase = "Model",
                    Title = "正在回复",
                    Detail = delta
                }),
                cancellationToken);

            if (response.ToolCalls.Count == 0)
            {
                finalAnswer = response.Content ?? "子任务已完成。";
                var finalTrace = CreateTrace(
                    sequence++,
                    agentName,
                    "FinalAnswer",
                    "提交专业结论",
                    finalAnswer);
                trace.Add(finalTrace);
                EmitTrace(onProgress, finalTrace, responseMessageId);
                break;
            }

            messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = response.Content,
                ToolCalls = response.ToolCalls
            });

            foreach (var toolCall in response.ToolCalls)
            {
                if (!calledToolNames.Add(toolCall.Name))
                {
                    var duplicateDetail = $"{toolCall.Name} 已在本轮执行过，系统跳过重复调用并要求 Agent 使用已有结果。";
                    var duplicateTrace = CreateTrace(
                        sequence++,
                        agentName,
                        "Observation",
                        "跳过重复工具调用",
                        duplicateDetail);
                    trace.Add(duplicateTrace);
                    EmitTrace(onProgress, duplicateTrace);
                    messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = toolCall.Id,
                        Content = JsonSerializer.Serialize(new { notice = duplicateDetail }, JsonOptions)
                    });
                    continue;
                }

                var actionTrace = CreateTrace(
                    sequence++,
                    agentName,
                    "Action",
                    $"调用 {toolCall.Name}",
                    SummarizeArguments(toolCall.Arguments));
                trace.Add(actionTrace);
                EmitTrace(
                    onProgress,
                    actionTrace,
                    toolCall.Id,
                    toolCall.Name,
                    null);

                var result = await _toolRegistry.ExecuteAsync(
                    toolCall.Name,
                    toolCall.Arguments,
                    cancellationToken);
                toolCallCount++;
                observations[toolCall.Name] = result.Content;

                var observationTrace = CreateTrace(
                    sequence++,
                    agentName,
                    "Observation",
                    result.Success ? $"{toolCall.Name} 返回结果" : $"{toolCall.Name} 调用失败",
                    SummarizeObservation(result));
                trace.Add(observationTrace);
                EmitTrace(
                    onProgress,
                    observationTrace,
                    toolCall.Id,
                    toolCall.Name,
                    result.Success);

                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    Content = result.Content
                });
            }
        }

        if (string.IsNullOrWhiteSpace(finalAnswer))
        {
            finalAnswer = "达到最大推理步数，主控 Agent 将使用已获得的工具结果继续生成方案。";
            var limitTrace = CreateTrace(
                sequence,
                agentName,
                "FinalAnswer",
                "达到步数上限",
                finalAnswer);
            trace.Add(limitTrace);
            EmitTrace(onProgress, limitTrace);
        }

        _logger.LogInformation(
            "{AgentName} finished with {ToolCallCount} tool calls in {Mode} mode.",
            agentName,
            toolCallCount,
            _llmClient.CurrentMode);

        return new AgentRunResult(
            agentName,
            finalAnswer,
            observations,
            trace,
            toolCallCount,
            _llmClient.CurrentMode);
    }

    private static string BuildSystemPrompt(
        string agentName,
        string responsibility,
        IReadOnlyList<string> allowedTools) =>
        $"""
         你是多 Agent 旅游规划系统中的 {agentName}。
         职责：{responsibility}
         你必须基于工具结果工作，不得编造实时价格、天气或政策。
         涉及景点、地标、商圈、博物馆、公园或餐饮区域时，必须使用真实、具体、可搜索的专有名称。
         严禁使用“城市历史博物馆”“老城步行街”“城市中央公园”“本地市场美食体验”
         “城市观景台”等通用占位名称。工具资料不足时必须明确写“需联网确认”，不能虚构名称。
         请依次调用完成任务所需的工具：{string.Join("、", allowedTools)}。
         每个工具在一次子任务中最多调用一次；已有工具结果足够时必须直接提交结论，禁止反复查询同一工具。
         工具调用结束后，以 Markdown 输出简洁、可核验的专业结论。不要输出私有思维链。
         """;

    private static string BuildUserPrompt(AgentTaskContext context)
    {
        var requestJson = JsonSerializer.Serialize(context.Request, JsonOptions);
        var sharedContextJson = JsonSerializer.Serialize(
            context.SharedContext ?? new Dictionary<string, string>(),
            JsonOptions);

        return $"""
                子任务：{context.Task}
                <request>{requestJson}</request>
                <shared-context>{sharedContextJson}</shared-context>
                """;
    }

    private static AgentTraceStep CreateTrace(
        int sequence,
        string agent,
        string phase,
        string title,
        string detail) =>
        new(sequence, agent, phase, title, detail, DateTimeOffset.UtcNow);

    private static void EmitTrace(
        Action<PlanningStreamEvent>? onProgress,
        AgentTraceStep trace,
        string? messageId = null,
        string? toolName = null,
        bool? success = null) =>
        onProgress?.Invoke(new PlanningStreamEvent
        {
            Type = "trace",
            MessageId = messageId,
            Agent = trace.Agent,
            Phase = trace.Phase,
            Title = trace.Title,
            Detail = trace.Detail,
            ToolName = toolName,
            Success = success,
            Timestamp = trace.Timestamp
        });

    private static string NormalizeId(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    private static string SummarizeArguments(string arguments)
    {
        if (arguments.Length <= 220)
        {
            return arguments;
        }

        return $"{arguments[..220]}...";
    }

    private static string SummarizeObservation(ToolExecutionResult result)
    {
        if (!result.Success)
        {
            return result.Error ?? "工具执行失败。";
        }

        try
        {
            using var document = JsonDocument.Parse(result.Content);
            var compact = JsonSerializer.Serialize(document.RootElement, JsonOptions);
            return compact.Length <= 360 ? compact : $"{compact[..360]}...";
        }
        catch (JsonException)
        {
            return result.Content.Length <= 360
                ? result.Content
                : $"{result.Content[..360]}...";
        }
    }
}
