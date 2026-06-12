using DotNetAgentDev.Models;

namespace DotNetAgentDev.Llm;

public sealed class ResilientLlmClient : ILlmClient
{
    private readonly DeepSeekLlmClient _deepSeek;
    private readonly OfflineLlmClient _offline;
    private readonly ILogger<ResilientLlmClient> _logger;
    private volatile bool _fallbackActivated;

    public ResilientLlmClient(
        DeepSeekLlmClient deepSeek,
        OfflineLlmClient offline,
        ILogger<ResilientLlmClient> logger)
    {
        _deepSeek = deepSeek;
        _offline = offline;
        _logger = logger;
    }

    public string CurrentMode =>
        _deepSeek.IsConfigured && !_fallbackActivated ? "deepseek" : "offline";

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        return await CompleteWithFallbackAsync(
            messages,
            tools,
            null,
            useStreaming: false,
            cancellationToken);
    }

    public async Task<LlmResponse> CompleteStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        Action<string>? onContentDelta,
        CancellationToken cancellationToken)
    {
        return await CompleteWithFallbackAsync(
            messages,
            tools,
            onContentDelta,
            useStreaming: true,
            cancellationToken);
    }

    private async Task<LlmResponse> CompleteWithFallbackAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        Action<string>? onContentDelta,
        bool useStreaming,
        CancellationToken cancellationToken)
    {
        if (!_deepSeek.IsConfigured || _fallbackActivated)
        {
            return useStreaming
                ? await _offline.CompleteStreamingAsync(
                    messages,
                    tools,
                    onContentDelta,
                    cancellationToken)
                : await _offline.CompleteAsync(messages, tools, cancellationToken);
        }

        try
        {
            return useStreaming
                ? await _deepSeek.CompleteStreamingAsync(
                    messages,
                    tools,
                    onContentDelta,
                    cancellationToken)
                : await _deepSeek.CompleteAsync(messages, tools, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _fallbackActivated = true;
            _logger.LogWarning(
                exception,
                "DeepSeek unavailable. Switching to the deterministic offline agent engine.");
            return useStreaming
                ? await _offline.CompleteStreamingAsync(
                    messages,
                    tools,
                    onContentDelta,
                    cancellationToken)
                : await _offline.CompleteAsync(messages, tools, cancellationToken);
        }
    }
}
