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
            "结合联网资料、目的地、偏好与节奏设计具体到时段、交通、真实命名景点和餐饮的每日路线。"
            + "必须给出可在地图中搜索到的景点专名，按相邻区域组合并避免重复和不合理绕路。"
            + "若目的地是国家或大区域，必须先选择适合本次天数与预算的核心城市，通常不超过3个，"
            + "只有时间和预算明显充足时才可增加；后续景点、酒店和交通必须围绕已选城市。",
            new AgentTaskContext(
                request,
                revisionInstruction is null
                    ? "联网查询真实景点、美食与交通时刻，列出具体专有名称、所在区域、建议游览时长和城市区域顺序"
                    : $"预算复核后的重新规划：{revisionInstruction}",
                revisionInstruction is null
                    ? null
                    : new Dictionary<string, string> { ["revisionInstruction"] = revisionInstruction }),
            ["preference_memory", "route_sort", "travel_web_research", "attraction_search"],
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
            "根据总预算、已选核心城市与路线区域推荐交通方便且不会挤压核心行程费用的住宿。"
            + "hotel_search 每轮最多调用一次；目的地包含多个核心城市时，必须把完整城市列表放在一次调用中，"
            + "不要逐城重复调用，也不要输出任何工具协议标记。",
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
