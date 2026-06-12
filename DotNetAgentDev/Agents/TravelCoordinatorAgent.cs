using System.Globalization;
using System.Text.Json;
using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Agents;

public sealed class TravelCoordinatorAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ItineraryAgent _itineraryAgent;
    private readonly HotelAgent _hotelAgent;
    private readonly TransportAgent _transportAgent;
    private readonly BudgetAgent _budgetAgent;
    private readonly RiskAgent _riskAgent;
    private readonly TourismCatalog _catalog;
    private readonly ILogger<TravelCoordinatorAgent> _logger;

    public TravelCoordinatorAgent(
        ItineraryAgent itineraryAgent,
        HotelAgent hotelAgent,
        TransportAgent transportAgent,
        BudgetAgent budgetAgent,
        RiskAgent riskAgent,
        TourismCatalog catalog,
        ILogger<TravelCoordinatorAgent> logger)
    {
        _itineraryAgent = itineraryAgent;
        _hotelAgent = hotelAgent;
        _transportAgent = transportAgent;
        _budgetAgent = budgetAgent;
        _riskAgent = riskAgent;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<TravelPlan> PlanAsync(
        TravelRequest request,
        CancellationToken cancellationToken,
        Action<PlanningStreamEvent>? onProgress = null)
    {
        var startDate = request.StartDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(30));
        request = request with { StartDate = startDate };
        var coordinatorTrace = new List<AgentTraceStep>
        {
            Trace(1, "主控 Agent", "Thought", "理解旅行目标",
                $"从{request.Departure}前往{request.Destination}，{request.Days}天，"
                + $"{request.Travelers}人，预算{request.Budget:F0}元，节奏为{PaceText(request.Pace)}。"),
            Trace(2, "主控 Agent", "Action", "拆分专业子任务",
                "并行委派行程、酒店、交通与风险 Agent，待事实数据返回后再执行预算 Agent。")
        };
        EmitProgress(onProgress, "trace", coordinatorTrace[0], 5);
        EmitProgress(onProgress, "trace", coordinatorTrace[1], 10);

        var itineraryTask = _itineraryAgent.RunAsync(request, 10, cancellationToken, onProgress);
        var hotelTask = _hotelAgent.RunAsync(request, 100, cancellationToken, onProgress);
        var transportTask = _transportAgent.RunAsync(request, 200, cancellationToken, onProgress);
        var riskTask = _riskAgent.RunAsync(request, 300, cancellationToken, onProgress);
        await Task.WhenAll(itineraryTask, hotelTask, transportTask, riskTask);

        var itineraryRun = await itineraryTask;
        var hotelRun = await hotelTask;
        var transportRun = await transportTask;
        var riskRun = await riskTask;

        var destinations = await _catalog.FindDestinationsAsync(request.Destination);
        var attractions = ParseAttractions(itineraryRun)
                          ?? BuildFallbackAttractions(request, destinations);
        var hotels = ParseHotels(hotelRun)
                     ?? BuildFallbackHotels(request, destinations);
        var transport = ParseTransport(transportRun)
                        ?? BuildFallbackTransport(request, destinations);
        var days = BuildDailyPlan(request, attractions, destinations, startDate);
        var selectedHotels = SelectHotels(hotels, days, request);
        var costs = CalculateCostInputs(request, days, selectedHotels, transport, destinations);

        var summaryTrace = Trace(
            350,
            "主控 Agent",
            "Observation",
            "专业结果汇总",
            $"获得 {attractions.Count} 个候选体验、{hotels.Count} 个住宿方案，"
            + $"交通预估 {transport.OutboundCost + transport.LocalCost + transport.IntercityCost:F0} 元。");
        coordinatorTrace.Add(summaryTrace);
        EmitProgress(onProgress, "trace", summaryTrace, 72);

        var budgetContext = costs.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(CultureInfo.InvariantCulture));
        var budgetRun = await _budgetAgent.RunAsync(
            request,
            budgetContext,
            400,
            cancellationToken,
            onProgress);
        var budget = ParseBudget(budgetRun)
                     ?? BuildFallbackBudget(request, costs);
        var risks = ParseRisks(riskRun);
        risks.AddRange(ParseWeatherRisks(riskRun));
        var suggestions = BuildSuggestions(request, budget, transport, days);

        var conflictTrace = Trace(
            500,
            "主控 Agent",
            "Thought",
            "执行冲突检查",
            budget.IsOverBudget
                ? $"预算超出 {Math.Abs(budget.Remaining):F0} 元，将优先给出住宿、交通和门票调整建议。"
                : $"预算内仍有 {budget.Remaining:F0} 元机动空间，保留用于价格波动。");
        coordinatorTrace.Add(conflictTrace);
        EmitProgress(onProgress, "trace", conflictTrace, 90);
        var finalTrace = Trace(
            501,
            "主控 Agent",
            "FinalAnswer",
            "生成统一旅行方案",
            $"已整合 {request.Days} 天日程、住宿、交通、预算与 {risks.Count} 条风险提醒。");
        coordinatorTrace.Add(finalTrace);
        EmitProgress(onProgress, "trace", finalTrace, 96);

        var runs = new[] { itineraryRun, hotelRun, transportRun, riskRun, budgetRun };
        var allTrace = coordinatorTrace
            .Concat(runs.SelectMany(run => run.Trace))
            .OrderBy(step => step.Sequence)
            .Select((step, index) => step with { Sequence = index + 1 })
            .ToList();
        var contributions = runs.Select(ToContribution).ToList();
        contributions.Insert(0, new AgentContribution(
            "主控 Agent",
            "任务分解、冲突处理与结果整合",
            budget.IsOverBudget ? "完成整合并标记预算超支。" : "完成整合并通过预算约束。",
            0));

        var plan = new TravelPlan
        {
            Request = request,
            Title = $"{request.Destination} {request.Days} 天个性化旅行计划",
            Summary = BuildSummary(request, days, selectedHotels, budget),
            Days = days,
            Hotels = selectedHotels,
            Transport = transport,
            Budget = budget,
            Risks = risks
                .DistinctBy(risk => $"{risk.Category}:{risk.Title}")
                .ToList(),
            AdjustmentSuggestions = suggestions,
            Trace = allTrace,
            AgentContributions = contributions,
            ModelMode = runs.All(run => run.ModelMode == "deepseek") ? "deepseek" : "offline"
        };

        _logger.LogInformation(
            "Coordinator created plan {PlanId}: total {Total}, budget {Budget}.",
            plan.Id,
            plan.Budget.Total,
            plan.Budget.BudgetLimit);
        return plan;
    }

    private static void EmitProgress(
        Action<PlanningStreamEvent>? onProgress,
        string type,
        AgentTraceStep trace,
        int? percent = null) =>
        onProgress?.Invoke(new PlanningStreamEvent
        {
            Type = type,
            Agent = trace.Agent,
            Phase = trace.Phase,
            Title = trace.Title,
            Detail = trace.Detail,
            Percent = percent,
            Timestamp = trace.Timestamp
        });

    private static IReadOnlyList<AttractionCandidate>? ParseAttractions(AgentRunResult run)
    {
        if (!run.Observations.TryGetValue("attraction_search", out var json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("candidates")
            .Deserialize<List<AttractionCandidate>>(JsonOptions);
    }

    private static IReadOnlyList<HotelRecommendation>? ParseHotels(AgentRunResult run)
    {
        if (!run.Observations.TryGetValue("hotel_search", out var json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("hotels")
            .Deserialize<List<HotelRecommendation>>(JsonOptions);
    }

    private static TransportSummary? ParseTransport(AgentRunResult run)
    {
        if (!run.Observations.TryGetValue("transport_estimate", out var json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("summary")
            .Deserialize<TransportSummary>(JsonOptions);
    }

    private static BudgetBreakdown? ParseBudget(AgentRunResult run)
    {
        if (!run.Observations.TryGetValue("budget_calculator", out var json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<BudgetBreakdown>(json, JsonOptions);
    }

    private static List<RiskNotice> ParseRisks(AgentRunResult run)
    {
        if (!run.Observations.TryGetValue("risk_check", out var json))
        {
            return
            [
                new RiskNotice(
                    "medium",
                    "data",
                    "信息需要复核",
                    "风险工具未返回结构化结果。",
                    "出发前核验签证、天气、安全和保险信息。")
            ];
        }

        return JsonSerializer.Deserialize<List<RiskNotice>>(json, JsonOptions) ?? [];
    }

    private static List<RiskNotice> ParseWeatherRisks(AgentRunResult run)
    {
        if (!run.Observations.TryGetValue("weather_lookup", out var json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("forecasts", out var forecasts))
        {
            return [];
        }

        return forecasts.EnumerateArray().Select(item => new RiskNotice(
            "low",
            "weather",
            $"{item.GetProperty("city").GetString()}季节天气",
            item.GetProperty("weather").GetString() ?? "暂无天气信息。",
            "出发前 7 天再次查询实时天气，并按日调整穿着与室内备选。")).ToList();
    }

    private static IReadOnlyList<AttractionCandidate> BuildFallbackAttractions(
        TravelRequest request,
        IReadOnlyList<DestinationData> destinations) =>
        destinations.SelectMany(destination => destination.Attractions.Select(attraction =>
                new AttractionCandidate(
                    destination.City,
                    attraction.Name,
                    attraction.Category,
                    attraction.Area,
                    attraction.Description,
                    attraction.TicketPrice,
                    attraction.DurationMinutes,
                    attraction.Tags,
                    8.0)))
            .Take(Math.Max(3, request.Days * 3))
            .ToList();

    private static IReadOnlyList<HotelRecommendation> BuildFallbackHotels(
        TravelRequest request,
        IReadOnlyList<DestinationData> destinations) =>
        destinations.SelectMany(destination => destination.Hotels.Select(hotel =>
                new HotelRecommendation(
                    destination.City,
                    hotel.Name,
                    hotel.Area,
                    hotel.PricePerNight,
                    hotel.Level,
                    hotel.Reason,
                    hotel.Score)))
            .DefaultIfEmpty(new HotelRecommendation(
                request.Destination,
                $"{request.Destination}市中心酒店",
                "市中心",
                500,
                "舒适型",
                "本地知识库暂无详细住宿数据，建议选择公共交通附近。",
                8.0))
            .ToList();

    private static TransportSummary BuildFallbackTransport(
        TravelRequest request,
        IReadOnlyList<DestinationData> destinations)
    {
        var international = destinations.Any(destination => destination.Country != "中国");
        var outbound = (international ? 2600m : 900m) * request.Travelers;
        var local = (destinations.Count == 0
            ? 60m
            : destinations.Average(destination => destination.LocalTransportPerDay))
                    * request.Days
                    * request.Travelers;
        return new TransportSummary(
            international ? "往返国际航班" : "高铁/国内航班",
            "按常见交通方式生成的保守估算。",
            outbound,
            local,
            Math.Max(0, destinations.Count - 1) * 360m * request.Travelers,
            international ? 480 : 300,
            ["尽早比较票价与时刻。", "同一区域尽量步行衔接。"]);
    }

    private static IReadOnlyList<DayPlan> BuildDailyPlan(
        TravelRequest request,
        IReadOnlyList<AttractionCandidate> attractions,
        IReadOnlyList<DestinationData> destinations,
        DateOnly startDate)
    {
        var activitiesPerDay = request.Pace switch
        {
            TravelPace.Relaxed => 2,
            TravelPace.Intensive => 4,
            _ => 3
        };
        var cities = destinations.Select(destination => destination.City).Distinct().ToList();
        if (cities.Count == 0)
        {
            cities.Add(request.Destination);
        }

        var cityQueues = cities.ToDictionary(
            city => city,
            city => new Queue<AttractionCandidate>(
                attractions.Where(candidate => candidate.City == city)
                    .OrderByDescending(candidate => candidate.Score)));
        var unused = new Queue<AttractionCandidate>(attractions.OrderByDescending(item => item.Score));
        var days = new List<DayPlan>();

        for (var index = 0; index < request.Days; index++)
        {
            var cityIndex = Math.Min(cities.Count - 1, index * cities.Count / request.Days);
            var city = cities[cityIndex];
            var count = activitiesPerDay;
            if (index == 0 || index == request.Days - 1)
            {
                count = Math.Max(1, count - 1);
            }

            var selected = new List<AttractionCandidate>();
            while (selected.Count < count && cityQueues.TryGetValue(city, out var queue) && queue.Count > 0)
            {
                selected.Add(queue.Dequeue());
            }

            while (selected.Count < count && unused.Count > 0)
            {
                var candidate = unused.Dequeue();
                if (selected.All(item => item.Name != candidate.Name))
                {
                    selected.Add(candidate);
                }
            }

            if (selected.Count == 0)
            {
                selected.Add(new AttractionCandidate(
                    city,
                    $"{city}自由探索",
                    "休闲",
                    "市中心",
                    "根据当天体力在酒店周边散步、用餐并保留机动时间。",
                    0,
                    120,
                    ["轻松"],
                    7.5));
            }

            var times = count switch
            {
                1 => new[] { "10:00" },
                2 => new[] { "10:00", "15:00" },
                4 => new[] { "08:30", "11:30", "14:30", "18:30" },
                _ => new[] { "09:00", "13:30", "17:30" }
            };
            var activities = selected.Select((candidate, activityIndex) =>
                new TravelActivity(
                    times[Math.Min(activityIndex, times.Length - 1)],
                    candidate.Name,
                    candidate.Category,
                    candidate.Description,
                    candidate.TicketPrice * request.Travelers,
                    candidate.DurationMinutes,
                    candidate.Area)).ToList();
            var dailyBase = destinations.FirstOrDefault(destination => destination.City == city);
            var dailyCost = activities.Sum(activity => activity.Cost)
                            + (dailyBase?.FoodPerDay ?? 220) * request.Travelers
                            + (dailyBase?.LocalTransportPerDay ?? 60) * request.Travelers;

            days.Add(new DayPlan(
                index + 1,
                startDate.AddDays(index),
                city,
                BuildTheme(activities),
                activities,
                decimal.Round(dailyCost, 2),
                index == 0
                    ? "抵达日降低强度，优先适应环境。"
                    : index == request.Days - 1
                        ? "返程日预留整理行李与交通时间。"
                        : $"按{PaceText(request.Pace)}节奏安排，并保留临时调整空间。"));
        }

        return days;
    }

    private static IReadOnlyList<HotelRecommendation> SelectHotels(
        IReadOnlyList<HotelRecommendation> hotels,
        IReadOnlyList<DayPlan> days,
        TravelRequest request)
    {
        var nightlyBudget = request.Budget * 0.3m / Math.Max(1, request.Days - 1);
        var cities = days.Select(day => day.City).Distinct();
        return cities.Select(city =>
                hotels.Where(hotel => hotel.City == city)
                    .OrderBy(hotel => hotel.PricePerNight > nightlyBudget)
                    .ThenByDescending(hotel => hotel.Score)
                    .FirstOrDefault()
                ?? hotels.OrderBy(hotel => hotel.PricePerNight > nightlyBudget)
                    .ThenByDescending(hotel => hotel.Score)
                    .First())
            .DistinctBy(hotel => hotel.Name)
            .ToList();
    }

    private static Dictionary<string, decimal> CalculateCostInputs(
        TravelRequest request,
        IReadOnlyList<DayPlan> days,
        IReadOnlyList<HotelRecommendation> hotels,
        TransportSummary transport,
        IReadOnlyList<DestinationData> destinations)
    {
        var nights = Math.Max(0, request.Days - 1);
        var rooms = (int)Math.Ceiling(request.Travelers / 2m);
        var accommodation = hotels.Count == 0
            ? 0
            : hotels.Average(hotel => hotel.PricePerNight) * nights * rooms;
        var foodPerDay = destinations.Count == 0
            ? 220m
            : destinations.Average(destination => destination.FoodPerDay);
        var food = foodPerDay * request.Days * request.Travelers;
        var tickets = days.SelectMany(day => day.Activities).Sum(activity => activity.Cost);
        var transportCost = transport.OutboundCost + transport.LocalCost + transport.IntercityCost;
        var other = decimal.Round((accommodation + food + tickets + transportCost) * 0.06m, 2);

        return new Dictionary<string, decimal>
        {
            ["budgetLimit"] = request.Budget,
            ["transport"] = decimal.Round(transportCost, 2),
            ["accommodation"] = decimal.Round(accommodation, 2),
            ["food"] = decimal.Round(food, 2),
            ["tickets"] = decimal.Round(tickets, 2),
            ["other"] = other
        };
    }

    private static BudgetBreakdown BuildFallbackBudget(
        TravelRequest request,
        IReadOnlyDictionary<string, decimal> costs)
    {
        var total = costs.Where(pair => pair.Key != "budgetLimit").Sum(pair => pair.Value);
        var remaining = request.Budget - total;
        return new BudgetBreakdown(
            total,
            request.Budget,
            costs["transport"],
            costs["accommodation"],
            costs["food"],
            costs["tickets"],
            costs["other"],
            remaining,
            remaining < 0,
            remaining < 0
                ? [$"预计超支 {Math.Abs(remaining):F0} 元，优先降低住宿或大交通成本。"]
                : [$"预留 {remaining:F0} 元作为机动资金。"]);
    }

    private static IReadOnlyList<string> BuildSuggestions(
        TravelRequest request,
        BudgetBreakdown budget,
        TransportSummary transport,
        IReadOnlyList<DayPlan> days)
    {
        var suggestions = budget.OptimizationTips.ToList();
        if (request.Pace == TravelPace.Relaxed)
        {
            suggestions.Add("保持每天 2 个核心活动，临时增加项目时不要占用跨城和返程缓冲。");
        }

        if (transport.IntercityCost > 0)
        {
            suggestions.Add("若希望降低成本或减轻疲劳，可删减一个停留城市并增加连续住宿。");
        }

        if (days.Any(day => day.Activities.Sum(activity => activity.DurationMinutes) > 540))
        {
            suggestions.Add("个别日期活动时长偏高，可删除评分最低的一个点作为雨天备选。");
        }

        return suggestions.Distinct().ToList();
    }

    private static AgentContribution ToContribution(AgentRunResult run) => new(
        run.AgentName,
        run.AgentName switch
        {
            "行程规划 Agent" => "候选体验、路线区域与每日节奏",
            "酒店 Agent" => "住宿位置与价位筛选",
            "交通 Agent" => "大交通、市内交通与跨城估算",
            "预算 Agent" => "费用汇总、预算检查与优化",
            "风险 Agent" => "天气、签证、安全与强度检查",
            _ => "专业子任务"
        },
        run.FinalAnswer,
        run.ToolCallCount);

    private static string BuildSummary(
        TravelRequest request,
        IReadOnlyList<DayPlan> days,
        IReadOnlyList<HotelRecommendation> hotels,
        BudgetBreakdown budget)
    {
        var cities = string.Join("、", days.Select(day => day.City).Distinct());
        var hotelText = hotels.Count > 0
            ? $"住宿优先选择{string.Join("、", hotels.Select(hotel => hotel.Area).Distinct())}"
            : "住宿建议靠近公共交通";
        return $"路线覆盖{cities}，按{PaceText(request.Pace)}节奏安排；{hotelText}。"
               + $"预计总费用 {budget.Total:F0} 元，"
               + (budget.IsOverBudget
                   ? $"超出预算 {Math.Abs(budget.Remaining):F0} 元，已给出调整方案。"
                   : $"保留约 {budget.Remaining:F0} 元机动资金。");
    }

    private static string BuildTheme(IReadOnlyList<TravelActivity> activities) =>
        string.Join(" · ", activities.Select(activity => activity.Category).Distinct().Take(3));

    private static string PaceText(TravelPace pace) => pace switch
    {
        TravelPace.Relaxed => "轻松",
        TravelPace.Intensive => "充实",
        _ => "均衡"
    };

    private static AgentTraceStep Trace(
        int sequence,
        string agent,
        string phase,
        string title,
        string detail) =>
        new(sequence, agent, phase, title, detail, DateTimeOffset.UtcNow);
}
