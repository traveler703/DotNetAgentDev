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
        CancellationToken cancellationToken,
        Action<PlanningStreamEvent>? onProgress = null)
    {
        var normalized = request with
        {
            UserId = string.IsNullOrWhiteSpace(request.UserId) ? "demo-user" : request.UserId.Trim(),
            Departure = request.Departure.Trim(),
            Destination = request.Destination.Trim(),
            Preferences = request.Preferences.Trim(),
            Notes = request.Notes.Trim()
        };
        var plan = await _coordinator.PlanAsync(normalized, cancellationToken, onProgress);
        await _memory.SavePlanAsync(plan, cancellationToken);
        onProgress?.Invoke(new PlanningStreamEvent
        {
            Type = "progress",
            Agent = "记忆模块",
            Phase = "Memory",
            Title = "方案已保存",
            Detail = "最终行程和用户偏好已写入长期记忆。",
            Percent = 99
        });
        return plan;
    }
}
