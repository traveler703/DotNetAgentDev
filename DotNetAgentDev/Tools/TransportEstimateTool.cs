using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class TransportEstimateTool(TourismCatalog catalog) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "transport_estimate",
        "估算出发地到目的地的大交通、跨城与市内交通时间和费用。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "departure": { "type": "string" },
                               "destination": { "type": "string" },
                               "days": { "type": "integer", "minimum": 1, "maximum": 30 },
                               "travelers": { "type": "integer", "minimum": 1, "maximum": 20 }
                             },
                             "required": ["departure", "destination", "days", "travelers"],
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

        var isDomestic = destinations.Count > 0
                         && destinations.All(destination => destination.Country == "中国")
                         && !ContainsForeignCountry(input.Destination);
        var sameCity = input.Departure.Contains(input.Destination, StringComparison.OrdinalIgnoreCase)
                       || input.Destination.Contains(input.Departure, StringComparison.OrdinalIgnoreCase);
        var outboundMode = sameCity ? "无需大交通" : isDomestic ? "高铁/国内航班" : "往返国际航班";
        var perPersonOutbound = sameCity ? 0m : isDomestic ? 900m : 2600m;
        var outboundCost = perPersonOutbound * input.Travelers;
        var localPerDay = destinations.Count > 0
            ? destinations.Average(destination => destination.LocalTransportPerDay)
            : 65m;
        var localCost = localPerDay * input.Days * input.Travelers;
        var intercityLegs = Math.Max(0, destinations.Count - 1);
        var intercityCost = intercityLegs * 360m * input.Travelers;
        var travelMinutes = sameCity ? 60 : isDomestic ? 300 : 480;
        travelMinutes += intercityLegs * 180;

        var summary = new TransportSummary(
            outboundMode,
            $"{input.Departure}至{input.Destination}建议比较提前购票的价格与时刻；估算按往返计。",
            outboundCost,
            localCost,
            intercityCost,
            travelMinutes,
            BuildNotes(destinations.Count, isDomestic));

        return ToolSupport.Success(new
        {
            disclaimer = "交通价格为课程演示估算，不代表实时票价。",
            summary
        });
    }

    private static IReadOnlyList<string> BuildNotes(int cityCount, bool isDomestic)
    {
        var notes = new List<string>
        {
            isDomestic ? "优先比较高铁与航班的总耗时。" : "国际行程建议至少提前 3 小时到达机场。",
            "每日路线优先使用公共交通，同一区域尽量步行衔接。"
        };
        if (cityCount > 1)
        {
            notes.Add("跨城移动安排在上午，并为酒店退房、行李寄存预留时间。");
        }

        return notes;
    }

    private static bool ContainsForeignCountry(string value) =>
        new[] { "日本", "新加坡", "泰国", "韩国", "美国", "法国", "英国" }
            .Any(value.Contains);

    private sealed record Input(string Departure, string Destination, int Days, int Travelers);
}
