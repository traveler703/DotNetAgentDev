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
                "先由行程 Agent 选择核心城市，并与风险 Agent 并行工作；城市确定后再并行启动酒店与交通 Agent。")
        };
        EmitProgress(onProgress, "trace", coordinatorTrace[0], 5);
        EmitProgress(onProgress, "trace", coordinatorTrace[1], 10);

        var itineraryTask = _itineraryAgent.RunAsync(request, 10, cancellationToken, onProgress);
        var riskTask = _riskAgent.RunAsync(request, 300, cancellationToken, onProgress);
        var itineraryRun = await itineraryTask;
        var routePlan = ParseRoutePlan(itineraryRun);
        var scopedRequest = ApplyRouteScope(request, routePlan);
        var hotelTask = _hotelAgent.RunAsync(scopedRequest, 100, cancellationToken, onProgress);
        var transportTask = _transportAgent.RunAsync(scopedRequest, 200, cancellationToken, onProgress);
        await Task.WhenAll(hotelTask, transportTask, riskTask);

        var hotelRun = await hotelTask;
        var transportRun = await transportTask;
        var riskRun = await riskTask;

        var destinations = await _catalog.FindDestinationsAsync(request.Destination);
        var attractions = ParseAttractions(itineraryRun) ?? BuildFallbackAttractions(request, destinations);
        attractions = FilterAttractionsForRoute(attractions, routePlan);
        var hotels = ParseHotels(hotelRun) ?? BuildFallbackHotels(request, destinations);
        var transport = ParseTransport(transportRun) ?? BuildFallbackTransport(request, destinations);
        transport = ApplyRouteToTransport(transport, routePlan, request.Travelers);
        var research = MergeResearch(
            ParseWebResearch(itineraryRun),
            ParseWebResearch(transportRun));
        var days = BuildDailyPlan(
            request,
            attractions,
            destinations,
            startDate,
            transport,
            research,
            routePlan,
            budgetRevision: false);
        var selectedHotels = SelectHotels(hotels, days, request, budgetRevision: false);
        var expenseDetails = BuildExpenseDetails(request, days, selectedHotels);
        var costs = CalculateCostInputs(request, expenseDetails);

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

        var allRuns = new List<AgentRunResult>
        {
            itineraryRun,
            hotelRun,
            transportRun,
            riskRun,
            budgetRun
        };
        var planningRevisionCount = 0;
        if (budget.Total > request.Budget * 1.10m)
        {
            planningRevisionCount = 1;
            var revisionInstruction =
                $"首次方案预计 {budget.Total:F0} 元，超过预算上限 10%。"
                + "请压缩住宿与交通成本，减少高价或低优先级活动，并保持核心偏好。";
            var revisionTrace = Trace(
                500,
                "主控 Agent",
                "Action",
                "预算超限，启动第二轮规划",
                revisionInstruction);
            coordinatorTrace.Add(revisionTrace);
            EmitProgress(onProgress, "trace", revisionTrace, 78);

            itineraryRun = await _itineraryAgent.RunAsync(
                request,
                510,
                cancellationToken,
                onProgress,
                revisionInstruction);
            routePlan = ParseRoutePlan(itineraryRun) ?? routePlan;
            scopedRequest = ApplyRouteScope(request, routePlan);
            var revisedHotelTask = _hotelAgent.RunAsync(
                scopedRequest,
                610,
                cancellationToken,
                onProgress,
                revisionInstruction);
            var revisedTransportTask = _transportAgent.RunAsync(
                scopedRequest,
                710,
                cancellationToken,
                onProgress,
                revisionInstruction);
            await Task.WhenAll(revisedHotelTask, revisedTransportTask);

            hotelRun = await revisedHotelTask;
            transportRun = await revisedTransportTask;
            attractions = ParseAttractions(itineraryRun) ?? attractions;
            attractions = FilterAttractionsForRoute(attractions, routePlan);
            hotels = ParseHotels(hotelRun) ?? hotels;
            transport = OptimizeTransportForBudget(
                ParseTransport(transportRun) ?? transport,
                request);
            transport = ApplyRouteToTransport(transport, routePlan, request.Travelers);
            research = MergeResearch(
                ParseWebResearch(itineraryRun),
                ParseWebResearch(transportRun));
            days = BuildDailyPlan(
                request,
                attractions,
                destinations,
                startDate,
                transport,
                research,
                routePlan,
                budgetRevision: true);
            selectedHotels = SelectHotels(hotels, days, request, budgetRevision: true);
            expenseDetails = BuildExpenseDetails(request, days, selectedHotels);
            costs = CalculateCostInputs(request, expenseDetails);
            budgetContext = costs.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString(CultureInfo.InvariantCulture));
            var revisedBudgetRun = await _budgetAgent.RunAsync(
                request,
                budgetContext,
                810,
                cancellationToken,
                onProgress);
            budget = ParseBudget(revisedBudgetRun) ?? BuildFallbackBudget(request, costs);
            allRuns.AddRange([itineraryRun, hotelRun, transportRun, revisedBudgetRun]);

            var revisionResultTrace = Trace(
                850,
                "主控 Agent",
                "Observation",
                "第二轮预算复核完成",
                budget.IsOverBudget
                    ? $"重规划后预计 {budget.Total:F0} 元，仍超预算 {Math.Abs(budget.Remaining):F0} 元。"
                    : $"重规划后预计 {budget.Total:F0} 元，已回到预算内并保留 {budget.Remaining:F0} 元余量。");
            coordinatorTrace.Add(revisionResultTrace);
            EmitProgress(onProgress, "trace", revisionResultTrace, 88);
        }

        var risks = ParseRisks(riskRun);
        risks.AddRange(ParseWeatherRisks(riskRun));
        risks.InsertRange(0, BuildWebRiskNotices(ParseWebResearch(riskRun), riskRun.FinalAnswer));
        var suggestions = BuildSuggestions(request, budget, transport, days);

        var conflictTrace = Trace(
            900,
            "主控 Agent",
            "Thought",
            "执行冲突检查",
            budget.IsOverBudget
                ? $"预算超出 {Math.Abs(budget.Remaining):F0} 元，将优先给出住宿、交通和门票调整建议。"
                : $"预算内仍有 {budget.Remaining:F0} 元机动空间，保留用于价格波动。");
        coordinatorTrace.Add(conflictTrace);
        EmitProgress(onProgress, "trace", conflictTrace, 90);
        var finalTrace = Trace(
            901,
            "主控 Agent",
            "FinalAnswer",
            "生成统一旅行方案",
            $"已整合 {request.Days} 天日程、住宿、交通、预算与 {risks.Count} 条风险提醒。");
        coordinatorTrace.Add(finalTrace);
        EmitProgress(onProgress, "trace", finalTrace, 96);

        var allTrace = coordinatorTrace
            .Concat(allRuns.SelectMany(run => run.Trace))
            .OrderBy(step => step.Sequence)
            .Select((step, index) => step with { Sequence = index + 1 })
            .ToList();
        var contributions = allRuns
            .GroupBy(run => run.AgentName)
            .Select(group =>
            {
                var latest = group.Last();
                var contribution = ToContribution(latest);
                return contribution with { ToolCallCount = group.Sum(run => run.ToolCallCount) };
            })
            .ToList();
        contributions.Insert(0, new AgentContribution(
            "主控 Agent",
            "任务分解、冲突处理与结果整合",
            planningRevisionCount > 0
                ? $"预算超过 110% 后已完成第 {planningRevisionCount + 1} 版方案。"
                : budget.IsOverBudget
                    ? "完成整合并标记预算超支。"
                    : "完成整合并通过预算约束。",
            0));

        var plan = new TravelPlan
        {
            Request = request,
            Title = $"{request.Destination} {request.Days} 天个性化旅行计划",
            Summary = BuildSummary(request, days, budget, planningRevisionCount),
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
            ExpenseDetails = expenseDetails,
            PlanningRevisionCount = planningRevisionCount,
            ModelMode = allRuns.All(run => run.ModelMode == "deepseek") ? "deepseek" : "offline"
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

    private static RoutePlan? ParseRoutePlan(AgentRunResult run)
    {
        if (!run.Observations.TryGetValue("route_sort", out var json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<RoutePlan>(json, JsonOptions);
    }

    private static TravelRequest ApplyRouteScope(TravelRequest request, RoutePlan? routePlan)
    {
        if (routePlan is null || routePlan.OrderedCities.Count == 0)
        {
            return request;
        }

        var cities = string.Join("、", routePlan.OrderedCities.Select(city => city.City));
        return request with
        {
            Destination = $"{request.Destination}（核心城市：{cities}）",
            Notes = string.IsNullOrWhiteSpace(request.Notes)
                ? $"核心城市已确定为：{cities}。"
                : $"{request.Notes}；核心城市已确定为：{cities}。"
        };
    }

    private static IReadOnlyList<AttractionCandidate> FilterAttractionsForRoute(
        IReadOnlyList<AttractionCandidate> attractions,
        RoutePlan? routePlan)
    {
        if (routePlan is null || routePlan.OrderedCities.Count == 0)
        {
            return attractions;
        }

        var cities = routePlan.OrderedCities
            .Select(city => city.City)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filtered = attractions
            .Where(candidate => cities.Contains(candidate.City))
            .ToList();
        return filtered.Count > 0 ? filtered : attractions;
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

    private static WebResearchReport? ParseWebResearch(AgentRunResult run)
    {
        if (!run.Observations.TryGetValue("travel_web_research", out var json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<WebResearchReport>(json, JsonOptions);
    }

    private static WebResearchReport? MergeResearch(params WebResearchReport?[] reports)
    {
        var available = reports.Where(report => report is not null).Cast<WebResearchReport>().ToList();
        if (available.Count == 0)
        {
            return null;
        }

        return new WebResearchReport(
            available.Max(report => report.SearchedAt),
            string.Join(" + ", available.Select(report => report.Source).Distinct()),
            string.Join(" ", available.Select(report => report.Disclaimer).Distinct()),
            available.SelectMany(report => report.Sections)
                .GroupBy(section => section.Topic, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var results = group.SelectMany(section => section.Results)
                        .DistinctBy(item => item.Url)
                        .OrderByDescending(item => item.LooksOfficial)
                        .Take(5)
                        .ToList();
                    return new WebResearchSection(
                        group.Key,
                        string.Join("；", group.Select(section => section.Query).Distinct()),
                        results,
                        group.Select(section => section.Error).FirstOrDefault(error => error is not null));
                })
                .ToList());
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

    private static List<RiskNotice> BuildWebRiskNotices(
        WebResearchReport? report,
        string llmSummary)
    {
        if (report is null)
        {
            return [];
        }

        var configuration = new Dictionary<string, (string Level, string Title, string Recommendation)>
        {
            ["visa"] = (
                "high",
                "签证与入境联网核验",
                "打开使领馆、移民或目的地政府来源，按自己的护照、停留目的和转机方式逐项确认。"),
            ["weather"] = (
                "medium",
                "天气与气候联网核验",
                "临近出发前 7 天和 24 小时分别复查权威天气预报，并准备室内替代安排。"),
            ["disaster"] = (
                "medium",
                "自然灾害与应急信息",
                "关注目的地官方预警，确认酒店疏散路线、保险范围和当地紧急联络方式。"),
            ["safety"] = (
                "medium",
                "社会治安与旅行安全",
                "查看领事提醒和当地警方建议，避开高风险区域并保管好证件与支付工具。")
        };

        var notices = new List<RiskNotice>();
        foreach (var section in report.Sections.Where(section => configuration.ContainsKey(section.Topic)))
        {
            var setup = configuration[section.Topic];
            var useful = section.Results.Take(3).ToList();
            var detail = useful.Count == 0
                ? section.Error is null
                    ? "联网检索未找到通过相关性校验的官方来源，请使用下方建议中的官方渠道人工复核。"
                    : $"联网检索失败：{section.Error}"
                : string.Join(" ", useful.Select(result => $"{result.Title}：{Limit(result.Snippet, 180)}"));
            notices.Add(new RiskNotice(
                setup.Level,
                section.Topic,
                setup.Title,
                detail,
                setup.Recommendation,
                useful.Select(ToSourceReference).ToList()));
        }

        var allSources = report.Sections.SelectMany(section => section.Results)
            .DistinctBy(item => item.Url)
            .Take(6)
            .Select(ToSourceReference)
            .ToList();
        if (!string.IsNullOrWhiteSpace(llmSummary))
        {
            notices.Add(new RiskNotice(
                "medium",
                "web-research",
                "风险 Agent 联网综合结论",
                Limit(llmSummary, 600),
                report.Disclaimer,
                allSources));
        }

        return notices;
    }

    private static SourceReference ToSourceReference(WebResearchItem item) =>
        new(item.Title, item.Url, item.PublishedAt);

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

    private static TransportSummary OptimizeTransportForBudget(
        TransportSummary transport,
        TravelRequest request)
    {
        var currentTotal = transport.OutboundCost + transport.LocalCost + transport.IntercityCost;
        var target = request.Budget * 0.4m;
        if (currentTotal <= target || currentTotal <= 0)
        {
            return transport;
        }

        var ratio = target / currentTotal;
        return transport with
        {
            OutboundDescription = transport.OutboundDescription
                                  + " 第二轮预算规划按错峰经济舱、廉航或提前购票目标价重新估算，实际下单前必须复核。",
            OutboundCost = decimal.Round(transport.OutboundCost * ratio, 2),
            LocalCost = decimal.Round(transport.LocalCost * ratio, 2),
            IntercityCost = decimal.Round(transport.IntercityCost * ratio, 2),
            RouteNotes = transport.RouteNotes
                .Append("预算超限后已将交通目标压缩到总预算约 40%，优先选择错峰直达班次和公共交通。")
                .ToList()
        };
    }

    private static TransportSummary ApplyRouteToTransport(
        TransportSummary transport,
        RoutePlan? routePlan,
        int travelers)
    {
        var cityCount = routePlan?.OrderedCities.Count ?? 0;
        if (cityCount <= 1 || transport.IntercityCost > 0)
        {
            return transport;
        }

        var cityNames = string.Join(" → ", routePlan!.OrderedCities.Select(city => city.City));
        return transport with
        {
            IntercityCost = (cityCount - 1) * 360m * Math.Max(1, travelers),
            EstimatedTravelMinutes = transport.EstimatedTravelMinutes + (cityCount - 1) * 180,
            RouteNotes = transport.RouteNotes
                .Append($"已按行程 Agent 选择的城市顺序安排跨城交通：{cityNames}。")
                .ToList()
        };
    }

    private static IReadOnlyList<WebResearchItem> GetResearchResults(
        WebResearchReport? report,
        string topic) =>
        report?.Sections.FirstOrDefault(section =>
                string.Equals(section.Topic, topic, StringComparison.OrdinalIgnoreCase))
            ?.Results
        ?? [];

    private static TravelActivity CreateActivity(
        string time,
        string endTime,
        string name,
        string category,
        string venue,
        string description,
        ActivityCostBreakdown breakdown,
        WebResearchItem? source) =>
        new(
            time,
            name,
            category,
            description,
            decimal.Round(breakdown.Total, 2),
            CalculateDuration(time, endTime),
            venue,
            endTime,
            venue,
            breakdown,
            source?.Title,
            source?.Url);

    private static int CalculateDuration(string start, string end)
    {
        if (!TimeOnly.TryParse(start, out var startTime)
            || !TimeOnly.TryParse(end, out var endTime))
        {
            return 60;
        }

        var duration = endTime.ToTimeSpan() - startTime.ToTimeSpan();
        if (duration < TimeSpan.Zero)
        {
            duration += TimeSpan.FromDays(1);
        }

        return Math.Max(15, (int)duration.TotalMinutes);
    }

    private static (string Breakfast, string Lunch, string Dinner) GetCuisineSuggestions(
        string city,
        string destination)
    {
        var value = $"{destination}{city}".ToLowerInvariant();
        if (value.Contains("日本") || value.Contains("东京") || value.Contains("京都")
            || value.Contains("大阪") || value.Contains("japan") || value.Contains("tokyo")
            || value.Contains("kyoto") || value.Contains("osaka"))
        {
            return ("饭团、味噌汤或玉子烧", "寿司、荞麦面或当地定食", "拉面、烧鸟或大阪烧");
        }

        if (value.Contains("台湾") || value.Contains("台北") || value.Contains("高雄")
            || value.Contains("taiwan") || value.Contains("taipei"))
        {
            return ("蛋饼、饭团与豆浆", "牛肉面或卤肉饭", "夜市小吃、盐酥鸡与珍珠奶茶");
        }

        if (value.Contains("香港") || value.Contains("hong kong") || value.Contains("hongkong"))
        {
            return ("菠萝油、沙嗲牛肉面与港式奶茶", "云吞面、烧味饭或港式点心", "避风塘海鲜、煲仔饭或庙街小吃");
        }

        if (value.Contains("新加坡") || value.Contains("singapore"))
        {
            return ("咖椰吐司与南洋咖啡", "海南鸡饭或叻沙", "熟食中心沙爹与海鲜");
        }

        if (value.Contains("越南") || value.Contains("河内") || value.Contains("岘港")
            || value.Contains("胡志明") || value.Contains("vietnam"))
        {
            return ("越南法棍、河粉与滴漏咖啡", "越南河粉、烤肉米线或鸡饭", "春卷、海鲜、越式火锅或街头小吃");
        }

        if (value.Contains("成都"))
        {
            return ("红油抄手或担担面", "川菜小馆或串串", "火锅或夜市小吃");
        }

        if (value.Contains("杭州"))
        {
            return ("小笼、豆浆或片儿川", "杭帮菜与龙井虾仁", "东坡肉、面馆或湖滨小吃");
        }

        return ("当地早餐与咖啡", "当地代表性主食或套餐", "本地特色菜与夜间小吃");
    }

    private static IReadOnlyList<string> BuildCitySchedule(
        IReadOnlyList<string> cities,
        RoutePlan? routePlan,
        int days)
    {
        var schedule = new List<string>(days);
        if (routePlan is not null)
        {
            foreach (var city in routePlan.OrderedCities)
            {
                schedule.AddRange(Enumerable.Repeat(city.City, Math.Max(1, city.RecommendedDays)));
            }
        }

        if (schedule.Count == 0)
        {
            for (var index = 0; index < days; index++)
            {
                schedule.Add(cities[Math.Min(cities.Count - 1, index * cities.Count / days)]);
            }
        }

        while (schedule.Count < days)
        {
            schedule.Add(schedule[^1]);
        }

        return schedule.Take(days).ToList();
    }

    private static IReadOnlyList<DayPlan> BuildDailyPlan(
        TravelRequest request,
        IReadOnlyList<AttractionCandidate> attractions,
        IReadOnlyList<DestinationData> destinations,
        DateOnly startDate,
        TransportSummary transport,
        WebResearchReport? research,
        RoutePlan? routePlan,
        bool budgetRevision)
    {
        var activitiesPerDay = request.Pace switch
        {
            TravelPace.Relaxed => 2,
            TravelPace.Intensive => 4,
            _ => 3
        };
        if (budgetRevision)
        {
            activitiesPerDay = Math.Max(1, activitiesPerDay - 1);
        }

        var cities = routePlan?.OrderedCities
            .Select(city => city.City)
            .Distinct()
            .ToList()
            ?? [];
        if (cities.Count == 0)
        {
            cities.AddRange(destinations.Select(destination => destination.City).Distinct());
        }
        if (cities.Count == 0)
        {
            cities.AddRange(attractions
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.City))
                .Select(candidate => candidate.City)
                .Distinct());
        }
        if (cities.Count == 0)
        {
            cities.Add(request.Destination);
        }
        var citySchedule = BuildCitySchedule(cities, routePlan, request.Days);

        var cityQueues = cities.ToDictionary(
            city => city,
            city => new Queue<AttractionCandidate>(
                attractions.Where(candidate => candidate.City == city)
                    .OrderBy(candidate => budgetRevision ? candidate.TicketPrice : 0)
                    .ThenByDescending(candidate => candidate.Score)));
        var unused = new Queue<AttractionCandidate>(
            attractions.Where(item => cities.Contains(item.City))
                .OrderByDescending(item => item.Score));
        var days = new List<DayPlan>();
        var itinerarySources = GetResearchResults(research, "itinerary");
        var foodSources = GetResearchResults(research, "food");
        var transportSources = GetResearchResults(research, "transport");
        var localPerDay = decimal.Round(transport.LocalCost / Math.Max(1, request.Days), 2);
        var intercityTransitions = Enumerable.Range(1, request.Days - 1)
            .Count(index =>
            {
                var previousCity = citySchedule[index - 1];
                var currentCity = citySchedule[index];
                return previousCity != currentCity;
            });
        var intercityPerTransition = intercityTransitions == 0
            ? 0
            : decimal.Round(transport.IntercityCost / intercityTransitions, 2);

        for (var index = 0; index < request.Days; index++)
        {
            var city = citySchedule[index];
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

            var dailyBase = destinations.FirstOrDefault(destination => destination.City == city);
            var dailyFood = (dailyBase?.FoodPerDay ?? 220) * request.Travelers;
            if (budgetRevision)
            {
                dailyFood = decimal.Round(dailyFood * 0.82m, 2);
            }

            var activities = new List<TravelActivity>();
            var previousCity = index == 0
                ? city
                : citySchedule[index - 1];
            if (index == 0)
            {
                var outboundCost = request.Days == 1
                    ? transport.OutboundCost
                    : decimal.Round(transport.OutboundCost / 2, 2);
                activities.Add(CreateActivity(
                    "07:30",
                    "13:00",
                    $"建议搭乘 09:00 左右的{transport.OutboundMode}",
                    "交通",
                    request.Departure,
                    $"07:30 前往机场或车站办理手续，建议选择上午班次抵达 {city}；具体航班、车次与票价以预订页面为准。",
                    new ActivityCostBreakdown(outboundCost, 0, 0, 0),
                    transportSources.ElementAtOrDefault(0)));
            }
            else if (previousCity != city)
            {
                activities.Add(CreateActivity(
                    "08:00",
                    "11:00",
                    $"{previousCity} → {city} 跨城移动",
                    "交通",
                    "铁路或城际交通枢纽",
                    "早餐后退房并前往车站，优先选择上午直达班次，到站后寄存行李再开始游览。",
                    new ActivityCostBreakdown(intercityPerTransition, 0, 0, 0),
                    transportSources.ElementAtOrDefault(1)));
            }

            var cuisines = GetCuisineSuggestions(city, request.Destination);
            activities.Add(CreateActivity(
                index == 0 ? "13:15" : "08:00",
                index == 0 ? "14:00" : "08:40",
                index == 0 ? $"午餐：品尝{cuisines.Lunch}" : $"早餐：品尝{cuisines.Breakfast}",
                "餐饮",
                $"{city}交通便利区域",
                index == 0
                    ? "抵达后先安排就近用餐，避免空腹直接进入高强度活动。"
                    : "选择酒店或车站附近的当地早餐，控制通勤时间。",
                new ActivityCostBreakdown(
                    0,
                    0,
                    decimal.Round(dailyFood * (index == 0 ? 0.42m : 0.22m), 2),
                    0),
                foodSources.ElementAtOrDefault(index % Math.Max(1, foodSources.Count))));

            activities.Add(CreateActivity(
                index == 0 ? "14:10" : "08:50",
                index == 0 ? "14:40" : "09:20",
                "市内交通与步行接驳",
                "交通",
                city,
                "优先使用公共交通，同一区域景点以步行串联；费用为当日交通预算。",
                new ActivityCostBreakdown(localPerDay, 0, 0, 0),
                transportSources.ElementAtOrDefault(2)));

            var attractionTimes = index == 0
                ? new[] { ("15:00", "17:30") }
                : activitiesPerDay switch
                {
                    1 => new[] { ("09:30", "12:00") },
                    2 => new[] { ("09:30", "12:00"), ("14:30", "17:00") },
                    4 => new[]
                    {
                        ("09:30", "11:30"),
                        ("13:30", "15:00"),
                        ("15:30", "17:00"),
                        ("18:00", "20:00")
                    },
                    _ => new[] { ("09:30", "11:30"), ("14:00", "16:00"), ("17:00", "19:00") }
                };
            foreach (var (candidate, activityIndex) in selected.Select((candidate, activityIndex) => (candidate, activityIndex)))
            {
                var slot = attractionTimes[Math.Min(activityIndex, attractionTimes.Length - 1)];
                var source = itinerarySources.ElementAtOrDefault(
                    (index * Math.Max(1, activitiesPerDay) + activityIndex)
                    % Math.Max(1, itinerarySources.Count));
                activities.Add(CreateActivity(
                    slot.Item1,
                    slot.Item2,
                    candidate.Name,
                    candidate.Category,
                    string.IsNullOrWhiteSpace(candidate.Area) ? city : candidate.Area,
                    candidate.Description,
                    new ActivityCostBreakdown(
                        0,
                        candidate.TicketPrice * request.Travelers,
                        0,
                        0),
                    source));
            }

            if (index != 0)
            {
                activities.Add(CreateActivity(
                    "12:15",
                    "13:15",
                    $"午餐：品尝{cuisines.Lunch}",
                    "餐饮",
                    $"{city}当日游览区域",
                    "选择当日景点附近的本地料理，减少折返。",
                    new ActivityCostBreakdown(0, 0, decimal.Round(dailyFood * 0.38m, 2), 0),
                    foodSources.ElementAtOrDefault((index + 1) % Math.Max(1, foodSources.Count))));
            }

            activities.Add(CreateActivity(
                "18:30",
                "20:00",
                $"晚餐：品尝{cuisines.Dinner}",
                "餐饮",
                $"{city}夜间餐饮区域",
                "晚餐后根据体力选择夜景散步或直接返回酒店。",
                new ActivityCostBreakdown(
                    0,
                    0,
                    decimal.Round(
                        dailyFood - activities.Sum(activity => activity.CostBreakdown?.Food ?? 0),
                        2),
                    0),
                foodSources.ElementAtOrDefault((index + 2) % Math.Max(1, foodSources.Count))));

            if (index == request.Days - 1 && request.Days > 1)
            {
                activities.Add(CreateActivity(
                    "20:00",
                    "23:30",
                    $"搭乘返程{transport.OutboundMode}",
                    "交通",
                    $"{city}机场或车站",
                    "至少提前到达交通枢纽，返程时段需在购票后按实际班次调整当天行程。",
                    new ActivityCostBreakdown(
                        transport.OutboundCost - decimal.Round(transport.OutboundCost / 2, 2),
                        0,
                        0,
                        0),
                    transportSources.ElementAtOrDefault(0)));
            }

            activities = activities.OrderBy(activity => activity.Time).ToList();
            var breakdown = new DayCostBreakdown(
                activities.Sum(activity => activity.CostBreakdown?.Transport ?? 0),
                activities.Sum(activity => activity.CostBreakdown?.Tickets ?? 0),
                activities.Sum(activity => activity.CostBreakdown?.Food ?? 0),
                activities.Sum(activity => activity.CostBreakdown?.Other ?? 0));

            days.Add(new DayPlan(
                index + 1,
                startDate.AddDays(index),
                city,
                BuildTheme(activities),
                activities,
                decimal.Round(breakdown.Total, 2),
                index == 0
                    ? "抵达日安排明确交通时段、用餐和一个核心景点，优先适应环境。"
                    : index == request.Days - 1
                        ? "返程日预留整理行李、前往交通枢纽和误点缓冲。"
                        : budgetRevision
                            ? "第二轮按严格预算重排，保留核心景点并减少高价项目。"
                            : $"按{PaceText(request.Pace)}节奏安排，并保留临时调整空间。",
                breakdown));
        }

        return days;
    }

    private static IReadOnlyList<HotelRecommendation> SelectHotels(
        IReadOnlyList<HotelRecommendation> hotels,
        IReadOnlyList<DayPlan> days,
        TravelRequest request,
        bool budgetRevision)
    {
        var nightlyBudget = request.Budget * (budgetRevision ? 0.23m : 0.3m)
                            / Math.Max(1, request.Days - 1);
        var cities = days.Select(day => day.City).Distinct();
        return cities.Select(city =>
            {
                var cityCandidate = hotels.Where(hotel => hotel.City == city)
                                    .OrderBy(hotel => hotel.PricePerNight > nightlyBudget)
                                    .ThenBy(hotel => budgetRevision ? hotel.PricePerNight : 0)
                                    .ThenByDescending(hotel => hotel.Score)
                                    .FirstOrDefault();
                var candidate = cityCandidate
                                ?? hotels.OrderBy(hotel => hotel.PricePerNight > nightlyBudget)
                                    .ThenBy(hotel => budgetRevision ? hotel.PricePerNight : 0)
                                    .ThenByDescending(hotel => hotel.Score)
                                    .First();
                if (budgetRevision && candidate.PricePerNight > nightlyBudget)
                {
                    return new HotelRecommendation(
                        city,
                        $"{city}交通便利型住宿（预算上限）",
                        "公共交通站点周边",
                        decimal.Round(nightlyBudget, 0),
                        "经济型",
                        "第二轮预算规划采用住宿价格上限，具体房源需在预订平台再次筛选确认。",
                        8.0);
                }

                return cityCandidate is not null
                    ? candidate
                    : candidate with
                    {
                        City = city,
                        Name = $"{city}公共交通附近住宿",
                        Area = "核心线路公共交通站点周边",
                        Reason = "住宿工具未返回该城市的具体房源，按同档预算生成城市级筛选条件，预订前需再次查询。"
                    };
            })
            .DistinctBy(hotel => hotel.Name)
            .ToList();
    }

    private static IReadOnlyList<ExpenseDetail> BuildExpenseDetails(
        TravelRequest request,
        IReadOnlyList<DayPlan> days,
        IReadOnlyList<HotelRecommendation> hotels)
    {
        var details = new List<ExpenseDetail>();
        foreach (var day in days)
        {
            foreach (var activity in day.Activities)
            {
                AddExpense(details, "transport", day.Date, activity, activity.CostBreakdown?.Transport ?? 0);
                AddExpense(details, "tickets", day.Date, activity, activity.CostBreakdown?.Tickets ?? 0);
                AddExpense(details, "food", day.Date, activity, activity.CostBreakdown?.Food ?? 0);
                AddExpense(details, "other", day.Date, activity, activity.CostBreakdown?.Other ?? 0);
            }
        }

        var rooms = (int)Math.Ceiling(request.Travelers / 2m);
        foreach (var day in days.Take(Math.Max(0, request.Days - 1)))
        {
            var hotel = hotels.FirstOrDefault(item => item.City == day.City) ?? hotels.FirstOrDefault();
            if (hotel is null)
            {
                continue;
            }

            details.Add(new ExpenseDetail(
                "accommodation",
                day.Date,
                $"{hotel.Name} · {rooms} 间",
                $"{hotel.City} {hotel.Area}，按每晚估算。",
                hotel.PricePerNight * rooms));
        }

        var subtotal = details.Sum(detail => detail.Amount);
        var other = decimal.Round(subtotal * 0.06m, 2);
        details.Add(new ExpenseDetail(
            "other",
            null,
            "保险、通信与机动预留",
            "按交通、住宿、餐饮和门票小计的 6% 估算。",
            other));
        return details;
    }

    private static void AddExpense(
        ICollection<ExpenseDetail> details,
        string category,
        DateOnly date,
        TravelActivity activity,
        decimal amount)
    {
        if (amount <= 0)
        {
            return;
        }

        details.Add(new ExpenseDetail(
            category,
            date,
            activity.Name,
            $"{activity.Time}-{activity.EndTime} · {activity.Venue}",
            amount));
    }

    private static Dictionary<string, decimal> CalculateCostInputs(
        TravelRequest request,
        IReadOnlyList<ExpenseDetail> details)
    {
        decimal Sum(string category) => details
            .Where(detail => detail.Category == category)
            .Sum(detail => detail.Amount);

        return new Dictionary<string, decimal>
        {
            ["budgetLimit"] = request.Budget,
            ["transport"] = decimal.Round(Sum("transport"), 2),
            ["accommodation"] = decimal.Round(Sum("accommodation"), 2),
            ["food"] = decimal.Round(Sum("food"), 2),
            ["tickets"] = decimal.Round(Sum("tickets"), 2),
            ["other"] = decimal.Round(Sum("other"), 2)
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
        BudgetBreakdown budget,
        int planningRevisionCount)
    {
        var cities = string.Join("、", days.Select(day => day.City).Distinct());
        var highlights = days.SelectMany(day => day.Activities)
            .Where(activity => activity.Category is not "交通" and not "餐饮")
            .Select(activity => activity.Name)
            .Distinct()
            .Take(5)
            .ToList();
        var highlightText = highlights.Count == 0
            ? "以城市漫步与当地体验为主"
            : $"主要安排{string.Join("、", highlights)}";
        var revisionText = planningRevisionCount > 0
            ? "首次方案超过预算 110%，已完成行程、住宿、交通和预算的第二轮重排。"
            : string.Empty;
        return $"路线覆盖{cities}，按{PaceText(request.Pace)}节奏安排；{highlightText}。"
               + $"预计总费用 {budget.Total:F0} 元，"
               + (budget.IsOverBudget
                   ? $"超出预算 {Math.Abs(budget.Remaining):F0} 元。"
                   : $"保留约 {budget.Remaining:F0} 元机动资金。")
               + revisionText;
    }

    private static string BuildTheme(IReadOnlyList<TravelActivity> activities) =>
        string.Join(
            " · ",
            activities.Where(activity => activity.Category is not "交通" and not "餐饮")
                .Select(activity => activity.Category)
                .Distinct()
                .DefaultIfEmpty("城市体验")
                .Take(3));

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : $"{value[..maxLength]}...";

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
