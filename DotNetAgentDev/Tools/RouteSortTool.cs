using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class RouteSortTool(TourismCatalog catalog) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "route_sort",
        "先判断目的地范围并选择少量核心城市，再按区域、偏好和节奏排序；普通行程最多选择3个城市。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "destination": { "type": "string" },
                               "preferences": { "type": "string" },
                               "pace": { "type": "string", "enum": ["Relaxed", "Balanced", "Intensive"] },
                               "days": { "type": "integer", "minimum": 1, "maximum": 30 },
                               "budget": { "type": "number", "minimum": 0 },
                               "travelers": { "type": "integer", "minimum": 1, "maximum": 20 }
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

        var profiles = destinations.Count > 0
            ? destinations
                .OrderBy(destination => CityPriority(input.Destination, destination.City))
                .Select(destination => new CityProfile(
                    destination.City,
                    destination.Attractions
                        .GroupBy(attraction => attraction.Area)
                        .OrderByDescending(group => group.Count())
                        .Select(group => group.Key)
                        .Take(4)
                        .ToList()))
                .ToList()
            : FindBroadDestinationCities(input.Destination);
        if (profiles.Count == 0)
        {
            profiles.Add(new CityProfile(
                input.Destination,
                ["市中心", "代表性历史街区", "公共交通便利区域"]));
        }

        var cityCount = Math.Min(profiles.Count, SelectCityCount(input));
        var selected = profiles.Take(cityCount).ToList();
        var baseDays = input.Days / cityCount;
        var extraDays = input.Days % cityCount;
        var orderedCities = selected
            .Select((profile, index) => new RouteCityPlan(
                profile.City,
                Math.Max(1, baseDays + (index < extraDays ? 1 : 0)),
                profile.Areas))
            .ToList();

        var activitiesPerDay = input.Pace switch
        {
            "Relaxed" => 2,
            "Intensive" => 4,
            _ => 3
        };

        return ToolSupport.Success(new RoutePlan(
            orderedCities,
            activitiesPerDay,
            $"目的地范围分析后选择 {orderedCities.Count} 个核心城市；"
            + "同一区域优先安排在同一天，跨城日减少景点数量并预留行李与交通时间。"));
    }

    private static int SelectCityCount(Input input)
    {
        var normalLimit = input.Days switch
        {
            <= 4 => 1,
            <= 7 => 2,
            _ => 3
        };
        var perPersonPerDay = input.Budget <= 0
            ? 0
            : input.Budget / Math.Max(1, input.Travelers) / input.Days;
        return input.Days >= 15 && perPersonPerDay >= 900
            ? 4
            : normalLimit;
    }

    private static int CityPriority(string destination, string city)
    {
        var key = destination.ToLowerInvariant();
        if (key.Contains("日本") || key.Contains("japan"))
        {
            return city switch
            {
                "东京" => 0,
                "京都" => 1,
                "大阪" => 2,
                _ => 10
            };
        }

        return 0;
    }

    private static List<CityProfile> FindBroadDestinationCities(string destination)
    {
        var key = destination.Trim().ToLowerInvariant();
        if (key.Contains("越南") || key.Contains("vietnam"))
        {
            return
            [
                new CityProfile("河内", ["还剑湖与老城区", "巴亭广场周边", "西湖"]),
                new CityProfile("岘港", ["美溪海滩", "山茶半岛", "会安古城"]),
                new CityProfile("胡志明市", ["第一郡", "堤岸", "西贡河畔"])
            ];
        }

        if (key.Contains("台湾") || key.Contains("taiwan"))
        {
            return
            [
                new CityProfile("台北", ["信义区", "中正区", "士林区", "大同区"]),
                new CityProfile("台中", ["西区", "中区", "后里"]),
                new CityProfile("台南", ["中西区", "安平区"]),
                new CityProfile("高雄", ["盐埕区", "鼓山区", "左营区"])
            ];
        }

        if (key.Contains("西欧") || key.Contains("western europe"))
        {
            return
            [
                new CityProfile("巴黎", ["塞纳河沿岸", "卢浮宫周边", "蒙马特"]),
                new CityProfile("阿姆斯特丹", ["运河带", "博物馆广场", "约旦区"]),
                new CityProfile("布鲁塞尔", ["大广场", "欧洲区"])
            ];
        }

        return [];
    }

    private sealed record CityProfile(string City, IReadOnlyList<string> Areas);

    private sealed record Input(
        string Destination,
        string Preferences,
        string Pace,
        int Days,
        decimal Budget = 0,
        int Travelers = 1);
}
