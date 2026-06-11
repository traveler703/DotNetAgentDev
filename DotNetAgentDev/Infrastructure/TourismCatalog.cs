using System.Text.Json;

namespace DotNetAgentDev.Infrastructure;

public sealed class TourismCatalog
{
    private readonly Lazy<Task<TourismCatalogData>> _data;
    private readonly ILogger<TourismCatalog> _logger;
    private readonly string _dataPath;

    public TourismCatalog(IWebHostEnvironment environment, ILogger<TourismCatalog> logger)
    {
        _logger = logger;
        _dataPath = Path.Combine(environment.ContentRootPath, "Data", "tourism-data.json");
        _data = new Lazy<Task<TourismCatalogData>>(LoadAsync);
    }

    public Task<TourismCatalogData> GetAsync() => _data.Value;

    public async Task<IReadOnlyList<DestinationData>> FindDestinationsAsync(string query)
    {
        var data = await GetAsync();
        var terms = query.Split(['、', ',', '，', '/', ' '], StringSplitOptions.RemoveEmptyEntries);
        var matches = data.Destinations
            .Where(destination => terms.Any(term => destination.Matches(term)))
            .ToList();

        if (matches.Count == 0)
        {
            matches = data.Destinations
                .Where(destination => destination.Matches(query))
                .ToList();
        }

        return matches;
    }

    private async Task<TourismCatalogData> LoadAsync()
    {
        await using var stream = File.OpenRead(_dataPath);
        var data = await JsonSerializer.DeserializeAsync<TourismCatalogData>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (data is null || data.Destinations.Count == 0)
        {
            throw new InvalidOperationException("本地旅游知识库为空或格式错误。");
        }

        _logger.LogInformation(
            "Loaded tourism catalog with {DestinationCount} destinations.",
            data.Destinations.Count);
        return data;
    }
}

public sealed record TourismCatalogData
{
    public IReadOnlyList<DestinationData> Destinations { get; init; } = [];
}

public sealed record DestinationData
{
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public decimal FoodPerDay { get; init; }
    public decimal LocalTransportPerDay { get; init; }
    public string VisaNote { get; init; } = string.Empty;
    public string SafetyNote { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> SeasonalWeather { get; init; }
        = new Dictionary<string, string>();
    public IReadOnlyList<CatalogAttraction> Attractions { get; init; } = [];
    public IReadOnlyList<CatalogHotel> Hotels { get; init; } = [];

    public bool Matches(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains(Country, StringComparison.OrdinalIgnoreCase)
               || value.Contains(City, StringComparison.OrdinalIgnoreCase)
               || Country.Contains(value, StringComparison.OrdinalIgnoreCase)
               || City.Contains(value, StringComparison.OrdinalIgnoreCase)
               || Aliases.Any(alias =>
                   value.Contains(alias, StringComparison.OrdinalIgnoreCase)
                   || alias.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record CatalogAttraction(
    string Name,
    string Category,
    string Area,
    string Description,
    decimal TicketPrice,
    int DurationMinutes,
    IReadOnlyList<string> Tags);

public sealed record CatalogHotel(
    string Name,
    string Area,
    decimal PricePerNight,
    string Level,
    string Reason,
    double Score);
