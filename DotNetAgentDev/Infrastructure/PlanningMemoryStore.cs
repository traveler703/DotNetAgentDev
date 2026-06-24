using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotNetAgentDev.Models;
using DotNetAgentDev.Options;
using Microsoft.Extensions.Options;

namespace DotNetAgentDev.Infrastructure;

public sealed class PlanningMemoryStore
{
    private static readonly Regex UnicodeEscapePattern = new(
        @"\\u([0-9a-fA-F]{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnicodeSurrogatePairPattern = new(
        @"\\u(D[89ABab][0-9a-fA-F]{2})\\u(D[C-Fc-f][0-9a-fA-F]{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TruncatedUnicodeEscapePattern = new(
        @"\\u[0-9a-fA-F]{0,3}(?=\.{3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
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
            var json = DecodeSerializedSurrogatePairs(
                JsonSerializer.Serialize(plan, JsonOptions));
            await File.WriteAllTextAsync(path, json, cancellationToken);
            await UpdateProfileUnsafeAsync(plan, cancellationToken);
            _logger.LogInformation("Saved travel plan {PlanId} for {UserId}.", plan.Id, plan.Request.UserId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task NormalizeStoredJsonAsync(CancellationToken cancellationToken)
    {
        var files = new List<string>();
        if (File.Exists(_profilesPath))
        {
            files.Add(_profilesPath);
        }

        if (Directory.Exists(_plansDirectory))
        {
            files.AddRange(Directory.EnumerateFiles(_plansDirectory, "*.json"));
        }

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            if (!text.Contains(@"\u", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var root = JsonNode.Parse(text);
                if (root is null)
                {
                    continue;
                }

                var normalized = DecodeSerializedSurrogatePairs(JsonSerializer.Serialize(
                    NormalizeEmbeddedJsonStrings(root),
                    JsonOptions));
                var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(temporaryPath, normalized, cancellationToken);
                File.Move(temporaryPath, path, overwrite: true);
                _logger.LogInformation("Normalized escaped Unicode in {Path}.", path);
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Skipped invalid JSON while normalizing {Path}.", path);
            }
        }
    }

    private static JsonNode? NormalizeEmbeddedJsonStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var key in jsonObject.Select(pair => pair.Key).ToList())
                {
                    var current = jsonObject[key];
                    var normalized = NormalizeEmbeddedJsonStrings(current);
                    if (!ReferenceEquals(current, normalized))
                    {
                        jsonObject[key] = normalized;
                    }
                }

                return jsonObject;
            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    var current = jsonArray[index];
                    var normalized = NormalizeEmbeddedJsonStrings(current);
                    if (!ReferenceEquals(current, normalized))
                    {
                        jsonArray[index] = normalized;
                    }
                }

                return jsonArray;
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var value)
                                          && value.Contains(@"\u", StringComparison.Ordinal):
                try
                {
                    if (value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('['))
                    {
                        using var embedded = JsonDocument.Parse(value);
                        return JsonValue.Create(JsonSerializer.Serialize(embedded.RootElement, JsonOptions));
                    }
                }
                catch (JsonException)
                {
                    // Truncated trace summaries are not complete JSON; decode their Unicode escapes directly.
                }

                var decoded = UnicodeEscapePattern.Replace(
                    value,
                    match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
                return JsonValue.Create(TruncatedUnicodeEscapePattern.Replace(decoded, string.Empty));
            default:
                return node;
        }
    }

    private static string DecodeSerializedSurrogatePairs(string json) =>
        UnicodeSurrogatePairPattern.Replace(json, match =>
        {
            var high = (char)Convert.ToInt32(match.Groups[1].Value, 16);
            var low = (char)Convert.ToInt32(match.Groups[2].Value, 16);
            return char.ConvertFromUtf32(char.ConvertToUtf32(high, low));
        });

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
            : new UserMemoryProfile { UserId = userId };
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
        var travelPaces = (existing?.TravelPaces ?? [])
            .Append(PaceText(plan.Request.Pace))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        var notes = (existing?.Notes ?? [])
            .Concat(ParseNotes(plan.Request.Notes))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        var newCount = (existing?.PlanCount ?? 0) + 1;
        var currentDailyBudget = plan.Request.Budget / plan.Request.Days;
        var average = existing?.AverageBudgetPerDay is null
            ? currentDailyBudget
            : ((existing.AverageBudgetPerDay.Value * (newCount - 1)) + currentDailyBudget) / newCount;

        profiles[plan.Request.UserId] = new UserMemoryProfile
        {
            UserId = plan.Request.UserId,
            TravelPaces = travelPaces,
            Preferences = preferences,
            Notes = notes,
            AverageBudgetPerDay = decimal.Round(average, 2),
            PlanCount = newCount,
            UpdatedAt = DateTimeOffset.UtcNow
        };

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

    private static IEnumerable<string> ParseNotes(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? [] : [trimmed];
    }

    private static string PaceText(TravelPace pace) => pace switch
    {
        TravelPace.Relaxed => "轻松",
        TravelPace.Intensive => "充实",
        _ => "均衡"
    };
}
