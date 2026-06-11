using DotNetAgentDev.Models;

namespace DotNetAgentDev.Agents;

public sealed class ItineraryAgent(AgentLoop loop)
{
    public Task<AgentRunResult> RunAsync(
        TravelRequest request,
        int sequenceStart,
        CancellationToken cancellationToken) =>
        loop.RunAsync(
            "行程规划 Agent",
            "结合目的地、偏好与节奏设计每日路线，避免重复和不合理绕路。",
            new AgentTaskContext(request, "查询候选体验并确定城市与区域顺序"),
            ["preference_memory", "attraction_search", "route_sort"],
            sequenceStart,
            cancellationToken);
}

public sealed class HotelAgent(AgentLoop loop)
{
    public Task<AgentRunResult> RunAsync(
        TravelRequest request,
        int sequenceStart,
        CancellationToken cancellationToken) =>
        loop.RunAsync(
            "酒店 Agent",
            "根据预算、城市与路线区域推荐交通方便的住宿。",
            new AgentTaskContext(request, "为各停留城市查询不同价位住宿"),
            ["hotel_search"],
            sequenceStart,
            cancellationToken);
}

public sealed class TransportAgent(AgentLoop loop)
{
    public Task<AgentRunResult> RunAsync(
        TravelRequest request,
        int sequenceStart,
        CancellationToken cancellationToken) =>
        loop.RunAsync(
            "交通 Agent",
            "估算大交通、市内交通和跨城移动成本，检查路线耗时。",
            new AgentTaskContext(request, "估算完整旅行交通方案"),
            ["transport_estimate"],
            sequenceStart,
            cancellationToken);
}

public sealed class RiskAgent(AgentLoop loop)
{
    public Task<AgentRunResult> RunAsync(
        TravelRequest request,
        int sequenceStart,
        CancellationToken cancellationToken) =>
        loop.RunAsync(
            "风险 Agent",
            "检查季节天气、签证、安全和行程强度风险。",
            new AgentTaskContext(request, "查询天气参考并执行综合风险检查"),
            ["weather_lookup", "risk_check"],
            sequenceStart,
            cancellationToken);
}

public sealed class BudgetAgent(AgentLoop loop)
{
    public Task<AgentRunResult> RunAsync(
        TravelRequest request,
        IReadOnlyDictionary<string, string> costContext,
        int sequenceStart,
        CancellationToken cancellationToken) =>
        loop.RunAsync(
            "预算 Agent",
            "汇总费用、判断是否超支，并提出优先级明确的优化建议。",
            new AgentTaskContext(request, "校验专业 Agent 汇总后的总预算", costContext),
            ["budget_calculator"],
            sequenceStart,
            cancellationToken);
}
