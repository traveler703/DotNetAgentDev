using System.Text.Encodings.Web;
using System.Text.Json;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

internal static class ToolSupport
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static T Parse<T>(string arguments) where T : class =>
        JsonSerializer.Deserialize<T>(arguments, JsonOptions)
        ?? throw new ArgumentException("工具参数不能为空。");

    public static string ToJson<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    public static ToolExecutionResult Success<T>(T value) =>
        new(true, ToJson(value));

    public static ToolExecutionResult Failure(Exception exception) =>
        new(false, ToJson(new { error = exception.Message }), exception.Message);

    public static string GetSeason(int month) => month switch
    {
        3 or 4 or 5 => "spring",
        6 or 7 or 8 => "summer",
        9 or 10 or 11 => "autumn",
        _ => "winter"
    };
}
