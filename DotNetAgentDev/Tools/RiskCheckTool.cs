using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class RiskCheckTool(TourismCatalog catalog) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "risk_check",
        "检查签证、安全、季节、行程强度和备注中的旅行风险。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "destination": { "type": "string" },
                               "days": { "type": "integer", "minimum": 1, "maximum": 30 },
                               "pace": { "type": "string", "enum": ["Relaxed", "Balanced", "Intensive"] },
                               "notes": { "type": "string" },
                               "startDate": { "type": "string" }
                             },
                             "required": ["destination", "days", "pace", "notes", "startDate"],
                             "additionalProperties": false
                           }
                           """));

    public async Task<ToolExecutionResult> ExecuteAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var input = ToolSupport.Parse<Input>(arguments);
        var destinations = await catalog.FindDestinationsAsync(input.Destination);
        cancellationToken.ThrowIfCancellationRequested();
        var risks = new List<RiskNotice>();

        foreach (var destination in destinations.DefaultIfEmpty())
        {
            if (destination is null)
            {
                risks.Add(new RiskNotice(
                    "medium",
                    "information",
                    "目的地资料需复核",
                    "本地知识库未覆盖该目的地，政策和安全信息不能视为实时结论。",
                    "查询外交部领事服务、目的地官方旅游网站和实时天气。"));
                continue;
            }

            risks.Add(new RiskNotice(
                destination.Country == "中国" ? "low" : "high",
                "visa",
                $"{destination.Country}入境与证件",
                destination.VisaNote,
                "至少在出发前两周复核官方要求，并准备护照、签证和行程材料。"));
            risks.Add(new RiskNotice(
                "medium",
                "safety",
                $"{destination.City}安全提示",
                destination.SafetyNote,
                "购买合适保险，保存紧急联系方式和重要证件电子备份。"));
        }

        if (input.Pace == "Intensive" || (destinations.Count > 2 && input.Days <= 6))
        {
            risks.Add(new RiskNotice(
                "medium",
                "pace",
                "行程强度偏高",
                "景点或跨城密度较高，延误后容易产生连锁影响。",
                "每天至少保留一段机动时间，跨城当天减少一个景点。"));
        }

        if (!string.IsNullOrWhiteSpace(input.Notes))
        {
            risks.Add(new RiskNotice(
                "low",
                "personal",
                "已记录个性化备注",
                input.Notes,
                "预订交通、酒店和餐厅时再次核对该约束。"));
        }

        risks.Add(new RiskNotice(
            "medium",
            "data",
            "模拟数据声明",
            "本系统中的价格、天气和政策为课程演示参考，可能与实时情况不同。",
            "真实出行前必须通过官方或可信渠道二次确认。"));

        return ToolSupport.Success(risks);
    }

    private sealed record Input(
        string Destination,
        int Days,
        string Pace,
        string Notes,
        string StartDate);
}
