using DotNetAgentDev.Agents;
using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;
using System.Text;

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
        var normalized = NormalizeRequest(request);
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

    public Task<TravelPlan> RevisePlanAsync(
        TravelPlan previousPlan,
        string instruction,
        CancellationToken cancellationToken,
        Action<PlanningStreamEvent>? onProgress = null)
    {
        var trimmedInstruction = instruction.Trim();
        var request = previousPlan.Request with
        {
            PreviousPlanId = previousPlan.Id,
            RevisionNumber = previousPlan.Request.RevisionNumber + 1,
            RevisionInstruction = trimmedInstruction,
            PreviousPlanSummary = BuildPreviousPlanSummary(previousPlan),
            Notes = AppendRevisionNote(previousPlan.Request.Notes, trimmedInstruction)
        };

        onProgress?.Invoke(new PlanningStreamEvent
        {
            Type = "progress",
            Agent = "主控 Agent",
            Phase = "Start",
            Title = "收到行程修改要求",
            Detail = $"将基于上一版方案重新运行 Agent 团队：{trimmedInstruction}",
            Percent = 2
        });

        return CreatePlanAsync(request, cancellationToken, onProgress);
    }

    private static TravelRequest NormalizeRequest(TravelRequest request) =>
        request with
        {
            UserId = string.IsNullOrWhiteSpace(request.UserId) ? "demo-user" : request.UserId.Trim(),
            Departure = request.Departure.Trim(),
            Destination = request.Destination.Trim(),
            Preferences = request.Preferences.Trim(),
            Notes = request.Notes.Trim(),
            RevisionInstruction = request.RevisionInstruction.Trim(),
            PreviousPlanSummary = request.PreviousPlanSummary.Trim()
        };

    private static string AppendRevisionNote(string notes, string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return notes.Trim();
        }

        var revisionNote = $"本轮修改要求：{instruction}";
        return string.IsNullOrWhiteSpace(notes)
            ? revisionNote
            : $"{notes.Trim()}；{revisionNote}";
    }

    private static string BuildPreviousPlanSummary(TravelPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"上一版计划：{plan.Title}");
        builder.AppendLine(
            $"预算：预计 {plan.Budget.Total:F0} 元 / 上限 {plan.Budget.BudgetLimit:F0} 元，"
            + (plan.Budget.IsOverBudget
                ? $"超支 {Math.Abs(plan.Budget.Remaining):F0} 元。"
                : $"剩余 {plan.Budget.Remaining:F0} 元。"));
        builder.AppendLine("每日安排：");

        foreach (var day in plan.Days)
        {
            var activities = string.Join(
                "；",
                day.Activities.Select(activity =>
                    $"{activity.Time} {activity.Name}"
                    + (string.IsNullOrWhiteSpace(activity.Area) ? "" : $"（{activity.Area}）")));
            builder.AppendLine(
                $"D{day.Day} {day.Date:yyyy-MM-dd} {day.City} {day.Theme}：{activities}");
        }

        if (plan.Hotels.Count > 0)
        {
            builder.AppendLine("住宿：");
            foreach (var hotel in plan.Hotels)
            {
                builder.AppendLine(
                    $"- {hotel.City} {hotel.Name}，{hotel.Area}，{hotel.PricePerNight:F0} 元/晚。");
            }
        }

        return builder.ToString().Trim();
    }
}
