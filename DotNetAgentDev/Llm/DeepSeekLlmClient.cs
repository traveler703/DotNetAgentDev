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
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("DeepSeek API Key 未配置。");
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["messages"] = messages.Select(ToApiMessage).ToList(),
            ["stream"] = false,
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

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "DeepSeek request failed with {StatusCode}: {Response}",
                response.StatusCode,
                responseText);
            throw new HttpRequestException(
                $"DeepSeek API 返回 {(int)response.StatusCode}：{ExtractError(responseText)}");
        }

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
}
