using System.Text;
using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;
using DotNetAgentDev.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetAgentDev.Tests;

public sealed class PlanningMemoryStoreTests
{
    [Fact]
    public async Task SavePlanAsync_WritesReadableChineseInsteadOfUnicodeEscapes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"travel-memory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var environment = new TestWebHostEnvironment { ContentRootPath = root };
            var store = new PlanningMemoryStore(
                environment,
                Microsoft.Extensions.Options.Options.Create(
                    new AgentOptions { DataDirectory = "App_Data" }),
                NullLogger<PlanningMemoryStore>.Instance);
            var plan = CreatePlan();

            await store.SavePlanAsync(plan, CancellationToken.None);

            var path = Path.Combine(root, "App_Data", "plans", $"{plan.Id:N}.json");
            var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
            Assert.Contains("上海", json);
            Assert.Contains("日本", json);
            Assert.Contains("🏯", json);
            Assert.DoesNotContain(@"\u4E0A\u6D77", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\uD83C\uDFEF", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NormalizeStoredJsonAsync_DecodesUnicodeInsideEmbeddedJsonStrings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"travel-memory-{Guid.NewGuid():N}");
        var plans = Path.Combine(root, "App_Data", "plans");
        Directory.CreateDirectory(plans);
        var path = Path.Combine(plans, "legacy.json");
        await File.WriteAllTextAsync(
            path,
            """{"detail":"{\"destination\":\"\u65E5\u672C\",\"icon\":\"\uD83C\uDFEF\",\"summary\":\"完成\\u540...\"}","items":[{"name":"\u4E0A\u6D77"}]}""");
        try
        {
            var store = new PlanningMemoryStore(
                new TestWebHostEnvironment { ContentRootPath = root },
                Microsoft.Extensions.Options.Options.Create(
                    new AgentOptions { DataDirectory = "App_Data" }),
                NullLogger<PlanningMemoryStore>.Instance);

            await store.NormalizeStoredJsonAsync(CancellationToken.None);

            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("日本", json);
            Assert.Contains("上海", json);
            Assert.Contains("🏯", json);
            Assert.DoesNotContain(@"\u65E5\u672C", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\uD83C\uDFEF", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\u540", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TravelPlan CreatePlan()
    {
        var request = new TravelRequest
        {
            Departure = "上海",
            Destination = "日本",
            Days = 1,
            Travelers = 1,
            Budget = 5000
        };
        return new TravelPlan
        {
            Request = request,
            Title = "日本旅行",
            Summary = "参观东京主要景点 🏯。",
            Days = [],
            Hotels = [],
            Transport = new TransportSummary("航班", "测试", 0, 0, 0, 0, []),
            Budget = new BudgetBreakdown(0, 5000, 0, 0, 0, 0, 0, 5000, false, []),
            Risks = [],
            AdjustmentSuggestions = [],
            Trace = [],
            AgentContributions = []
        };
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
