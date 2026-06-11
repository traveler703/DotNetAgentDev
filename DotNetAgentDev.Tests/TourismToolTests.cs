using System.Text.Json;
using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetAgentDev.Tests;

public sealed class TourismToolTests
{
    [Fact]
    public async Task AttractionSearch_ReturnsPreferenceRankedJapaneseCandidates()
    {
        var catalog = CreateCatalog();
        var tool = new AttractionSearchTool(catalog);
        var result = await tool.ExecuteAsync(
            """{"destination":"日本","preferences":"美食、轻松","maxResults":8}""",
            CancellationToken.None);
        using var document = JsonDocument.Parse(result.Content);
        var candidates = document.RootElement.GetProperty("candidates");

        Assert.True(result.Success);
        Assert.Equal(8, candidates.GetArrayLength());
        Assert.Contains(
            candidates.EnumerateArray(),
            item => item.GetProperty("city").GetString() == "东京");
        Assert.Contains(
            candidates.EnumerateArray(),
            item => item.GetProperty("city").GetString() == "京都");
    }

    [Fact]
    public async Task HotelSearch_UsesGenericFallback_ForUnknownDestination()
    {
        var catalog = CreateCatalog();
        var tool = new HotelSearchTool(catalog);
        var result = await tool.ExecuteAsync(
            """{"destination":"测试城","nightlyBudget":500,"preferences":"交通方便"}""",
            CancellationToken.None);
        using var document = JsonDocument.Parse(result.Content);

        Assert.True(result.Success);
        Assert.Equal(3, document.RootElement.GetProperty("hotels").GetArrayLength());
    }

    private static TourismCatalog CreateCatalog()
    {
        var projectPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DotNetAgentDev"));
        var environment = new TestWebHostEnvironment
        {
            ContentRootPath = projectPath,
            WebRootPath = Path.Combine(projectPath, "wwwroot")
        };
        return new TourismCatalog(environment, NullLogger<TourismCatalog>.Instance);
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
