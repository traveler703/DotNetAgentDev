using System.Net;
using System.Text;
using DotNetAgentDev.Agents;
using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Llm;
using DotNetAgentDev.Models;
using DotNetAgentDev.Options;
using DotNetAgentDev.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetAgentDev.Tests;

public sealed class TravelCoordinatorTests
{
    [Fact]
    public async Task PlanAsync_ReplansWhenInitialTotalExceedsBudgetByMoreThanTenPercent()
    {
        var coordinator = CreateCoordinator();
        var streamEvents = new List<PlanningStreamEvent>();
        var plan = await coordinator.PlanAsync(
            new TravelRequest
            {
                UserId = "budget-test",
                Departure = "上海",
                Destination = "台湾",
                StartDate = new DateOnly(2026, 7, 12),
                Days = 6,
                Travelers = 2,
                Budget = 3000,
                Preferences = "美食、人文、自然风光",
                Pace = TravelPace.Relaxed
            },
            CancellationToken.None,
            streamEvents.Add);

        Assert.Equal(1, plan.PlanningRevisionCount);
        Assert.Contains(plan.Trace, step => step.Title == "预算超限，启动第二轮规划");
        Assert.All(plan.Days, day => Assert.NotNull(day.CostBreakdown));
        Assert.Contains(
            plan.Days.SelectMany(day => day.Activities),
            activity => activity.Category == "餐饮" && activity.EndTime.Length > 0);
        Assert.Contains(plan.ExpenseDetails, detail => detail.Category == "transport");
        Assert.Contains(plan.ExpenseDetails, detail => detail.Category == "food");
        Assert.Contains(
            plan.Days.SelectMany(day => day.Activities),
            activity => activity.Name is "台北101观景台" or "台北故宫博物院" or "中正纪念堂");
        Assert.DoesNotContain(
            plan.Days.SelectMany(day => day.Activities),
            activity => activity.Name.Contains("城市历史博物馆")
                        || activity.Name.Contains("老城步行街")
                        || activity.Name.Contains("城市中央公园"));
        Assert.All(
            streamEvents.Where(item => item.Type == "delta"),
            item => Assert.False(string.IsNullOrWhiteSpace(item.MessageId)));
        Assert.All(
            streamEvents.Where(item => item.Phase == "Action" && item.Title.StartsWith("调用 ")),
            item => Assert.False(string.IsNullOrWhiteSpace(item.ToolName)));
        Assert.All(
            streamEvents.Where(item => item.Phase == "Observation" && item.Title.EndsWith("返回结果")),
            item => Assert.NotNull(item.Success));
    }

    [Fact]
    public async Task PlanAsync_UsesSelectedCitiesAndNamedPlaces_ForBroadDestination()
    {
        var plan = await CreateCoordinator().PlanAsync(
            new TravelRequest
            {
                UserId = "vietnam-test",
                Departure = "上海",
                Destination = "越南",
                StartDate = new DateOnly(2026, 8, 10),
                Days = 7,
                Travelers = 1,
                Budget = 12000,
                Preferences = "人文、美食、城市漫步",
                Pace = TravelPace.Balanced
            },
            CancellationToken.None);

        Assert.Equal(["河内", "岘港"], plan.Days.Select(day => day.City).Distinct());
        Assert.Contains(
            plan.Days.SelectMany(day => day.Activities),
            activity => activity.Name is "还剑湖与玉山祠" or "会安古城");
        Assert.DoesNotContain(
            plan.Days.SelectMany(day => day.Activities),
            activity => activity.Name.Contains("需联网确认"));
        Assert.All(plan.Hotels, hotel => Assert.Contains(hotel.City, new[] { "河内", "岘港" }));
        Assert.True(plan.Transport.IntercityCost > 0);
        var routeSelectedAt = plan.Trace.ToList().FindIndex(step =>
            step.Phase == "Observation" && step.Title == "route_sort 返回结果");
        var hotelStartedAt = plan.Trace.ToList().FindIndex(step =>
            step.Agent == "酒店 Agent" && step.Phase == "Thought");
        Assert.True(routeSelectedAt >= 0);
        Assert.True(hotelStartedAt > routeSelectedAt);
    }

    private static TravelCoordinatorAgent CreateCoordinator()
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
        var registry = new ToolRegistry(
            new AttractionSearchTool(catalog),
            new RouteSortTool(catalog),
            new HotelSearchTool(catalog),
            new TransportEstimateTool(catalog),
            new BudgetCalculatorTool(),
            new WeatherLookupTool(catalog),
            new RiskCheckTool(catalog),
            new PreferenceMemoryTool(memory),
            new TravelWebResearchTool(
                new StubHttpClientFactory(),
                NullLogger<TravelWebResearchTool>.Instance),
            NullLogger<ToolRegistry>.Instance);
        var loop = new AgentLoop(
            new OfflineAdapter(),
            registry,
            Microsoft.Extensions.Options.Options.Create(
                new AgentOptions { MaxSteps = 8, DataDirectory = "App_Data" }),
            NullLogger<AgentLoop>.Instance);

        return new TravelCoordinatorAgent(
            new ItineraryAgent(loop),
            new HotelAgent(loop),
            new TransportAgent(loop),
            new BudgetAgent(loop),
            new RiskAgent(loop),
            catalog,
            NullLogger<TravelCoordinatorAgent>.Instance);
    }

    private sealed class OfflineAdapter : ILlmClient
    {
        private readonly OfflineLlmClient _inner = new();

        public string CurrentMode => "offline";

        public Task<LlmResponse> CompleteAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            CancellationToken cancellationToken) =>
            _inner.CompleteAsync(messages, tools, cancellationToken);

        public Task<LlmResponse> CompleteStreamingAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition> tools,
            Action<string>? onContentDelta,
            CancellationToken cancellationToken) =>
            _inner.CompleteStreamingAsync(messages, tools, onContentDelta, cancellationToken);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string rss = """
                               <?xml version="1.0" encoding="utf-8"?>
                               <rss version="2.0"><channel>
                               <item><title>官方旅行资料</title><link>https://example.gov/travel</link>
                               <description>用于测试的官方旅行、交通、天气与安全摘要。</description>
                               <pubDate>Fri, 12 Jun 2026 03:00:00 GMT</pubDate></item>
                               </channel></rss>
                               """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(rss, Encoding.UTF8, "application/rss+xml")
            });
        }
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
