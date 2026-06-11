using DotNetAgentDev.Agents;
using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Services;

public sealed class TravelPlanningService
{
    private readonly TravelCoordinatorAgent _coordinator;
    private readonly PlanningMemoryStore _memory;

    public TravelPlanningService(
        TravelCoordinatorAgent coordinator,
        PlanningMemoryStore memory)
    {
        _coordinator = coordinator;
        _memory = memory;
    }

    public async Task<TravelPlan> CreatePlanAsync(
        TravelRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = request with
        {
            UserId = string.IsNullOrWhiteSpace(request.UserId) ? "demo-user" : request.UserId.Trim(),
            Departure = request.Departure.Trim(),
            Destination = request.Destination.Trim(),
            Preferences = request.Preferences.Trim(),
            Notes = request.Notes.Trim()
        };
        var plan = await _coordinator.PlanAsync(normalized, cancellationToken);
        await _memory.SavePlanAsync(plan, cancellationToken);
        return plan;
    }
}
