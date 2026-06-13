using System.Globalization;
using System.Xml.Linq;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class TravelWebResearchTool(
    IHttpClientFactory httpClientFactory,
    ILogger<TravelWebResearchTool> logger) : IAgentTool
{
    private static readonly HashSet<string> SupportedTopics =
    [
        "itinerary",
        "transport",
        "food",
        "visa",
        "weather",
        "disaster",
        "safety"
    ];

    public ToolDefinition Definition { get; } = new(
        "travel_web_research",
        "联网检索旅游资料，覆盖景点与美食、交通时刻、签证入境、天气、自然灾害和社会治安；返回网页摘要与来源链接。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "departure": { "type": "string", "description": "出发地" },
                               "destination": { "type": "string", "description": "目的地国家或城市" },
                               "startDate": { "type": "string", "description": "出发日期 yyyy-MM-dd" },
                               "days": { "type": "integer", "minimum": 1, "maximum": 30 },
                               "topics": {
                                 "type": "string",
                                 "description": "逗号分隔的主题，可选 itinerary,transport,food,visa,weather,disaster,safety"
                               }
                             },
                             "required": ["departure", "destination", "startDate", "days", "topics"],
                             "additionalProperties": false
                           }
                           """));

    public async Task<ToolExecutionResult> ExecuteAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var input = ToolSupport.Parse<Input>(arguments);
        var topics = input.Topics
            .Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries)
            .Select(topic => topic.Trim().ToLowerInvariant())
            .Where(SupportedTopics.Contains)
            .Distinct()
            .Take(7)
            .ToList();
        if (topics.Count == 0)
        {
            throw new ArgumentException("topics 至少需要包含一个支持的联网检索主题。");
        }

        var tasks = topics.Select(topic => SearchTopicAsync(input, topic, cancellationToken));
        var sections = await Task.WhenAll(tasks);
        return ToolSupport.Success(new WebResearchReport(
            DateTimeOffset.UtcNow,
            "Bing RSS web search",
            "联网结果是搜索摘要，不等同于预订确认或官方实时结论；签证、灾害和交通时刻必须打开来源页面复核。",
            sections));
    }

    private async Task<WebResearchSection> SearchTopicAsync(
        Input input,
        string topic,
        CancellationToken cancellationToken)
    {
        var profile = BuildSearchProfile(input, topic);
        try
        {
            var client = httpClientFactory.CreateClient("TravelWebResearch");
            var url = $"https://www.bing.com/search?format=rss&q={Uri.EscapeDataString(profile.Query)}";
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await XDocument.LoadAsync(
                stream,
                LoadOptions.None,
                cancellationToken);
            var searchResults = document.Descendants("item")
                .Select(item => new WebResearchItem(
                    Clean(item.Element("title")?.Value),
                    Clean(item.Element("link")?.Value),
                    Clean(item.Element("description")?.Value),
                    NormalizeDate(item.Element("pubDate")?.Value),
                    LooksOfficial(item.Element("link")?.Value)))
                .Where(item => item.Title.Length > 0 && Uri.IsWellFormedUriString(item.Url, UriKind.Absolute))
                .Select(item => new
                {
                    Item = item,
                    Score = ScoreResult(item, input.Destination, topic, profile)
                })
                .Where(candidate => candidate.Score >= MinimumScore(topic))
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.Item.PublishedAt)
                .Select(candidate => candidate.Item)
                .Take(5)
                .ToList();
            var results = searchResults
                .Concat(BuildOfficialFallbackResults(input.Destination, topic))
                .DistinctBy(item => item.Url)
                .Take(5)
                .ToList();

            return new WebResearchSection(topic, profile.Query, results);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Web research failed for topic {Topic}.", topic);
            return new WebResearchSection(topic, profile.Query, [], exception.Message);
        }
    }

    private static SearchProfile BuildSearchProfile(Input input, string topic)
    {
        var date = DateOnly.TryParse(input.StartDate, out var parsed)
            ? parsed.ToString("yyyy年M月", CultureInfo.GetCultureInfo("zh-CN"))
            : input.StartDate;
        var hosts = GetPreferredHosts(input.Destination, topic);
        var siteHint = hosts.Count > 0 ? $" site:{hosts[0]}" : string.Empty;
        var query = topic switch
        {
            "itinerary" => $"{input.Destination} {date} 官方旅游 景点 开放时间 行程推荐{siteHint}",
            "transport" => $"{input.Departure} 到 {input.Destination} {input.StartDate} 航班 火车 时刻表 机场 官方",
            "food" => $"{input.Destination} 官方旅游 美食 特色料理 推荐{siteHint}",
            "visa" => $"{input.Destination} 入境 签证 护照 许可 官方{siteHint}",
            "weather" => $"{input.Destination} {date} 天气 气候 预报 气象 官方{siteHint}",
            "disaster" => $"{input.Destination} 自然灾害 地震 台风 应急 避难 官方{siteHint}",
            "safety" => $"{input.Destination} 社会治安 旅行安全 领事 提醒 警方 官方{siteHint}",
            _ => $"{input.Destination} 旅行 官方信息{siteHint}"
        };

        return new SearchProfile(query, TopicKeywords[topic], hosts);
    }

    private static int ScoreResult(
        WebResearchItem item,
        string destination,
        string topic,
        SearchProfile profile)
    {
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri))
        {
            return int.MinValue;
        }

        var host = uri.Host.ToLowerInvariant();
        if (LowQualityHosts.Any(blocked => host.EndsWith(blocked, StringComparison.Ordinal)))
        {
            return int.MinValue;
        }

        var searchable = $"{item.Title} {item.Snippet} {host}".ToLowerInvariant();
        var preferredHost = profile.PreferredHosts.Any(
            preferred => host.EndsWith(preferred, StringComparison.Ordinal));
        var topicMatch = profile.Keywords.Any(
            keyword => searchable.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        var destinationMatch = DestinationAliases(destination).Any(
            alias => searchable.Contains(alias, StringComparison.OrdinalIgnoreCase));

        if (IsRiskTopic(topic) && (!topicMatch || (!preferredHost && !item.LooksOfficial)))
        {
            return int.MinValue;
        }

        var score = 0;
        if (preferredHost)
        {
            score += 10;
        }

        if (topicMatch)
        {
            score += 5;
        }

        if (destinationMatch)
        {
            score += 2;
        }

        if (item.LooksOfficial)
        {
            score += 3;
        }

        return score;
    }

    private static int MinimumScore(string topic) => IsRiskTopic(topic) ? 8 : 5;

    private static bool IsRiskTopic(string topic) =>
        topic is "visa" or "weather" or "disaster" or "safety";

    private static IReadOnlyList<string> GetPreferredHosts(string destination, string topic)
    {
        var key = NormalizeDestination(destination);
        if (OfficialHosts.TryGetValue(key, out var topics)
            && topics.TryGetValue(topic, out var hosts))
        {
            return hosts;
        }

        return [];
    }

    private static IReadOnlyList<WebResearchItem> BuildOfficialFallbackResults(
        string destination,
        string topic)
    {
        var key = NormalizeDestination(destination);
        if (OfficialSourceFallbacks.TryGetValue(key, out var topics)
            && topics.TryGetValue(topic, out var results))
        {
            return results;
        }

        return topic switch
        {
            "visa" =>
            [
                OfficialSource(
                    "中国领事服务网",
                    "https://cs.mfa.gov.cn/",
                    $"查询前往{destination}的领事提醒、入境政策核验入口和驻外使领馆信息。"),
                OfficialSource(
                    "国家移民管理局",
                    "https://www.nia.gov.cn/",
                    "核验中国公民出入境证件、办事指南和口岸政策。")
            ],
            "weather" =>
            [
                OfficialSource(
                    "世界气象组织世界天气信息服务",
                    "https://worldweather.wmo.int/",
                    $"查询{destination}官方气象机构提供的天气和气候信息入口。")
            ],
            "disaster" =>
            [
                OfficialSource(
                    "GDACS 全球灾害警报与协调系统",
                    "https://www.gdacs.org/",
                    $"核验{destination}近期地震、热带气旋、洪水等灾害警报。")
            ],
            "safety" =>
            [
                OfficialSource(
                    "中国领事服务网安全提醒",
                    "https://cs.mfa.gov.cn/gyls/lsgz/lsyj/",
                    $"查询前往{destination}的领事安全提醒和应急联系方式。")
            ],
            _ => []
        };
    }

    private static WebResearchItem OfficialSource(string title, string url, string snippet) =>
        new(title, url, snippet, null, true);

    private static string NormalizeDestination(string destination)
    {
        var value = destination.Trim().ToLowerInvariant();
        if (value.Contains("台湾") || value.Contains("台北") || value.Contains("taiwan"))
        {
            return "taiwan";
        }

        if (value.Contains("香港") || value.Contains("hong kong"))
        {
            return "hongkong";
        }

        if (value.Contains("日本") || value.Contains("东京") || value.Contains("大阪")
            || value.Contains("京都") || value.Contains("japan"))
        {
            return "japan";
        }

        return "other";
    }

    private static IReadOnlyList<string> DestinationAliases(string destination)
    {
        var aliases = new List<string> { destination.Trim().ToLowerInvariant() };
        switch (NormalizeDestination(destination))
        {
            case "taiwan":
                aliases.AddRange(["台湾", "taiwan", "台北"]);
                break;
            case "hongkong":
                aliases.AddRange(["香港", "hong kong", "hongkong"]);
                break;
            case "japan":
                aliases.AddRange(["日本", "japan", "东京", "tokyo", "大阪", "osaka", "京都", "kyoto"]);
                break;
        }

        return aliases.Where(value => value.Length > 0).Distinct().ToList();
    }

    private static string Clean(string? value) =>
        System.Net.WebUtility.HtmlDecode(value ?? string.Empty).Trim();

    private static string? NormalizeDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    private static bool LooksOfficial(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host.EndsWith(".gov")
               || host.Contains(".gov.")
               || host.EndsWith(".go.jp")
               || host.EndsWith(".gov.cn")
               || host.EndsWith(".gov.tw")
               || host.EndsWith(".gov.hk")
               || host.Contains("embassy")
               || host.Contains("mofa")
               || host.Contains("immigration")
               || host.Contains("weather")
               || host.Contains("tourism")
               || host.EndsWith("japan.travel")
               || host.EndsWith("discoverhongkong.com")
               || host.EndsWith("taiwan.net.tw");
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TopicKeywords =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["itinerary"] = ["景点", "景點", "观光", "觀光", "旅游", "旅遊", "attraction", "tourism"],
            ["transport"] = ["航班", "班次", "时刻", "時刻", "机场", "機場", "flight", "train", "schedule"],
            ["food"] = ["美食", "料理", "餐厅", "餐廳", "food", "cuisine", "restaurant"],
            ["visa"] = ["签证", "簽證", "入境", "移民", "护照", "護照", "visa", "immigration", "entry"],
            ["weather"] = ["天气", "天氣", "气象", "氣象", "预报", "預報", "weather", "forecast", "climate"],
            ["disaster"] = ["灾害", "災害", "地震", "台风", "颱風", "应急", "應急", "避难", "避難", "disaster", "earthquake", "typhoon"],
            ["safety"] = ["治安", "安全", "领事", "領事", "警方", "警察", "safety", "security", "advisory"]
        };

    private static readonly IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, IReadOnlyList<string>>> OfficialHosts =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
        {
            ["taiwan"] = new Dictionary<string, IReadOnlyList<string>>
            {
                ["itinerary"] = ["taiwan.net.tw"],
                ["food"] = ["taiwan.net.tw"],
                ["visa"] = ["immigration.gov.tw", "boca.gov.tw"],
                ["weather"] = ["cwa.gov.tw"],
                ["disaster"] = ["nfa.gov.tw", "cwa.gov.tw"],
                ["safety"] = ["boca.gov.tw", "npa.gov.tw"]
            },
            ["hongkong"] = new Dictionary<string, IReadOnlyList<string>>
            {
                ["itinerary"] = ["discoverhongkong.com"],
                ["food"] = ["discoverhongkong.com"],
                ["visa"] = ["immd.gov.hk"],
                ["weather"] = ["hko.gov.hk"],
                ["disaster"] = ["hko.gov.hk", "gov.hk"],
                ["safety"] = ["police.gov.hk", "sb.gov.hk"]
            },
            ["japan"] = new Dictionary<string, IReadOnlyList<string>>
            {
                ["itinerary"] = ["japan.travel"],
                ["food"] = ["japan.travel"],
                ["visa"] = ["mofa.go.jp", "cn.emb-japan.go.jp"],
                ["weather"] = ["jma.go.jp"],
                ["disaster"] = ["jma.go.jp"],
                ["safety"] = ["anzen.mofa.go.jp", "npa.go.jp"]
            }
        };

    private static readonly IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, IReadOnlyList<WebResearchItem>>> OfficialSourceFallbacks =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WebResearchItem>>>
        {
            ["taiwan"] = new Dictionary<string, IReadOnlyList<WebResearchItem>>
            {
                ["itinerary"] =
                [
                    OfficialSource(
                        "台湾观光资讯网",
                        "https://www.taiwan.net.tw/",
                        "官方景点、节庆、交通与旅游服务资讯入口。")
                ],
                ["food"] =
                [
                    OfficialSource(
                        "台湾观光资讯网：美食",
                        "https://www.taiwan.net.tw/m1.aspx?sNo=0000072",
                        "官方地方美食与餐饮主题资讯入口。")
                ],
                ["visa"] =
                [
                    OfficialSource(
                        "台湾移民事务主管部门",
                        "https://www.immigration.gov.tw/",
                        "核验入出境、停留、居留和线上申请等最新规定。"),
                    OfficialSource(
                        "台湾领事事务主管部门",
                        "https://www.boca.gov.tw/",
                        "核验签证、护照及入境相关公告。")
                ],
                ["weather"] =
                [
                    OfficialSource(
                        "台湾中央气象署",
                        "https://www.cwa.gov.tw/V8/C/",
                        "查询天气预报、台风、地震与气候资讯。")
                ],
                ["disaster"] =
                [
                    OfficialSource(
                        "台湾消防署",
                        "https://www.nfa.gov.tw/cht/index.php",
                        "查询防灾资讯、灾害应变和避难指引。"),
                    OfficialSource(
                        "台湾中央气象署灾害资讯",
                        "https://www.cwa.gov.tw/V8/C/",
                        "查询台风、豪雨、地震等气象与地质警报。")
                ],
                ["safety"] =
                [
                    OfficialSource(
                        "台湾警务主管部门",
                        "https://www.npa.gov.tw/",
                        "查询治安资讯、警政服务与紧急联络入口。"),
                    OfficialSource(
                        "中国领事服务网安全提醒",
                        "https://cs.mfa.gov.cn/gyls/lsgz/lsyj/",
                        "核验中国公民出行安全提醒和领事保护信息。")
                ]
            },
            ["hongkong"] = new Dictionary<string, IReadOnlyList<WebResearchItem>>
            {
                ["itinerary"] =
                [
                    OfficialSource(
                        "香港旅游发展局",
                        "https://www.discoverhongkong.com/",
                        "官方景点、活动、餐饮和行程资讯入口。")
                ],
                ["food"] =
                [
                    OfficialSource(
                        "香港旅游发展局：餐饮",
                        "https://www.discoverhongkong.com/eng/explore/dining.html",
                        "官方餐饮与本地美食资讯入口。")
                ],
                ["visa"] =
                [
                    OfficialSource(
                        "香港入境事务处",
                        "https://www.immd.gov.hk/",
                        "核验访港、过境、签证和入境安排。")
                ],
                ["weather"] =
                [
                    OfficialSource(
                        "香港天文台",
                        "https://www.hko.gov.hk/",
                        "查询天气预报、热带气旋和气候资料。")
                ],
                ["disaster"] =
                [
                    OfficialSource(
                        "香港天文台警告及信号",
                        "https://www.hko.gov.hk/",
                        "查询热带气旋、暴雨、雷暴等警告。")
                ],
                ["safety"] =
                [
                    OfficialSource(
                        "香港警务处",
                        "https://www.police.gov.hk/",
                        "查询警务资讯、防骗提示和紧急联络方式。")
                ]
            },
            ["japan"] = new Dictionary<string, IReadOnlyList<WebResearchItem>>
            {
                ["itinerary"] =
                [
                    OfficialSource(
                        "日本国家旅游局",
                        "https://www.japan.travel/",
                        "官方景点、交通、季节活动和行程资讯入口。")
                ],
                ["food"] =
                [
                    OfficialSource(
                        "日本国家旅游局：饮食",
                        "https://www.japan.travel/en/things-to-do/eat-and-drink/",
                        "官方日本料理与地方饮食文化资讯入口。")
                ],
                ["visa"] =
                [
                    OfficialSource(
                        "日本外务省签证信息",
                        "https://www.mofa.go.jp/j_info/visit/visa/index.html",
                        "核验赴日签证类型、申请条件和最新公告。")
                ],
                ["weather"] =
                [
                    OfficialSource(
                        "日本气象厅",
                        "https://www.jma.go.jp/jma/indexe.html",
                        "查询天气预报、台风、地震和火山资讯。")
                ],
                ["disaster"] =
                [
                    OfficialSource(
                        "日本气象厅防灾信息",
                        "https://www.jma.go.jp/bosai/",
                        "查询地震、海啸、台风、暴雨等防灾警报。")
                ],
                ["safety"] =
                [
                    OfficialSource(
                        "日本警察厅",
                        "https://www.npa.go.jp/english/",
                        "查询警务、安全与面向外国访客的防范资讯。"),
                    OfficialSource(
                        "中国领事服务网安全提醒",
                        "https://cs.mfa.gov.cn/gyls/lsgz/lsyj/",
                        "核验中国公民赴日安全提醒和领事保护信息。")
                ]
            }
        };

    private static readonly IReadOnlyList<string> LowQualityHosts =
    [
        "wikipedia.org",
        "geopoliticaleconomy.com"
    ];

    private sealed record Input(
        string Departure,
        string Destination,
        string StartDate,
        int Days,
        string Topics);

    private sealed record SearchProfile(
        string Query,
        IReadOnlyList<string> Keywords,
        IReadOnlyList<string> PreferredHosts);
}
