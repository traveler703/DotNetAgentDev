using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class PreferenceMemoryTool(PlanningMemoryStore memory) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "preference_memory",
        "读取用户长期记忆中的历史偏好、常去目的地和日均预算。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "userId": { "type": "string" },
                               "preferences": { "type": "string" },
                               "destination": { "type": "string" }
                             },
                             "required": ["userId", "preferences", "destination"],
                             "additionalProperties": false
                           }
                           """));

    public async Task<ToolExecutionResult> ExecuteAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var input = ToolSupport.Parse<Input>(arguments);
        var profile = await memory.GetProfileAsync(input.UserId, cancellationToken);
        return ToolSupport.Success(new
        {
            profile,
            currentPreferences = input.Preferences,
            currentDestination = input.Destination,
            memoryHint = profile.PlanCount == 0
                ? "这是该用户的首份计划，完成后会写入长期记忆。"
                : "规划时可参考历史偏好，但当前输入始终具有更高优先级。"
        });
    }

    private sealed record Input(string UserId, string Preferences, string Destination);
}
