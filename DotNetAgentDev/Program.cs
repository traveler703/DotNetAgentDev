using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetAgentDev.Agents;
using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Llm;
using DotNetAgentDev.Models;
using DotNetAgentDev.Options;
using DotNetAgentDev.Services;
using DotNetAgentDev.Tools;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<DeepSeekOptions>(builder.Configuration.GetSection(DeepSeekOptions.SectionName));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.AddHttpClient<DeepSeekLlmClient>();
builder.Services.AddSingleton<OfflineLlmClient>();
builder.Services.AddSingleton<ILlmClient, ResilientLlmClient>();
builder.Services.AddSingleton<TourismCatalog>();
builder.Services.AddSingleton<PlanningMemoryStore>();
builder.Services.AddSingleton<ToolRegistry>();

builder.Services.AddSingleton<AttractionSearchTool>();
builder.Services.AddSingleton<RouteSortTool>();
builder.Services.AddSingleton<HotelSearchTool>();
builder.Services.AddSingleton<TransportEstimateTool>();
builder.Services.AddSingleton<BudgetCalculatorTool>();
builder.Services.AddSingleton<WeatherLookupTool>();
builder.Services.AddSingleton<RiskCheckTool>();
builder.Services.AddSingleton<PreferenceMemoryTool>();

builder.Services.AddSingleton<AgentLoop>();
builder.Services.AddSingleton<ItineraryAgent>();
builder.Services.AddSingleton<HotelAgent>();
builder.Services.AddSingleton<TransportAgent>();
builder.Services.AddSingleton<BudgetAgent>();
builder.Services.AddSingleton<RiskAgent>();
builder.Services.AddSingleton<TravelCoordinatorAgent>();
builder.Services.AddSingleton<TravelPlanningService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (IConfiguration configuration) =>
{
    var apiKey = configuration["DEEPSEEK_API_KEY"]
                 ?? configuration[$"{DeepSeekOptions.SectionName}:ApiKey"];
    var model = configuration[$"{DeepSeekOptions.SectionName}:Model"] ?? "deepseek-v4-flash";

    return Results.Ok(new
    {
        mode = string.IsNullOrWhiteSpace(apiKey) ? "offline" : "deepseek",
        model,
        message = string.IsNullOrWhiteSpace(apiKey)
            ? "当前为离线演示模式，仍会完整执行多 Agent 与工具调用流程。"
            : "DeepSeek API 已配置。"
    });
});

app.MapPost("/api/plans", async (
    TravelRequest request,
    TravelPlanningService service,
    CancellationToken cancellationToken) =>
{
    var errors = TravelRequestValidator.Validate(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var plan = await service.CreatePlanAsync(request, cancellationToken);
    return Results.Ok(plan);
});

app.MapGet("/api/plans", async (
    string? userId,
    PlanningMemoryStore memory,
    CancellationToken cancellationToken) =>
{
    var plans = await memory.GetRecentPlansAsync(userId ?? "demo-user", 12, cancellationToken);
    return Results.Ok(plans);
});

app.MapGet("/api/plans/{id:guid}", async (
    Guid id,
    PlanningMemoryStore memory,
    CancellationToken cancellationToken) =>
{
    var plan = await memory.GetPlanAsync(id, cancellationToken);
    return plan is null ? Results.NotFound() : Results.Ok(plan);
});

app.MapGet("/api/memory/{userId}", async (
    string userId,
    PlanningMemoryStore memory,
    CancellationToken cancellationToken) =>
{
    var profile = await memory.GetProfileAsync(userId, cancellationToken);
    return Results.Ok(profile);
});

app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
