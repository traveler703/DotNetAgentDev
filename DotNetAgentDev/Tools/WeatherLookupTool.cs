using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class WeatherLookupTool(TourismCatalog catalog) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "weather_lookup",
        "按目的地和日期查询本地知识库中的季节性天气参考。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "destination": { "type": "string" },
                               "startDate": { "type": "string", "description": "yyyy-MM-dd" },
                               "days": { "type": "integer", "minimum": 1, "maximum": 30 }
                             },
                             "required": ["destination", "startDate", "days"],
                             "additionalProperties": false
                           }
                           """));

    public async Task<ToolExecutionResult> ExecuteAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var input = ToolSupport.Parse<Input>(arguments);
        var date = DateOnly.TryParse(input.StartDate, out var parsed)
            ? parsed
            : DateOnly.FromDateTime(DateTime.Today.AddDays(30));
        var season = ToolSupport.GetSeason(date.Month);
        var destinations = await catalog.FindDestinationsAsync(input.Destination);
        cancellationToken.ThrowIfCancellationRequested();

        var forecasts = destinations.Select(destination => new
        {
            destination.City,
            Season = season,
            Weather = destination.SeasonalWeather.GetValueOrDefault(
                season,
                "季节天气信息暂缺，请出发前查询实时预报。")
        }).ToList();

        if (forecasts.Count == 0)
        {
            forecasts.Add(new
            {
                City = input.Destination,
                Season = season,
                Weather = "本地知识库暂无该城市天气数据，请在出发前 7 天查询权威实时天气。"
            });
        }

        return ToolSupport.Success(new
        {
            period = $"{date:yyyy-MM-dd} 起 {input.Days} 天",
            forecasts,
            disclaimer = "此处为季节性参考，不是实时天气预报。"
        });
    }

    private sealed record Input(string Destination, string StartDate, int Days);
}
