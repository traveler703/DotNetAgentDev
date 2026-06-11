using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class AttractionSearchTool(TourismCatalog catalog) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "attraction_search",
        "根据目的地与偏好查询景点、美食、商圈和休闲候选点。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "destination": { "type": "string", "description": "目的地国家或城市" },
                               "preferences": { "type": "string", "description": "用户偏好" },
                               "maxResults": { "type": "integer", "minimum": 1, "maximum": 20 }
                             },
                             "required": ["destination", "preferences", "maxResults"],
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

        var preferenceTerms = input.Preferences
            .Split(['、', ',', '，', '/', ' '], StringSplitOptions.RemoveEmptyEntries);
        var candidates = destinations
            .SelectMany(destination => destination.Attractions.Select(attraction =>
            {
                var preferenceScore = attraction.Tags.Count(tag =>
                    preferenceTerms.Any(term =>
                        tag.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || term.Contains(tag, StringComparison.OrdinalIgnoreCase)));
                var score = 7.5 + preferenceScore * 0.7 + (attraction.TicketPrice == 0 ? 0.2 : 0);
                return new AttractionCandidate(
                    destination.City,
                    attraction.Name,
                    attraction.Category,
                    attraction.Area,
                    attraction.Description,
                    attraction.TicketPrice,
                    attraction.DurationMinutes,
                    attraction.Tags,
                    Math.Min(10, score));
            }))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.City)
            .Take(Math.Clamp(input.MaxResults, 1, 20))
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = CreateFallback(input.Destination, input.MaxResults);
        }

        return ToolSupport.Success(new
        {
            source = destinations.Count > 0 ? "local-tourism-knowledge-base" : "generic-fallback",
            disclaimer = "门票为课程演示估算值，请在出行前复核官方信息。",
            candidates
        });
    }

    private static List<AttractionCandidate> CreateFallback(string destination, int maxResults)
    {
        var templates = new[]
        {
            ("城市历史博物馆", "博物馆", "市中心", "快速了解目的地历史与文化。", 60m, 150, new[] { "人文", "室内" }),
            ("老城步行街", "街区", "老城区", "体验本地建筑、生活与特色餐饮。", 0m, 150, new[] { "美食", "街区", "轻松" }),
            ("城市中央公园", "自然", "市中心", "安排轻松散步并为行程留出休息时间。", 0m, 120, new[] { "自然", "轻松" }),
            ("本地市场美食体验", "美食", "市场区", "品尝当地常见菜品并观察日常生活。", 0m, 120, new[] { "美食", "市场" }),
            ("城市观景台", "城市景观", "核心区", "俯瞰城市并适合傍晚观景。", 100m, 120, new[] { "夜景", "摄影" })
        };

        return templates.Take(Math.Clamp(maxResults, 1, templates.Length))
            .Select((item, index) => new AttractionCandidate(
                destination,
                $"{destination}{item.Item1}",
                item.Item2,
                item.Item3,
                item.Item4,
                item.Item5,
                item.Item6,
                item.Item7,
                8.2 - index * 0.1))
            .ToList();
    }

    private sealed record Input(string Destination, string Preferences, int MaxResults);
}
