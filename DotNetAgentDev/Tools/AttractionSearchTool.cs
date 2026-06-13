using DotNetAgentDev.Infrastructure;
using DotNetAgentDev.Models;

namespace DotNetAgentDev.Tools;

public sealed class AttractionSearchTool(TourismCatalog catalog) : IAgentTool
{
    public ToolDefinition Definition { get; } = new(
        "attraction_search",
        "根据目的地与偏好查询景点、美食、商圈和休闲候选点。",
        ToolSupport.Schema("""
                           {
                             "type": "object",
                             "properties": {
                               "destination": { "type": "string", "description": "目的地国家或城市" },
                               "preferences": { "type": "string", "description": "用户偏好" },
                               "maxResults": { "type": "integer", "minimum": 1, "maximum": 20 }
                             },
                             "required": ["destination", "preferences", "maxResults"],
                             "additionalProperties": false
                           }
                           """));

    public async Task<ToolExecutionResult> ExecuteAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var input = ToolSupport.Parse<Input>(arguments);
        var destinations = await catalog.FindDestinationsAsync(input.Destination);
        cancellationToken.ThrowIfCancellationRequested();

        var preferenceTerms = input.Preferences
            .Split(['、', ',', '，', '/', ' '], StringSplitOptions.RemoveEmptyEntries);
        var candidates = destinations
            .SelectMany(destination => destination.Attractions.Select(attraction =>
            {
                var preferenceScore = attraction.Tags.Count(tag =>
                    preferenceTerms.Any(term =>
                        tag.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || term.Contains(tag, StringComparison.OrdinalIgnoreCase)));
                var score = 7.5 + preferenceScore * 0.7 + (attraction.TicketPrice == 0 ? 0.2 : 0);
                return new AttractionCandidate(
                    destination.City,
                    attraction.Name,
                    attraction.Category,
                    attraction.Area,
                    attraction.Description,
                    attraction.TicketPrice,
                    attraction.DurationMinutes,
                    attraction.Tags,
                    Math.Min(10, score));
            }))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.City)
            .Take(Math.Clamp(input.MaxResults, 1, 20))
            .ToList();

        if (candidates.Count == 0)
        {
            candidates = CreateNamedFallback(
                input.Destination,
                preferenceTerms,
                input.MaxResults);
        }

        return ToolSupport.Success(new
        {
            source = destinations.Count > 0
                ? "local-tourism-knowledge-base"
                : HasNamedFallback(input.Destination)
                    ? "curated-named-destination-fallback"
                    : "verification-required-fallback",
            disclaimer = "门票为课程演示估算值，请在出行前复核官方信息。",
            candidates
        });
    }

    private static List<AttractionCandidate> CreateNamedFallback(
        string destination,
        IReadOnlyList<string> preferenceTerms,
        int maxResults)
    {
        var key = NormalizeDestination(destination);
        if (NamedFallbacks.TryGetValue(key, out var candidates))
        {
            return candidates
                .Select(candidate =>
                {
                    var preferenceScore = candidate.Tags.Count(tag =>
                        preferenceTerms.Any(term =>
                            tag.Contains(term, StringComparison.OrdinalIgnoreCase)
                            || term.Contains(tag, StringComparison.OrdinalIgnoreCase)));
                    return candidate with
                    {
                        Score = Math.Min(10, candidate.Score + preferenceScore * 0.5)
                    };
                })
                .OrderByDescending(candidate => candidate.Score)
                .Take(Math.Clamp(maxResults, 1, candidates.Count))
                .ToList();
        }

        return
        [
            new AttractionCandidate(
                destination,
                $"{destination}具体景点需联网确认",
                "待核验",
                "待核验",
                "本地知识库没有该目的地的真实景点清单，请根据联网官方旅游资料补充具体名称后再安排。",
                0,
                120,
                ["需联网确认"],
                1)
        ];
    }

    private static bool HasNamedFallback(string destination) =>
        NamedFallbacks.ContainsKey(NormalizeDestination(destination));

    private static string NormalizeDestination(string destination)
    {
        var value = destination.Trim().ToLowerInvariant();
        if (value.Contains("香港") || value.Contains("hong kong") || value.Contains("hongkong"))
        {
            return "hongkong";
        }

        if (value.Contains("台湾") || value.Contains("台北") || value.Contains("taiwan")
            || value.Contains("taipei"))
        {
            return "taiwan";
        }

        if (value.Contains("越南") || value.Contains("vietnam"))
        {
            return "vietnam";
        }

        return value;
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<AttractionCandidate>> NamedFallbacks =
        new Dictionary<string, IReadOnlyList<AttractionCandidate>>
        {
            ["hongkong"] =
            [
                Candidate("香港", "K11 MUSEA", "艺术商场", "尖沙咀", "参观艺术装置、设计空间与维港沿岸商业地标。", 0, 120, ["艺术", "购物", "建筑"], 9.2),
                Candidate("香港", "尖沙咀海滨花园与星光大道", "海滨", "尖沙咀", "沿维多利亚港散步，观赏香港岛天际线与电影主题展陈。", 0, 120, ["夜景", "摄影", "城市漫步"], 9.1),
                Candidate("香港", "中环街市与中环历史街区", "街区", "中环", "串联中环街市、皇后大道中及周边历史建筑。", 0, 150, ["人文", "建筑", "城市漫步"], 9.0),
                Candidate("香港", "中环至半山自动扶梯", "城市体验", "中环", "由中环步行体验半山扶梯，并探索苏豪与荷李活道。", 0, 90, ["城市漫步", "街区"], 8.9),
                Candidate("香港", "太平山顶与凌霄阁", "城市景观", "山顶", "从山顶俯瞰维多利亚港，适合傍晚至夜间游览。", 108, 150, ["夜景", "摄影"], 9.0),
                Candidate("香港", "M+博物馆", "博物馆", "西九文化区", "参观视觉文化、设计、建筑与当代艺术展览。", 120, 180, ["艺术", "人文", "室内"], 8.9),
                Candidate("香港", "香港故宫文化博物馆", "博物馆", "西九文化区", "参观中国艺术文化专题展，可与M+及海滨长廊联游。", 80, 180, ["人文", "博物馆"], 8.8),
                Candidate("香港", "大馆", "历史建筑", "中环", "参观旧中区警署建筑群、当代艺术展览与公共空间。", 0, 150, ["人文", "建筑", "艺术"], 8.8),
                Candidate("香港", "文武庙与荷李活道", "人文街区", "上环", "参观传统庙宇并探索古董街、壁画与上环街区。", 0, 120, ["人文", "城市漫步"], 8.7),
                Candidate("香港", "天星小轮维港航线", "城市体验", "尖沙咀/中环", "搭乘天星小轮横渡维港，连接尖沙咀和中环行程。", 10, 45, ["交通体验", "摄影"], 8.7),
                Candidate("香港", "庙街夜市", "夜市", "油麻地", "体验夜市摊档与街头饮食，适合安排在晚餐后。", 0, 120, ["美食", "夜市"], 8.6),
                Candidate("香港", "旺角花园街与女人街", "街区", "旺角", "体验高密度商业街区、市场与本地生活。", 0, 120, ["购物", "市场", "城市漫步"], 8.5),
                Candidate("香港", "南莲园池与志莲净苑", "园林", "钻石山", "游览中式园林与佛寺建筑，节奏相对安静。", 0, 120, ["自然", "建筑", "轻松"], 8.5),
                Candidate("香港", "昂坪360与天坛大佛", "人文景观", "大屿山", "乘缆车前往昂坪，参观天坛大佛与宝莲禅寺。", 270, 300, ["自然", "人文", "摄影"], 8.8)
            ],
            ["taiwan"] =
            [
                Candidate("台北", "台北101观景台", "城市景观", "信义区", "登观景台俯瞰台北城市景观，并游览信义商圈。", 135, 150, ["夜景", "摄影", "地标"], 9.3),
                Candidate("台北", "台北故宫博物院", "博物馆", "士林区", "参观翠玉白菜、肉形石等中国艺术与文物收藏。", 80, 180, ["人文", "博物馆", "室内"], 9.3),
                Candidate("台北", "中正纪念堂", "历史建筑", "中正区", "参观纪念堂、自由广场与国家两厅院建筑群。", 0, 120, ["人文", "建筑", "摄影"], 9.1),
                Candidate("台北", "龙山寺", "宗教建筑", "万华区", "参观台北代表性传统寺庙，并可串联剥皮寮历史街区。", 0, 90, ["人文", "建筑"], 8.9),
                Candidate("台北", "西门町", "商业街区", "万华区", "体验步行街、潮流商店、电影文化与街头小吃。", 0, 150, ["购物", "美食", "城市漫步"], 8.9),
                Candidate("台北", "迪化街与大稻埕", "历史街区", "大同区", "游览老街建筑、南北货商店与大稻埕码头。", 0, 150, ["人文", "美食", "城市漫步"], 8.8),
                Candidate("台北", "象山亲山步道", "自然", "信义区", "短程登山并从经典机位拍摄台北101，建议傍晚前往。", 0, 120, ["自然", "摄影", "夜景"], 8.9),
                Candidate("台北", "华山1914文化创意产业园区", "文创园区", "中正区", "参观展览、文创商店和由旧酒厂改造的历史空间。", 0, 120, ["艺术", "文创", "建筑"], 8.7),
                Candidate("台北", "松山文创园区", "文创园区", "信义区", "游览旧烟厂建筑、设计展与文创空间，可与台北101联游。", 0, 120, ["艺术", "文创", "建筑"], 8.7),
                Candidate("台北", "士林夜市", "夜市", "士林区", "品尝蚵仔煎、豪大鸡排等夜市小吃。", 0, 150, ["美食", "夜市"], 8.8),
                Candidate("台北", "饶河街观光夜市", "夜市", "松山区", "品尝胡椒饼等小吃，并参观附近松山慈祐宫。", 0, 120, ["美食", "夜市"], 8.7),
                Candidate("台北", "北投温泉博物馆与地热谷", "自然人文", "北投区", "了解温泉历史并游览北投公园、地热谷。", 0, 180, ["自然", "人文", "轻松"], 8.7),
                Candidate("台北", "淡水老街与渔人码头", "滨水街区", "淡水区", "游览淡水老街，傍晚前往渔人码头欣赏夕阳。", 0, 240, ["美食", "摄影", "城市漫步"], 8.8),
                Candidate("台北", "猫空缆车与茶园", "自然体验", "文山区", "乘缆车上山，体验茶园景观与台北盆地视野。", 55, 240, ["自然", "茶文化", "摄影"], 8.7),
                Candidate("台北", "台北市立美术馆", "美术馆", "中山区", "参观现代与当代艺术展览，可串联花博公园。", 30, 150, ["艺术", "室内"], 8.5)
            ],
            ["vietnam"] =
            [
                Candidate("河内", "还剑湖与玉山祠", "城市人文", "还剑湖", "环湖步行并参观玉山祠，适合作为认识河内老城的起点。", 15, 120, ["人文", "城市漫步", "摄影"], 9.2),
                Candidate("河内", "河内老城区三十六行街", "历史街区", "老城区", "探索传统街巷、法式建筑、咖啡馆与街头饮食。", 0, 180, ["人文", "美食", "城市漫步"], 9.2),
                Candidate("河内", "文庙国子监", "历史建筑", "栋多郡", "参观越南代表性的儒学建筑与古代教育遗址。", 30, 120, ["人文", "建筑", "历史"], 9.0),
                Candidate("河内", "胡志明陵与巴亭广场", "历史地标", "巴亭郡", "参观巴亭广场、一柱寺及周边国家历史建筑。", 0, 150, ["历史", "建筑", "人文"], 8.9),
                Candidate("河内", "越南民族学博物馆", "博物馆", "纸桥郡", "了解越南各民族生活、建筑与传统文化。", 40, 150, ["博物馆", "人文", "室内"], 8.8),
                Candidate("河内", "升龙皇城", "世界遗产", "巴亭郡", "参观河内千年都城遗址与考古展区。", 30, 150, ["世界遗产", "历史", "建筑"], 8.8),
                Candidate("岘港", "美溪海滩", "海滨", "山茶郡", "沿海滩散步或休闲，建议清晨或傍晚避开强烈日晒。", 0, 150, ["自然", "海滨", "轻松"], 9.0),
                Candidate("岘港", "山茶半岛与灵应寺", "自然人文", "山茶半岛", "从山海观景路线参观灵应寺，并留意天气与山路交通。", 0, 210, ["自然", "人文", "摄影"], 8.9),
                Candidate("岘港", "五行山", "自然人文", "五行山区", "游览石灰岩洞穴、寺庙和观景点，需穿适合步行的鞋。", 40, 150, ["自然", "人文", "徒步"], 8.8),
                Candidate("岘港", "会安古城", "世界遗产", "会安", "游览古宅、会馆、来远桥和河畔街区，适合傍晚至夜间。", 120, 300, ["世界遗产", "夜景", "人文"], 9.3),
                Candidate("岘港", "岘港龙桥与韩江河畔", "城市景观", "海州郡", "沿韩江散步并观赏龙桥夜景，周末可核验喷火喷水时间。", 0, 120, ["夜景", "城市漫步", "摄影"], 8.7),
                Candidate("胡志明市", "战争遗迹博物馆", "博物馆", "第三郡", "通过历史照片与展品了解越南战争及其社会影响。", 40, 150, ["历史", "博物馆", "室内"], 9.1),
                Candidate("胡志明市", "西贡中央邮局与红教堂周边", "历史建筑", "第一郡", "参观殖民时期建筑，并步行串联书街和第一郡中心。", 0, 120, ["建筑", "历史", "城市漫步"], 9.0),
                Candidate("胡志明市", "统一宫", "历史地标", "第一郡", "参观越南近现代史重要建筑及保存完整的内部空间。", 40, 120, ["历史", "建筑", "人文"], 8.9),
                Candidate("胡志明市", "滨城市场", "市场", "第一郡", "体验本地市场、越南小吃与手工艺品，购物前注意询价。", 0, 120, ["美食", "市场", "购物"], 8.8),
                Candidate("胡志明市", "阮惠步行街与西贡河畔", "城市街区", "第一郡", "傍晚沿步行街和河畔游览，感受市中心夜间氛围。", 0, 120, ["夜景", "城市漫步"], 8.7)
            ]
        };

    private static AttractionCandidate Candidate(
        string city,
        string name,
        string category,
        string area,
        string description,
        decimal ticketPrice,
        int durationMinutes,
        IReadOnlyList<string> tags,
        double score) =>
        new(city, name, category, area, description, ticketPrice, durationMinutes, tags, score);

    private sealed record Input(string Destination, string Preferences, int MaxResults);
}
