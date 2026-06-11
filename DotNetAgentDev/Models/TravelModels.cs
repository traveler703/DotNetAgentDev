using System.ComponentModel.DataAnnotations;

namespace DotNetAgentDev.Models;

public enum TravelPace
{
    Relaxed,
    Balanced,
    Intensive
}

public sealed record TravelRequest
{
    public string UserId { get; init; } = "demo-user";

    [Required]
    public string Departure { get; init; } = string.Empty;

    [Required]
    public string Destination { get; init; } = string.Empty;

    public DateOnly? StartDate { get; init; }
    public int Days { get; init; } = 5;
    public int Travelers { get; init; } = 1;
    public decimal Budget { get; init; }
    public string Preferences { get; init; } = string.Empty;
    public TravelPace Pace { get; init; } = TravelPace.Balanced;
    public string Notes { get; init; } = string.Empty;
}

public sealed record TravelPlan
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required TravelRequest Request { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<DayPlan> Days { get; init; }
    public required IReadOnlyList<HotelRecommendation> Hotels { get; init; }
    public required TransportSummary Transport { get; init; }
    public required BudgetBreakdown Budget { get; init; }
    public required IReadOnlyList<RiskNotice> Risks { get; init; }
    public required IReadOnlyList<string> AdjustmentSuggestions { get; init; }
    public required IReadOnlyList<AgentTraceStep> Trace { get; init; }
    public required IReadOnlyList<AgentContribution> AgentContributions { get; init; }
    public string ModelMode { get; init; } = "offline";
}

public sealed record DayPlan(
    int Day,
    DateOnly Date,
    string City,
    string Theme,
    IReadOnlyList<TravelActivity> Activities,
    decimal EstimatedCost,
    string PaceNote);

public sealed record TravelActivity(
    string Time,
    string Name,
    string Category,
    string Description,
    decimal Cost,
    int DurationMinutes,
    string Area);

public sealed record AttractionCandidate(
    string City,
    string Name,
    string Category,
    string Area,
    string Description,
    decimal TicketPrice,
    int DurationMinutes,
    IReadOnlyList<string> Tags,
    double Score);

public sealed record HotelRecommendation(
    string City,
    string Name,
    string Area,
    decimal PricePerNight,
    string Level,
    string Reason,
    double Score);

public sealed record TransportSummary(
    string OutboundMode,
    string OutboundDescription,
    decimal OutboundCost,
    decimal LocalCost,
    decimal IntercityCost,
    int EstimatedTravelMinutes,
    IReadOnlyList<string> RouteNotes);

public sealed record BudgetBreakdown(
    decimal Total,
    decimal BudgetLimit,
    decimal Transport,
    decimal Accommodation,
    decimal Food,
    decimal Tickets,
    decimal Other,
    decimal Remaining,
    bool IsOverBudget,
    IReadOnlyList<string> OptimizationTips);

public sealed record RiskNotice(
    string Level,
    string Category,
    string Title,
    string Detail,
    string Recommendation);

public sealed record AgentTraceStep(
    int Sequence,
    string Agent,
    string Phase,
    string Title,
    string Detail,
    DateTimeOffset Timestamp);

public sealed record AgentContribution(
    string Agent,
    string Responsibility,
    string Summary,
    int ToolCallCount);

public sealed record UserMemoryProfile(
    string UserId,
    IReadOnlyList<string> Preferences,
    IReadOnlyList<string> FrequentDestinations,
    decimal? AverageBudgetPerDay,
    int PlanCount,
    DateTimeOffset UpdatedAt);

public static class TravelRequestValidator
{
    public static Dictionary<string, string[]> Validate(TravelRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Departure))
        {
            errors[nameof(request.Departure)] = ["请填写出发地。"];
        }

        if (string.IsNullOrWhiteSpace(request.Destination))
        {
            errors[nameof(request.Destination)] = ["请填写目的地。"];
        }

        if (request.Days is < 1 or > 30)
        {
            errors[nameof(request.Days)] = ["旅行天数必须在 1 到 30 天之间。"];
        }

        if (request.Travelers is < 1 or > 20)
        {
            errors[nameof(request.Travelers)] = ["出行人数必须在 1 到 20 人之间。"];
        }

        if (request.Budget <= 0)
        {
            errors[nameof(request.Budget)] = ["预算必须大于 0。"];
        }

        return errors;
    }
}
