using System.ComponentModel;
using System.Text.Json;
using DotNetAgentDev.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DotNetAgentDev.Mcp;

[McpServerToolType]
public static class TravelMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(
        Name = "attraction_search",
        Title = "景点与体验搜索",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("根据目的地和旅行偏好查询景点、美食、商圈与休闲候选点。数据来自项目本地旅游知识库。")]
    public static Task<string> AttractionSearchAsync(
        ToolRegistry registry,
        [Description("目的地国家或城市，例如“日本”“东京”“杭州”。")] string destination,
        [Description("用户偏好，例如“美食、人文、轻松、夜景”。")] string preferences,
        [Description("返回候选数量，范围 1 到 20。")] int maxResults,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            registry,
            "attraction_search",
            new { destination, preferences, maxResults },
            cancellationToken);

    [McpServerTool(
        Name = "route_sort",
        Title = "旅行路线排序",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("按目的地、偏好、行程节奏和天数生成城市顺序、区域顺序与每日活动密度建议。")]
    public static Task<string> RouteSortAsync(
        ToolRegistry registry,
        [Description("目的地国家或城市。")] string destination,
        [Description("用户旅行偏好。")] string preferences,
        [Description("行程节奏，只能是 Relaxed、Balanced 或 Intensive。")] string pace,
        [Description("旅行天数，范围 1 到 30。")] int days,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            registry,
            "route_sort",
            new { destination, preferences, pace, days },
            cancellationToken);

    [McpServerTool(
        Name = "hotel_search",
        Title = "住宿建议搜索",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("按目的地、每晚预算和住宿偏好查询酒店区域与住宿建议。")]
    public static Task<string> HotelSearchAsync(
        ToolRegistry registry,
        [Description("目的地国家或城市。")] string destination,
        [Description("每间房每晚预算，单位为人民币元。")] decimal nightlyBudget,
        [Description("住宿偏好，例如“靠近地铁、安静、交通方便”。")] string preferences,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            registry,
            "hotel_search",
            new { destination, nightlyBudget, preferences },
            cancellationToken);

    [McpServerTool(
        Name = "transport_estimate",
        Title = "交通费用与时间估算",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("估算出发地到目的地的大交通、跨城和市内交通费用与耗时。")]
    public static Task<string> TransportEstimateAsync(
        ToolRegistry registry,
        [Description("旅行出发地。")] string departure,
        [Description("旅行目的地。")] string destination,
        [Description("旅行天数，范围 1 到 30。")] int days,
        [Description("出行人数，范围 1 到 20。")] int travelers,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            registry,
            "transport_estimate",
            new { departure, destination, days, travelers },
            cancellationToken);

    [McpServerTool(
        Name = "budget_calculator",
        Title = "旅行预算计算",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("汇总旅行费用，检查是否超出预算，并针对最大费用项给出优化建议。所有金额单位为人民币元。")]
    public static Task<string> BudgetCalculatorAsync(
        ToolRegistry registry,
        [Description("旅行总预算上限。")] decimal budgetLimit,
        [Description("大交通、跨城和市内交通总费用。")] decimal transport,
        [Description("住宿总费用。")] decimal accommodation,
        [Description("餐饮总费用。")] decimal food,
        [Description("景点门票总费用。")] decimal tickets,
        [Description("保险、通信、购物预留等其他费用。")] decimal other,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            registry,
            "budget_calculator",
            new { budgetLimit, transport, accommodation, food, tickets, other },
            cancellationToken);

    [McpServerTool(
        Name = "weather_lookup",
        Title = "季节天气查询",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("按目的地和出发日期查询本地知识库中的季节性天气参考。结果不是实时天气预报。")]
    public static Task<string> WeatherLookupAsync(
        ToolRegistry registry,
        [Description("目的地国家或城市。")] string destination,
        [Description("出发日期，格式为 yyyy-MM-dd。")] string startDate,
        [Description("旅行天数，范围 1 到 30。")] int days,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            registry,
            "weather_lookup",
            new { destination, startDate, days },
            cancellationToken);

    [McpServerTool(
        Name = "risk_check",
        Title = "旅行风险检查",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("检查签证、安全、季节、行程强度和个性化备注中的旅行风险。政策信息必须在出行前通过官方渠道复核。")]
    public static Task<string> RiskCheckAsync(
        ToolRegistry registry,
        [Description("目的地国家或城市。")] string destination,
        [Description("旅行天数，范围 1 到 30。")] int days,
        [Description("行程节奏，只能是 Relaxed、Balanced 或 Intensive。")] string pace,
        [Description("健康、饮食、无障碍或时间等补充约束；没有时传空字符串。")] string notes,
        [Description("出发日期，格式为 yyyy-MM-dd。")] string startDate,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            registry,
            "risk_check",
            new { destination, days, pace, notes, startDate },
            cancellationToken);

    [McpServerTool(
        Name = "preference_memory",
        Title = "用户旅行偏好记忆",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("读取本项目保存的用户长期旅行偏好、常去目的地和历史日均预算。当前输入始终优先于历史记忆。")]
    public static Task<string> PreferenceMemoryAsync(
        ToolRegistry registry,
        [Description("用户标识；演示时可使用 demo-user。")] string userId,
        [Description("本次旅行偏好。")] string preferences,
        [Description("本次旅行目的地。")] string destination,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            registry,
            "preference_memory",
            new { userId, preferences, destination },
            cancellationToken);

    [McpServerTool(
        Name = "travel_web_research",
        Title = "旅游网页联网检索",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true)]
    [Description("联网检索景点、美食、交通、签证、天气、自然灾害和社会治安资料，返回摘要与来源链接。")]
    public static Task<string> TravelWebResearchAsync(
        ToolRegistry registry,
        [Description("旅行出发地。")] string departure,
        [Description("旅行目的地。")] string destination,
        [Description("出发日期，格式为 yyyy-MM-dd。")] string startDate,
        [Description("旅行天数，范围 1 到 30。")] int days,
        [Description("逗号分隔主题：itinerary,transport,food,visa,weather,disaster,safety。")] string topics,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            registry,
            "travel_web_research",
            new { departure, destination, startDate, days, topics },
            cancellationToken);

    private static async Task<string> ExecuteAsync(
        ToolRegistry registry,
        string toolName,
        object arguments,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(arguments, JsonOptions);
        var result = await registry.ExecuteAsync(toolName, json, cancellationToken);
        if (!result.Success)
        {
            throw new McpException(result.Error ?? $"工具 {toolName} 执行失败。");
        }

        return result.Content;
    }
}
