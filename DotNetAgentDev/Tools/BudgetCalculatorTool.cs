using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class BudgetCalculatorTool : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "budget_calculator",
        "汇总交通、住宿、餐饮、门票和其他费用，检查是否超预算并生成优化建议。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "budgetLimit": { "type": "number", "minimum": 1 },
                               "transport": { "type": "number", "minimum": 0 },
                               "accommodation": { "type": "number", "minimum": 0 },
                               "food": { "type": "number", "minimum": 0 },
                               "tickets": { "type": "number", "minimum": 0 },
                               "other": { "type": "number", "minimum": 0 }
                             },
                             "required": ["budgetLimit", "transport", "accommodation", "food", "tickets", "other"],
                             "additionalProperties": false
                           }
                           """));

    public Task<ToolExecutionResult> ExecuteAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = ToolSupport.Parse<Input>(arguments);
        var total = input.Transport + input.Accommodation + input.Food + input.Tickets + input.Other;
        var remaining = input.BudgetLimit - total;
        var tips = BuildTips(input, remaining);
        var result = new BudgetBreakdown(
            decimal.Round(total, 2),
            input.BudgetLimit,
            decimal.Round(input.Transport, 2),
            decimal.Round(input.Accommodation, 2),
            decimal.Round(input.Food, 2),
            decimal.Round(input.Tickets, 2),
            decimal.Round(input.Other, 2),
            decimal.Round(remaining, 2),
            remaining < 0,
            tips);

        return Task.FromResult(ToolSupport.Success(result));
    }

    private static IReadOnlyList<string> BuildTips(Input input, decimal remaining)
    {
        var tips = new List<string>();
        if (remaining >= 0)
        {
            tips.Add($"当前预留约 {remaining:F0} 元机动资金，可用于价格波动或临时体验。");
        }
        else
        {
            tips.Add($"方案预计超出预算 {Math.Abs(remaining):F0} 元，建议优先调整最大费用项。");
        }

        var items = new Dictionary<string, decimal>
        {
            ["交通"] = input.Transport,
            ["住宿"] = input.Accommodation,
            ["餐饮"] = input.Food,
            ["门票"] = input.Tickets
        };
        var largest = items.MaxBy(item => item.Value);
        tips.Add(largest.Key switch
        {
            "住宿" => "可改住交通便利的非核心景区酒店，通常比直接减少游玩天数更平衡。",
            "交通" => "可比较不同出发时段，或减少一次跨城移动。",
            "餐饮" => "保留一顿特色餐，其余安排市场、食堂或套餐。",
            _ => "可减少高价门票景点，替换为街区、公园或博物馆免费时段。"
        });
        return tips;
    }

    private sealed record Input(
        decimal BudgetLimit,
        decimal Transport,
        decimal Accommodation,
        decimal Food,
        decimal Tickets,
        decimal Other);
}
