using System.Text.Json;
using DotNetAgentDev.Models;
using DotNetAgentDev.Tools;

namespace DotNetAgentDev.Tests;

public sealed class BudgetCalculatorToolTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsOverBudget_WhenTotalExceedsLimit()
    {
        var tool = new BudgetCalculatorTool();
        var arguments = JsonSerializer.Serialize(new
        {
            budgetLimit = 5000,
            transport = 1800,
            accommodation = 2200,
            food = 900,
            tickets = 500,
            other = 300
        });

        var result = await tool.ExecuteAsync(arguments, CancellationToken.None);
        var budget = JsonSerializer.Deserialize<BudgetBreakdown>(
            result.Content,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(result.Success);
        Assert.NotNull(budget);
        Assert.Equal(5700, budget.Total);
        Assert.Equal(-700, budget.Remaining);
        Assert.True(budget.IsOverBudget);
        Assert.NotEmpty(budget.OptimizationTips);
    }

    [Fact]
    public async Task ExecuteAsync_LeavesPositiveBuffer_WhenWithinLimit()
    {
        var tool = new BudgetCalculatorTool();
        var arguments = """
                        {
                          "budgetLimit": 10000,
                          "transport": 2000,
                          "accommodation": 3000,
                          "food": 1500,
                          "tickets": 500,
                          "other": 400
                        }
                        """;

        var result = await tool.ExecuteAsync(arguments, CancellationToken.None);
        using var document = JsonDocument.Parse(result.Content);

        Assert.False(document.RootElement.GetProperty("isOverBudget").GetBoolean());
        Assert.Equal(2600, document.RootElement.GetProperty("remaining").GetDecimal());
    }
}
