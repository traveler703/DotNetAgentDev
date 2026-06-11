using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class RouteSortTool(TourismCatalog catalog) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "route_sort",
        "按城市、区域、偏好和行程节奏对候选点进行路线排序。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "destination": { "type": "string" },
                               "preferences": { "type": "string" },
                               "pace": { "type": "string", "enum": ["Relaxed", "Balanced", "Intensive"] },
                               "days": { "type": "integer", "minimum": 1, "maximum": 30 }
                             },
                             "required": ["destination", "preferences", "pace", "days"],
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

        var orderedCities = destinations
            .OrderBy(destination => destination.City.Contains("东京") ? 0 : 1)
            .ThenBy(destination => destination.City.Contains("京都") ? 0 : 1)
            .Select(destination => new
            {
                destination.City,
                RecommendedDays = Math.Max(
                    1,
                    (int)Math.Round((double)input.Days / Math.Max(1, destinations.Count))),
                Areas = destination.Attractions
                    .GroupBy(attraction => attraction.Area)
                    .OrderByDescending(group => group.Count())
                    .Select(group => group.Key)
                    .ToList()
            })
            .ToList();

        if (orderedCities.Count == 0)
        {
            orderedCities.Add(new
            {
                City = input.Destination,
                RecommendedDays = input.Days,
                Areas = new List<string> { "市中心", "老城区", "交通枢纽周边" }
            });
        }

        var activitiesPerDay = input.Pace switch
        {
            "Relaxed" => 2,
            "Intensive" => 4,
            _ => 3
        };

        return ToolSupport.Success(new
        {
            orderedCities,
            activitiesPerDay,
            strategy = "同一区域优先安排在同一天，跨城日减少景点数量并预留行李与交通时间。",
            preferenceHint = input.Preferences
        });
    }

    private sealed record Input(string Destination, string Preferences, string Pace, int Days);
}
