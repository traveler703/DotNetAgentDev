using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class HotelSearchTool(TourismCatalog catalog) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "hotel_search",
        "按城市、每晚预算和偏好查询住宿建议。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "destination": { "type": "string" },
                               "nightlyBudget": { "type": "number", "minimum": 1 },
                               "preferences": { "type": "string" }
                             },
                             "required": ["destination", "nightlyBudget", "preferences"],
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

        var hotels = destinations
            .SelectMany(destination => destination.Hotels.Select(hotel => new HotelRecommendation(
                destination.City,
                hotel.Name,
                hotel.Area,
                hotel.PricePerNight,
                hotel.Level,
                hotel.Reason,
                hotel.Score - (hotel.PricePerNight > input.NightlyBudget ? 0.8 : 0))))
            .OrderBy(hotel => hotel.PricePerNight > input.NightlyBudget)
            .ThenByDescending(hotel => hotel.Score)
            .Take(Math.Max(3, destinations.Count * 2))
            .ToList();

        if (hotels.Count == 0)
        {
            var baseline = Math.Max(260, input.NightlyBudget);
            hotels =
            [
                new(input.Destination, $"{input.Destination}交通枢纽酒店", "交通枢纽",
                    decimal.Round(baseline * 0.85m, 0), "经济型", "方便抵离并减少拖运行李时间。", 8.5),
                new(input.Destination, $"{input.Destination}市中心酒店", "市中心",
                    decimal.Round(baseline, 0), "舒适型", "餐饮和公共交通选择较多。", 8.8),
                new(input.Destination, $"{input.Destination}景观酒店", "核心景区",
                    decimal.Round(baseline * 1.35m, 0), "高档型", "更重视环境和住宿体验。", 8.7)
            ];
        }

        return ToolSupport.Success(new
        {
            nightlyBudget = input.NightlyBudget,
            disclaimer = "酒店名称与价格为本地模拟数据，真实预订前需再次查询。",
            hotels
        });
    }

    private sealed record Input(string Destination, decimal NightlyBudget, string Preferences);
}
