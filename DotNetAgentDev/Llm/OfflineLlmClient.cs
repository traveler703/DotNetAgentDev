using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Llm;

public sealed partial class OfflineLlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var calledTools = messages
            .Where(message => message.Role == "assistant" && message.ToolCalls is not null)
            .SelectMany(message => message.ToolCalls!)
            .Select(call => call.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextTool = tools.FirstOrDefault(tool => !calledTools.Contains(tool.Name));

        if (nextTool is not null)
        {
            var request = ExtractRequest(messages);
            var sharedContext = ExtractSharedContext(messages);
            var task = ExtractTask(messages);
            var arguments = BuildArguments(nextTool.Name, request, sharedContext, task);
            var call = new LlmToolCall(
                $"offline_{Guid.NewGuid():N}",
                nextTool.Name,
                JsonSerializer.Serialize(arguments, JsonOptions));

            return Task.FromResult(new LlmResponse(
                $"为了完成子任务，先调用 {nextTool.Name} 获取可验证数据。",
                [call],
                "tool_calls",
                "offline-rule-engine"));
        }

        var observations = messages.Count(message => message.Role == "tool");
        return Task.FromResult(new LlmResponse(
            $"已完成 {observations} 次工具观察，结果已交给主控 Agent 进行约束检查和统一整合。",
            [],
            "stop",
            "offline-rule-engine"));
    }

    public async Task<LlmResponse> CompleteStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        Action<string>? onContentDelta,
        CancellationToken cancellationToken)
    {
        var response = await CompleteAsync(messages, tools, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Content))
        {
            onContentDelta?.Invoke(response.Content);
        }

        return response;
    }

    private static TravelRequest ExtractRequest(IReadOnlyList<ChatMessage> messages)
    {
        var text = string.Join('\n', messages.Select(message => message.Content));
        var match = RequestRegex().Match(text);
        if (!match.Success)
        {
            return new TravelRequest
            {
                Departure = "未知",
                Destination = "未知",
                Budget = 5000
            };
        }

        return JsonSerializer.Deserialize<TravelRequest>(match.Groups[1].Value, JsonOptions)
               ?? throw new InvalidOperationException("离线模型无法读取旅行需求。");
    }

    private static Dictionary<string, JsonElement> ExtractSharedContext(
        IReadOnlyList<ChatMessage> messages)
    {
        var text = string.Join('\n', messages.Select(message => message.Content));
        var match = ContextRegex().Match(text);
        if (!match.Success)
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                   match.Groups[1].Value,
                   JsonOptions)
               ?? [];
    }

    private static Dictionary<string, object?> BuildArguments(
        string toolName,
        TravelRequest request,
        IReadOnlyDictionary<string, JsonElement> context,
        string task)
    {
        var startDate = request.StartDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(30));
        var nightlyBudget = request.Budget * 0.28m / Math.Max(1, request.Days - 1);

        return toolName switch
        {
            "attraction_search" => new()
            {
                ["destination"] = request.Destination,
                ["preferences"] = request.Preferences,
                ["maxResults"] = Math.Clamp(request.Days * 3, 4, 18)
            },
            "route_sort" => new()
            {
                ["destination"] = request.Destination,
                ["preferences"] = request.Preferences,
                ["pace"] = request.Pace.ToString(),
                ["days"] = request.Days
            },
            "hotel_search" => new()
            {
                ["destination"] = request.Destination,
                ["nightlyBudget"] = decimal.Round(nightlyBudget, 2),
                ["preferences"] = request.Preferences
            },
            "transport_estimate" => new()
            {
                ["departure"] = request.Departure,
                ["destination"] = request.Destination,
                ["days"] = request.Days,
                ["travelers"] = request.Travelers
            },
            "budget_calculator" => BuildBudgetArguments(request, context),
            "weather_lookup" => new()
            {
                ["destination"] = request.Destination,
                ["startDate"] = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["days"] = request.Days
            },
            "risk_check" => new()
            {
                ["destination"] = request.Destination,
                ["days"] = request.Days,
                ["pace"] = request.Pace.ToString(),
                ["notes"] = request.Notes,
                ["startDate"] = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            },
            "preference_memory" => new()
            {
                ["userId"] = request.UserId,
                ["preferences"] = request.Preferences,
                ["destination"] = request.Destination
            },
            "travel_web_research" => new()
            {
                ["departure"] = request.Departure,
                ["destination"] = request.Destination,
                ["startDate"] = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["days"] = request.Days,
                ["topics"] = task.Contains("风险", StringComparison.OrdinalIgnoreCase)
                             || task.Contains("签证", StringComparison.OrdinalIgnoreCase)
                    ? "visa,weather,disaster,safety"
                    : task.Contains("交通", StringComparison.OrdinalIgnoreCase)
                        ? "transport"
                        : "itinerary,transport,food"
            },
            _ => []
        };
    }

    private static string ExtractTask(IReadOnlyList<ChatMessage> messages)
    {
        var userMessage = messages.LastOrDefault(message => message.Role == "user")?.Content;
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return string.Empty;
        }

        var firstLine = userMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.TrimStart().StartsWith("子任务：", StringComparison.Ordinal));
        return firstLine?.Trim() ?? string.Empty;
    }

    private static Dictionary<string, object?> BuildBudgetArguments(
        TravelRequest request,
        IReadOnlyDictionary<string, JsonElement> context)
    {
        decimal ReadDecimal(string key, decimal fallback) =>
            context.TryGetValue(key, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
                    JsonValueKind.String when decimal.TryParse(
                        value.GetString(),
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out var textNumber) => textNumber,
                    _ => fallback
                }
                : fallback;

        return new Dictionary<string, object?>
        {
            ["budgetLimit"] = request.Budget,
            ["transport"] = ReadDecimal("transport", request.Budget * 0.25m),
            ["accommodation"] = ReadDecimal("accommodation", request.Budget * 0.28m),
            ["food"] = ReadDecimal("food", request.Budget * 0.2m),
            ["tickets"] = ReadDecimal("tickets", request.Budget * 0.12m),
            ["other"] = ReadDecimal("other", request.Budget * 0.05m)
        };
    }

    [GeneratedRegex("<request>(.*?)</request>", RegexOptions.Singleline)]
    private static partial Regex RequestRegex();

    [GeneratedRegex("<shared-context>(.*?)</shared-context>", RegexOptions.Singleline)]
    private static partial Regex ContextRegex();
}
