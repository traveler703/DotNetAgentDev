using DotNetAgentDev.Models;

namespace DotNetAgentDev.Agents;

public sealed class ItineraryAgent(AgentLoop loop)
{
    public Task<AgentRunResult> RunAsync(
        TravelRequest request,
        int sequenceStart,
        CancellationToken cancellationToken,
        Action<PlanningStreamEvent>? onProgress = null,
        string? revisionInstruction = null) =>
        loop.RunAsync(
            "行程规划 Agent",
            "结合联网资料、目的地、偏好与节奏设计具体到时段、交通、景点和餐饮的每日路线，避免重复和不合理绕路。",
            new AgentTaskContext(
                request,
                revisionInstruction is null
                    ? "联网查询景点、美食与交通时刻，确定候选体验和城市区域顺序"
                    : $"预算复核后的重新规划：{revisionInstruction}",
                revisionInstruction is null
                    ? null
                    : new Dictionary<string, string> { ["revisionInstruction"] = revisionInstruction }),
            ["preference_memory", "travel_web_research", "attraction_search", "route_sort"],
            sequenceStart,
            cancellationToken,
            onProgress);
}

public sealed class HotelAgent(AgentLoop loop)
{
    public Task<AgentRunResult> RunAsync(
        TravelRequest request,
        int sequenceStart,
        CancellationToken cancellationToken,
        Action<PlanningStreamEvent>? onProgress = null,
        string? revisionInstruction = null) =>
        loop.RunAsync(
            "酒店 Agent",
            "根据总预算、城市与路线区域推荐交通方便且不会挤压核心行程费用的住宿。",
            new AgentTaskContext(
                request,
                revisionInstruction is null
                    ? "为各停留城市查询不同价位住宿"
                    : $"预算复核后的住宿重选：{revisionInstruction}"),
            ["hotel_search"],
            sequenceStart,
            cancellationToken,
            onProgress);
}

public sealed class TransportAgent(AgentLoop loop)
{
    public Task<AgentRunResult> RunAsync(
        TravelRequest request,
        int sequenceStart,
        CancellationToken cancellationToken,
        Action<PlanningStreamEvent>? onProgress = null,
        string? revisionInstruction = null) =>
        loop.RunAsync(
            "交通 Agent",
            "结合联网交通资料估算大交通、市内交通和跨城移动成本，给出具体建议时段并检查路线耗时。",
            new AgentTaskContext(
                request,
                revisionInstruction is null
                    ? "联网查询交通时刻并估算完整旅行交通方案"
                    : $"预算复核后的交通重排：{revisionInstruction}"),
            ["travel_web_research", "transport_estimate"],
            sequenceStart,
            cancellationToken,
            onProgress);
}

public sealed class RiskAgent(AgentLoop loop)
{
    public Task<AgentRunResult> RunAsync(
        TravelRequest request,
        int sequenceStart,
        CancellationToken cancellationToken,
        Action<PlanningStreamEvent>? onProgress = null) =>
        loop.RunAsync(
            "风险 Agent",
            "必须先联网检索，再检查签证、天气、自然灾害、社会治安和行程强度风险，并保留来源。",
            new AgentTaskContext(request, "联网查询签证、天气、自然灾害和社会治安，再执行综合风险检查"),
            ["travel_web_research", "weather_lookup", "risk_check"],
            sequenceStart,
            cancellationToken,
            onProgress);
}

public sealed class BudgetAgent(AgentLoop loop)
{
    public Task<AgentRunResult> RunAsync(
        TravelRequest request,
        IReadOnlyDictionary<string, string> costContext,
        int sequenceStart,
        CancellationToken cancellationToken,
        Action<PlanningStreamEvent>? onProgress = null) =>
        loop.RunAsync(
            "预算 Agent",
            "汇总费用、判断是否超支，并提出优先级明确的优化建议。",
            new AgentTaskContext(request, "校验专业 Agent 汇总后的总预算", costContext),
            ["budget_calculator"],
            sequenceStart,
            cancellationToken,
            onProgress);
}
