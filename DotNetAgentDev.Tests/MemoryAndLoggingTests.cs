using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;
using DotNetAgentDev.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetAgentDev.Tests;

public sealed class MemoryAndLoggingTests
{
    [Fact]
    public async Task SavePlanAsync_StoresPacePreferencesAndNotesWithoutFrequentDestinations()
    {
        var root = CreateTemporaryDirectory();
        var store = new PlanningMemoryStore(
            new TestWebHostEnvironment { ContentRootPath = root },
            Microsoft.Extensions.Options.Options.Create(new AgentOptions { DataDirectory = "App_Data" }),
            NullLogger<PlanningMemoryStore>.Instance);
        var plan = CreatePlan();

        await store.SavePlanAsync(plan, CancellationToken.None);
        var profile = await store.GetProfileAsync("memory-test", CancellationToken.None);
        var profileJson = await File.ReadAllTextAsync(
            Path.Combine(root, "App_Data", "profiles.json"),
            CancellationToken.None);

        Assert.Contains("充实", profile.TravelPaces);
        Assert.Contains("美食", profile.Preferences);
        Assert.Contains("人文", profile.Preferences);
        Assert.Contains("不要早起，酒店靠地铁", profile.Notes);
        Assert.DoesNotContain("frequentDestinations", profileJson);
    }

    [Fact]
    public async Task SavePlanAsync_PersistsRevisionContext()
    {
        var root = CreateTemporaryDirectory();
        var store = new PlanningMemoryStore(
            new TestWebHostEnvironment { ContentRootPath = root },
            Microsoft.Extensions.Options.Options.Create(new AgentOptions { DataDirectory = "App_Data" }),
            NullLogger<PlanningMemoryStore>.Instance);
        var previousPlanId = Guid.NewGuid();
        var original = CreatePlan();
        var plan = original with
        {
            Request = original.Request with
            {
                PreviousPlanId = previousPlanId,
                RevisionNumber = 1,
                RevisionInstruction = "把故宫博物院改到第 2 天上午",
                PreviousPlanSummary = "上一版计划摘要"
            }
        };

        await store.SavePlanAsync(plan, CancellationToken.None);
        var saved = await store.GetPlanAsync(plan.Id, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(previousPlanId, saved.Request.PreviousPlanId);
        Assert.Equal(1, saved.Request.RevisionNumber);
        Assert.Equal("把故宫博物院改到第 2 天上午", saved.Request.RevisionInstruction);
        Assert.Equal("上一版计划摘要", saved.Request.PreviousPlanSummary);
    }

    [Fact]
    public void FileLoggerProvider_WritesLogFile()
    {
        var logDirectory = CreateTemporaryDirectory();
        using var provider = new FileLoggerProvider(logDirectory);
        var logger = provider.CreateLogger("DotNetAgentDev.Tests.FileLogger");

        logger.LogInformation("日志文件测试 {Value}", 42);

        var file = Assert.Single(Directory.EnumerateFiles(logDirectory, "*.log"));
        var content = File.ReadAllText(file);
        Assert.Contains("日志文件测试 42", content);
        Assert.Contains("DotNetAgentDev.Tests.FileLogger", content);
    }

    private static TravelPlan CreatePlan() =>
        new()
        {
            Request = new TravelRequest
            {
                UserId = "memory-test",
                Departure = "上海",
                Destination = "台湾",
                StartDate = new DateOnly(2026, 8, 1),
                Days = 3,
                Travelers = 1,
                Budget = 6000,
                Preferences = "美食、人文",
                Pace = TravelPace.Intensive,
                Notes = "不要早起，酒店靠地铁"
            },
            Title = "测试旅行计划",
            Summary = "测试用计划。",
            Days = [],
            Hotels = [],
            Transport = new TransportSummary("往返航班", "测试交通", 1000, 120, 0, 240, []),
            Budget = new BudgetBreakdown(3000, 6000, 1120, 900, 600, 200, 180, 3000, false, []),
            Risks = [],
            AdjustmentSuggestions = [],
            Trace = [],
            AgentContributions = []
        };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DotNetAgentDevTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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
