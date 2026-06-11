using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class ToolRegistry
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(
        AttractionSearchTool attractionSearch,
        RouteSortTool routeSort,
        HotelSearchTool hotelSearch,
        TransportEstimateTool transportEstimate,
        BudgetCalculatorTool budgetCalculator,
        WeatherLookupTool weatherLookup,
        RiskCheckTool riskCheck,
        PreferenceMemoryTool preferenceMemory,
        ILogger<ToolRegistry> logger)
    {
        var tools = new IAgentTool[]
        {
            attractionSearch,
            routeSort,
            hotelSearch,
            transportEstimate,
            budgetCalculator,
            weatherLookup,
            riskCheck,
            preferenceMemory
        };
        _tools = tools.ToDictionary(tool => tool.Definition.Name, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public ToolDefinition GetDefinition(string name) =>
        _tools.TryGetValue(name, out var tool)
            ? tool.Definition
            : throw new KeyNotFoundException($"未注册工具：{name}");

    public async Task<ToolExecutionResult> ExecuteAsync(
        string name,
        string arguments,
        CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            return new ToolExecutionResult(false, "{}", $"模型请求了未注册工具：{name}");
        }

        try
        {
            _logger.LogDebug("Executing tool {ToolName} with {Arguments}.", name, arguments);
            return await tool.ExecuteAsync(arguments, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Tool {ToolName} failed.", name);
            return ToolSupport.Failure(exception);
        }
    }
}
