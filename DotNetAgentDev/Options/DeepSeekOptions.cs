namespace DotNetAgentDev.Options;

public sealed class DeepSeekOptions
{
    public const string SectionName = "DeepSeek";

    public string BaseUrl { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-v4-flash";
    public string? ApiKey { get; init; }
    public int TimeoutSeconds { get; init; } = 90;
    public int MaxTokens { get; init; } = 1800;
    public double Temperature { get; init; } = 0.3;
}
