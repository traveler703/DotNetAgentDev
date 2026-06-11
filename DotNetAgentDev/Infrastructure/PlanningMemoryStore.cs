using System.Text.Json;
using DotNetAgentDev.Models;
using DotNetAgentDev.Options;
using Microsoft.Extensions.Options;

namespace DotNetAgentDev.Infrastructure;

public sealed class PlanningMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _plansDirectory;
    private readonly string _profilesPath;
    private readonly ILogger<PlanningMemoryStore> _logger;

    public PlanningMemoryStore(
        IWebHostEnvironment environment,
        IOptions<AgentOptions> options,
        ILogger<PlanningMemoryStore> logger)
    {
        var root = Path.Combine(environment.ContentRootPath, options.Value.DataDirectory);
        _plansDirectory = Path.Combine(root, "plans");
        _profilesPath = Path.Combine(root, "profiles.json");
        _logger = logger;
    }

    public async Task SavePlanAsync(TravelPlan plan, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_plansDirectory);
            var path = GetPlanPath(plan.Id);
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, plan, JsonOptions, cancellationToken);
            await UpdateProfileUnsafeAsync(plan, cancellationToken);
            _logger.LogInformation("Saved travel plan {PlanId} for {UserId}.", plan.Id, plan.Request.UserId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TravelPlan?> GetPlanAsync(Guid id, CancellationToken cancellationToken)
    {
        var path = GetPlanPath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TravelPlan>(stream, JsonOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<TravelPlan>> GetRecentPlansAsync(
        string userId,
        int count,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_plansDirectory))
        {
            return [];
        }

        var plans = new List<TravelPlan>();
        foreach (var path in Directory.EnumerateFiles(_plansDirectory, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(Math.Max(count * 3, count)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var plan = await JsonSerializer.DeserializeAsync<TravelPlan>(
                    stream,
                    JsonOptions,
                    cancellationToken);
                if (plan is not null
                    && string.Equals(plan.Request.UserId, userId, StringComparison.OrdinalIgnoreCase))
                {
                    plans.Add(plan);
                }
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Skipped invalid plan file {Path}.", path);
            }
        }

        return plans
            .OrderByDescending(plan => plan.CreatedAt)
            .Take(count)
            .ToList();
    }

    public async Task<UserMemoryProfile> GetProfileAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var profiles = await ReadProfilesAsync(cancellationToken);
        return profiles.TryGetValue(userId, out var profile)
            ? profile
            : new UserMemoryProfile(userId, [], [], null, 0, DateTimeOffset.UtcNow);
    }

    private async Task UpdateProfileUnsafeAsync(TravelPlan plan, CancellationToken cancellationToken)
    {
        var profiles = await ReadProfilesAsync(cancellationToken);
        profiles.TryGetValue(plan.Request.UserId, out var existing);

        var preferences = (existing?.Preferences ?? [])
            .Concat(ParsePreferences(plan.Request.Preferences))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        var destinations = (existing?.FrequentDestinations ?? [])
            .Append(plan.Request.Destination)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .Take(8)
            .ToList();
        var newCount = (existing?.PlanCount ?? 0) + 1;
        var currentDailyBudget = plan.Request.Budget / plan.Request.Days;
        var average = existing?.AverageBudgetPerDay is null
            ? currentDailyBudget
            : ((existing.AverageBudgetPerDay.Value * (newCount - 1)) + currentDailyBudget) / newCount;

        profiles[plan.Request.UserId] = new UserMemoryProfile(
            plan.Request.UserId,
            preferences,
            destinations,
            decimal.Round(average, 2),
            newCount,
            DateTimeOffset.UtcNow);

        Directory.CreateDirectory(Path.GetDirectoryName(_profilesPath)!);
        await using var stream = File.Create(_profilesPath);
        await JsonSerializer.SerializeAsync(stream, profiles, JsonOptions, cancellationToken);
    }

    private async Task<Dictionary<string, UserMemoryProfile>> ReadProfilesAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_profilesPath))
        {
            return new Dictionary<string, UserMemoryProfile>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = File.OpenRead(_profilesPath);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, UserMemoryProfile>>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   ?? new Dictionary<string, UserMemoryProfile>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Profile memory file is invalid; starting with empty memory.");
            return new Dictionary<string, UserMemoryProfile>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private string GetPlanPath(Guid id) => Path.Combine(_plansDirectory, $"{id:N}.json");

    private static IEnumerable<string> ParsePreferences(string value) =>
        value.Split(['、', ',', '，', '/', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0);
}
