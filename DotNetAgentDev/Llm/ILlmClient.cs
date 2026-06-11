using DotNetAgentDev.Models;

namespace DotNetAgentDev.Llm;

public interface ILlmClient
{
    string CurrentMode { get; }

    Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken);
}
