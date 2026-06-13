using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DotNetAgentDev.Agents;
using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Llm;
using DotNetAgentDev.Models;
using DotNetAgentDev.Options;
using DotNetAgentDev.Services;
using DotNetAgentDev.Tools;

var builder = WebApplication.CreateBuilder(args);

var dotEnvPath = DotEnvConfiguration.FindFile(
    builder.Environment.ContentRootPath,
    Directory.GetCurrentDirectory());
if (dotEnvPath is not null)
{
    builder.Configuration.AddInMemoryCollection(DotEnvConfiguration.Load(dotEnvPath));
}

// Real process environment variables override values from .env.
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
builder.Services.AddHttpClient(
    "TravelWebResearch",
    client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetAgentDev/1.0 travel-research");
    });

builder.Services.AddSingleton<AttractionSearchTool>();
builder.Services.AddSingleton<RouteSortTool>();
builder.Services.AddSingleton<HotelSearchTool>();
builder.Services.AddSingleton<TransportEstimateTool>();
builder.Services.AddSingleton<BudgetCalculatorTool>();
builder.Services.AddSingleton<WeatherLookupTool>();
builder.Services.AddSingleton<RiskCheckTool>();
builder.Services.AddSingleton<PreferenceMemoryTool>();
builder.Services.AddSingleton<TravelWebResearchTool>();

builder.Services.AddSingleton<AgentLoop>();
builder.Services.AddSingleton<ItineraryAgent>();
builder.Services.AddSingleton<HotelAgent>();
builder.Services.AddSingleton<TransportAgent>();
builder.Services.AddSingleton<BudgetAgent>();
builder.Services.AddSingleton<RiskAgent>();
builder.Services.AddSingleton<TravelCoordinatorAgent>();
builder.Services.AddSingleton<TravelPlanningService>();

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

var app = builder.Build();
var streamJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
streamJsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

if (dotEnvPath is not null)
{
    app.Logger.LogInformation("Loaded local environment configuration from {DotEnvPath}.", dotEnvPath);
}

await app.Services.GetRequiredService<PlanningMemoryStore>()
    .NormalizeStoredJsonAsync(CancellationToken.None);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (
    IConfiguration configuration,
    ILlmClient llmClient) =>
{
    var apiKey = configuration["DEEPSEEK_API_KEY"]
                 ?? configuration[$"{DeepSeekOptions.SectionName}:ApiKey"];
    var model = configuration[$"{DeepSeekOptions.SectionName}:Model"] ?? "deepseek-v4-flash";
    var isConfigured = !string.IsNullOrWhiteSpace(apiKey);

    return Results.Ok(new
    {
        mode = llmClient.CurrentMode,
        model,
        configured = isConfigured,
        mcp = new
        {
            enabled = true,
            endpoint = "/mcp",
            transport = "streamable-http",
            tools = 9
        },
        message = llmClient.CurrentMode == "deepseek"
            ? "DeepSeek API 已从配置或 .env 加载。"
            : isConfigured
                ? "DeepSeek API 已配置，但本次运行已因调用失败降级为离线模式。"
                : "未检测到 DeepSeek API Key，当前为离线演示模式。"
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

app.MapPost("/api/plans/stream", async (
    TravelRequest request,
    TravelPlanningService service,
    HttpResponse response,
    CancellationToken cancellationToken) =>
{
    var errors = TravelRequestValidator.Validate(request);
    if (errors.Count > 0)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await response.WriteAsJsonAsync(new { errors }, cancellationToken);
        return;
    }

    response.StatusCode = StatusCodes.Status200OK;
    response.ContentType = "text/event-stream; charset=utf-8";
    response.Headers.CacheControl = "no-cache, no-transform";
    response.Headers.Append("X-Accel-Buffering", "no");

    var channel = Channel.CreateUnbounded<PlanningStreamEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    var producer = ProducePlanEventsAsync(
        request,
        service,
        channel.Writer,
        cancellationToken);

    await foreach (var streamEvent in channel.Reader.ReadAllAsync(cancellationToken))
    {
        var json = JsonSerializer.Serialize(streamEvent, streamJsonOptions);
        await response.WriteAsync($"event: {streamEvent.Type}\n", cancellationToken);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    await producer;
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

app.MapMcp("/mcp");
app.MapFallbackToFile("index.html");
app.Run();

static async Task ProducePlanEventsAsync(
    TravelRequest request,
    TravelPlanningService service,
    ChannelWriter<PlanningStreamEvent> writer,
    CancellationToken cancellationToken)
{
    try
    {
        writer.TryWrite(new PlanningStreamEvent
        {
            Type = "progress",
            Agent = "系统",
            Phase = "Start",
            Title = "流式规划已启动",
            Detail = "服务器已建立 SSE 通道，正在启动多 Agent 协作。",
            Percent = 1
        });

        var plan = await service.CreatePlanAsync(
            request,
            cancellationToken,
            streamEvent => writer.TryWrite(streamEvent));
        writer.TryWrite(new PlanningStreamEvent
        {
            Type = "completed",
            Agent = "主控 Agent",
            Phase = "Completed",
            Title = "旅行方案生成完成",
            Detail = "完整方案已生成并保存。",
            Percent = 100,
            Plan = plan
        });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // The client closed the stream; no final event can be delivered.
    }
    catch (Exception exception)
    {
        writer.TryWrite(new PlanningStreamEvent
        {
            Type = "error",
            Agent = "系统",
            Phase = "Error",
            Title = "规划失败",
            Detail = exception.Message
        });
    }
    finally
    {
        writer.TryComplete();
    }
}

public partial class Program;
