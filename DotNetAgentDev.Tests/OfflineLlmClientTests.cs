using System.Text.Json;
using DotNetAgentDev.Llm;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tests;

public sealed class OfflineLlmClientTests
{
    [Fact]
    public async Task CompleteAsync_CallsEachAllowedToolThenStops()
    {
        var client = new OfflineLlmClient();
        var request = new TravelRequest
        {
            Departure = "上海",
            Destination = "日本",
            Days = 5,
            Budget = 9000
        };
        var requestJson = JsonSerializer.Serialize(
            request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = $"<request>{requestJson}</request>" }
        };
        var tools = new[]
        {
            Tool("attraction_search"),
            Tool("route_sort")
        };

        var first = await client.CompleteAsync(messages, tools, CancellationToken.None);
        Assert.Equal("attraction_search", Assert.Single(first.ToolCalls).Name);
        messages.Add(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = first.ToolCalls,
            Content = first.Content
        });
        messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolCallId = first.ToolCalls[0].Id,
            Content = "{}"
        });

        var second = await client.CompleteAsync(messages, tools, CancellationToken.None);
        Assert.Equal("route_sort", Assert.Single(second.ToolCalls).Name);
        messages.Add(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = second.ToolCalls,
            Content = second.Content
        });
        messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolCallId = second.ToolCalls[0].Id,
            Content = "{}"
        });

        var final = await client.CompleteAsync(messages, tools, CancellationToken.None);
        Assert.Empty(final.ToolCalls);
        Assert.Equal("stop", final.FinishReason);
    }

    private static ToolDefinition Tool(string name)
    {
        using var document = JsonDocument.Parse(
            """{"type":"object","properties":{},"additionalProperties":false}""");
        return new ToolDefinition(name, name, document.RootElement.Clone());
    }
}
