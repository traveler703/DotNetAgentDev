using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public interface IAgentTool
{
    ToolDefinition Definition { get; }

    Task<ToolExecutionResult> ExecuteAsync(
        string arguments,
        CancellationToken cancellationToken);
}
