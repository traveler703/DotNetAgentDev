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
        if (!_deepSeek.IsConfigured || _fallbackActivated)
        {
            return await _offline.CompleteAsync(messages, tools, cancellationToken);
        }

        try
        {
            return await _deepSeek.CompleteAsync(messages, tools, cancellationToken);
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
            return await _offline.CompleteAsync(messages, tools, cancellationToken);
        }
    }
}
