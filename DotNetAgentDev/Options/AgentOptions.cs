namespace DotNetAgentDev.Options;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public int MaxSteps { get; init; } = 6;
    public string DataDirectory { get; init; } = "App_Data";
}
