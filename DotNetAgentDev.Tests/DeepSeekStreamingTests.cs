using System.Net;
using System.Text;
using DotNetAgentDev.Llm;
using DotNetAgentDev.Models;
using DotNetAgentDev.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetAgentDev.Tests;

public sealed class DeepSeekStreamingTests
{
    [Fact]
    public async Task CompleteStreamingAsync_ReassemblesContentAndToolCallFragments()
    {
        const string sse = """
                           data: {"model":"deepseek-test","choices":[{"delta":{"content":"正在生成较长的流式文本片段，用于测试内容回调。","tool_calls":[{"index":0,"id":"call_","function":{"name":"hotel_","arguments":"{"}}]},"finish_reason":null}]}

                           data: {"model":"deepseek-test","choices":[{"delta":{"content":"完成。","tool_calls":[{"index":0,"id":"123","function":{"name":"search","arguments":"}"}}]},"finish_reason":"tool_calls"}]}

                           data: [DONE]

                           """;
        var handler = new StubHandler(sse);
        var options = Microsoft.Extensions.Options.Options.Create(new DeepSeekOptions
        {
            ApiKey = "test-key",
            Model = "deepseek-test"
        });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DEEPSEEK_API_KEY"] = "test-key"
            })
            .Build();
        var client = new DeepSeekLlmClient(
            new HttpClient(handler),
            options,
            configuration,
            NullLogger<DeepSeekLlmClient>.Instance);
        var deltas = new List<string>();

        var response = await client.CompleteStreamingAsync(
            [new ChatMessage { Role = "user", Content = "test" }],
            [],
            deltas.Add,
            CancellationToken.None);

        Assert.Equal("正在生成较长的流式文本片段，用于测试内容回调。完成。", response.Content);
        var toolCall = Assert.Single(response.ToolCalls);
        Assert.Equal("call_123", toolCall.Id);
        Assert.Equal("hotel_search", toolCall.Name);
        Assert.Equal("{}", toolCall.Arguments);
        Assert.Equal("tool_calls", response.FinishReason);
        Assert.Equal("deepseek-test", response.Model);
        Assert.Equal(response.Content, string.Concat(deltas));
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Contains(
                "\"stream\":true",
                request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream")
            });
        }
    }
}
