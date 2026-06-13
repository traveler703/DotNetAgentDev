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

    [Theory]
    [InlineData("香港", "K11 MUSEA", "尖沙咀海滨花园与星光大道")]
    [InlineData("台湾", "台北101观景台", "台北故宫博物院")]
    [InlineData("越南", "还剑湖与玉山祠", "会安古城")]
    public async Task AttractionSearch_ReturnsRealNamedPlaces_ForCuratedDestinations(
        string destination,
        string expectedFirst,
        string expectedSecond)
    {
        var tool = new AttractionSearchTool(CreateCatalog());
        var result = await tool.ExecuteAsync(
            $$"""{"destination":"{{destination}}","preferences":"人文、夜景、城市漫步","maxResults":14}""",
            CancellationToken.None);
        using var document = JsonDocument.Parse(result.Content);
        var names = document.RootElement.GetProperty("candidates")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToList();

        Assert.True(result.Success);
        Assert.Equal("curated-named-destination-fallback",
            document.RootElement.GetProperty("source").GetString());
        Assert.Contains(expectedFirst, names);
        Assert.Contains(expectedSecond, names);
        Assert.DoesNotContain(names, name => name?.Contains("城市历史博物馆") == true);
        Assert.DoesNotContain(names, name => name?.Contains("老城步行街") == true);
    }

    [Fact]
    public async Task RouteSort_SelectsLimitedCitiesBeforePlanningBroadDestination()
    {
        var tool = new RouteSortTool(CreateCatalog());
        var result = await tool.ExecuteAsync(
            """
            {
              "destination":"越南",
              "preferences":"人文、美食",
              "pace":"Balanced",
              "days":7,
              "budget":12000,
              "travelers":1
            }
            """,
            CancellationToken.None);
        using var document = JsonDocument.Parse(result.Content);
        var cities = document.RootElement.GetProperty("orderedCities")
            .EnumerateArray()
            .ToList();

        Assert.True(result.Success);
        Assert.Equal(2, cities.Count);
        Assert.Equal("河内", cities[0].GetProperty("city").GetString());
        Assert.Equal("岘港", cities[1].GetProperty("city").GetString());
        Assert.Equal(7, cities.Sum(city => city.GetProperty("recommendedDays").GetInt32()));
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
