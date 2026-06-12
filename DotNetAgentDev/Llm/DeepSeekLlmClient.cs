using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotNetAgentDev.Models;
using DotNetAgentDev.Options;
using Microsoft.Extensions.Options;

namespace DotNetAgentDev.Llm;

public sealed class DeepSeekLlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly DeepSeekOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DeepSeekLlmClient> _logger;

    public DeepSeekLlmClient(
        HttpClient httpClient,
        IOptions<DeepSeekOptions> options,
        IConfiguration configuration,
        ILogger<DeepSeekLlmClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetApiKey());

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(messages, tools, stream: false);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, responseText);
        return ParseResponse(responseText);
    }

    public async Task<LlmResponse> CompleteStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        Action<string>? onContentDelta,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(messages, tools, stream: true);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureSuccess(response, errorText);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var content = new StringBuilder();
        var pendingDelta = new StringBuilder();
        var toolCalls = new Dictionary<int, StreamingToolCall>();
        var finishReason = "stop";
        var model = _options.Model;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
            {
                break;
            }

            if (data.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("model", out var modelElement))
            {
                model = modelElement.GetString() ?? model;
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finishElement)
                && finishElement.ValueKind == JsonValueKind.String)
            {
                finishReason = finishElement.GetString() ?? finishReason;
            }

            if (!choice.TryGetProperty("delta", out var delta))
            {
                continue;
            }

            if (delta.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                var piece = contentElement.GetString();
                if (!string.IsNullOrEmpty(piece))
                {
                    content.Append(piece);
                    pendingDelta.Append(piece);
                    if (pendingDelta.Length >= 24 || piece.Contains('\n'))
                    {
                        onContentDelta?.Invoke(pendingDelta.ToString());
                        pendingDelta.Clear();
                    }
                }
            }

            AccumulateToolCalls(delta, toolCalls);
        }

        if (pendingDelta.Length > 0)
        {
            onContentDelta?.Invoke(pendingDelta.ToString());
        }

        return new LlmResponse(
            content.Length == 0 ? null : content.ToString(),
            toolCalls.OrderBy(pair => pair.Key)
                .Select(pair => pair.Value.Build())
                .ToList(),
            finishReason,
            model);
    }

    private HttpRequestMessage CreateRequest(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        bool stream)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("DeepSeek API Key 未配置。");
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = messages.Select(ToApiMessage).ToList(),
            ["stream"] = stream,
            ["temperature"] = _options.Temperature,
            ["max_tokens"] = _options.MaxTokens,
            ["thinking"] = new { type = "disabled" }
        };

        if (tools.Count > 0)
        {
            body["tools"] = tools.Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.Parameters
                }
            }).ToList();
            body["tool_choice"] = "auto";
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    private void EnsureSuccess(HttpResponseMessage response, string responseText)
    {
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "DeepSeek request failed with {StatusCode}: {Response}",
                response.StatusCode,
                responseText);
            throw new HttpRequestException(
                $"DeepSeek API 返回 {(int)response.StatusCode}：{ExtractError(responseText)}");
        }
    }

    private LlmResponse ParseResponse(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement)
                      && contentElement.ValueKind != JsonValueKind.Null
            ? contentElement.GetString()
            : null;
        var toolCalls = ParseToolCalls(message);
        var finishReason = choice.TryGetProperty("finish_reason", out var finishElement)
            ? finishElement.GetString() ?? "stop"
            : "stop";
        var model = root.TryGetProperty("model", out var modelElement)
            ? modelElement.GetString() ?? _options.Model
            : _options.Model;

        return new LlmResponse(content, toolCalls, finishReason, model);
    }

    private string? GetApiKey() =>
        _configuration["DEEPSEEK_API_KEY"]
        ?? _configuration[$"{DeepSeekOptions.SectionName}:ApiKey"]
        ?? _options.ApiKey;

    private static object ToApiMessage(ChatMessage message)
    {
        if (message.Role == "tool")
        {
            return new
            {
                role = "tool",
                content = message.Content ?? string.Empty,
                tool_call_id = message.ToolCallId
            };
        }

        if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role = "assistant",
                content = message.Content,
                tool_calls = message.ToolCalls.Select(call => new
                {
                    id = call.Id,
                    type = "function",
                    function = new
                    {
                        name = call.Name,
                        arguments = call.Arguments
                    }
                }).ToList()
            };
        }

        return new
        {
            role = message.Role,
            content = message.Content ?? string.Empty
        };
    }

    private static IReadOnlyList<LlmToolCall> ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var callsElement)
            || callsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var calls = new List<LlmToolCall>();
        foreach (var call in callsElement.EnumerateArray())
        {
            var function = call.GetProperty("function");
            calls.Add(new LlmToolCall(
                call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                function.GetProperty("name").GetString() ?? string.Empty,
                function.GetProperty("arguments").GetString() ?? "{}"));
        }

        return calls;
    }

    private static void AccumulateToolCalls(
        JsonElement delta,
        IDictionary<int, StreamingToolCall> toolCalls)
    {
        if (!delta.TryGetProperty("tool_calls", out var callsElement)
            || callsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var call in callsElement.EnumerateArray())
        {
            var index = call.TryGetProperty("index", out var indexElement)
                ? indexElement.GetInt32()
                : toolCalls.Count;
            if (!toolCalls.TryGetValue(index, out var accumulator))
            {
                accumulator = new StreamingToolCall();
                toolCalls[index] = accumulator;
            }

            if (call.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String)
            {
                accumulator.Id.Append(idElement.GetString());
            }

            if (!call.TryGetProperty("function", out var function))
            {
                continue;
            }

            if (function.TryGetProperty("name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String)
            {
                accumulator.Name.Append(nameElement.GetString());
            }

            if (function.TryGetProperty("arguments", out var argumentsElement)
                && argumentsElement.ValueKind == JsonValueKind.String)
            {
                accumulator.Arguments.Append(argumentsElement.GetString());
            }
        }
    }

    private static string ExtractError(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement
                       .GetProperty("error")
                       .GetProperty("message")
                       .GetString()
                   ?? response;
        }
        catch (JsonException)
        {
            return response.Length > 300 ? response[..300] : response;
        }
    }

    private sealed class StreamingToolCall
    {
        public StringBuilder Id { get; } = new();
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();

        public LlmToolCall Build() => new(
            Id.Length == 0 ? Guid.NewGuid().ToString("N") : Id.ToString(),
            Name.ToString(),
            Arguments.Length == 0 ? "{}" : Arguments.ToString());
    }
}
