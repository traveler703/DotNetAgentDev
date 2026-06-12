using System.Reflection;
using System.Text.Json;
using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Mcp;
using DotNetAgentDev.Options;
using DotNetAgentDev.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DotNetAgentDev.Tests;

public sealed class McpToolTests
{
    [Fact]
    public void TravelMcpTools_ExposeAllEightProjectTools()
    {
        var names = typeof(TravelMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(8, names.Count);
        Assert.Contains("attraction_search", names);
        Assert.Contains("route_sort", names);
        Assert.Contains("hotel_search", names);
        Assert.Contains("transport_estimate", names);
        Assert.Contains("budget_calculator", names);
        Assert.Contains("weather_lookup", names);
        Assert.Contains("risk_check", names);
        Assert.Contains("preference_memory", names);
    }

    [Fact]
    public async Task AttractionSearch_DelegatesToSharedToolRegistry()
    {
        var result = await TravelMcpTools.AttractionSearchAsync(
            CreateRegistry(),
            "日本",
            "美食、轻松",
            5,
            CancellationToken.None);
        using var document = JsonDocument.Parse(result);

        Assert.Equal(
            "local-tourism-knowledge-base",
            document.RootElement.GetProperty("source").GetString());
        Assert.Equal(5, document.RootElement.GetProperty("candidates").GetArrayLength());
    }

    private static ToolRegistry CreateRegistry()
    {
        var projectPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DotNetAgentDev"));
        var environment = new TestWebHostEnvironment
        {
            ContentRootPath = projectPath,
            WebRootPath = Path.Combine(projectPath, "wwwroot")
        };
        var catalog = new TourismCatalog(environment, NullLogger<TourismCatalog>.Instance);
        var memory = new PlanningMemoryStore(
            environment,
            Microsoft.Extensions.Options.Options.Create(
                new AgentOptions { DataDirectory = "App_Data" }),
            NullLogger<PlanningMemoryStore>.Instance);

        return new ToolRegistry(
            new AttractionSearchTool(catalog),
            new RouteSortTool(catalog),
            new HotelSearchTool(catalog),
            new TransportEstimateTool(catalog),
            new BudgetCalculatorTool(),
            new WeatherLookupTool(catalog),
            new RiskCheckTool(catalog),
            new PreferenceMemoryTool(memory),
            NullLogger<ToolRegistry>.Instance);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "DotNetAgentDev.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
