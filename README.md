# 旅序：多 Agent 旅游规划系统

基于 `.NET 10`、ASP.NET Core 与 DeepSeek API 的课程期末项目。用户输入出发地、目的地、日期、天数、人数、预算与偏好后，系统由主控 Agent 协调五个专业 Agent，生成每日行程、住宿、交通、预算和风险提醒。

系统支持两种运行模式：

- **DeepSeek 在线模式**：配置 API Key 后，使用模型完成工具选择与多轮函数调用。
- **离线演示模式**：未配置 API Key 或 API 暂时失败时，使用确定性决策引擎执行同一套 Agent Loop 与工具，保证课堂演示稳定。

## 功能亮点

- 手写 `Thought → Action → Observation → Final Answer` ReAct 循环。
- 主控 Agent + 行程、酒店、交通、预算、风险 5 个专业 Agent。
- 8 个带 JSON Schema 的自定义工具。
- 当前会话工作记忆 + JSON 文件长期偏好记忆。
- 本地旅游知识库，覆盖日本东京/京都/大阪、杭州、成都和新加坡。
- DeepSeek HTTP 客户端、自动工具调用、错误降级与日志。
- Web 端可视化 Agent 执行轨迹，不展示或伪造模型私有思维链。
- xUnit 单元测试与响应式界面。

## 快速开始

### 环境要求

- [.NET SDK 10](https://dotnet.microsoft.com/)
- 可选：DeepSeek API Key

### 1. 还原与测试

```bash
dotnet restore
dotnet test
```

### 2. 运行

不配置 API Key 时会自动使用离线演示模式：

```bash
dotnet run --project DotNetAgentDev/DotNetAgentDev.csproj
```

终端会显示访问地址，通常为 `http://localhost:5000` 或 `https://localhost:5001`。

### 3. 启用 DeepSeek

macOS / Linux：

```bash
export DEEPSEEK_API_KEY="sk-your-api-key"
dotnet run --project DotNetAgentDev/DotNetAgentDev.csproj
```

PowerShell：

```powershell
$env:DEEPSEEK_API_KEY="sk-your-api-key"
dotnet run --project DotNetAgentDev/DotNetAgentDev.csproj
```

模型、地址、超时与最大输出长度可在
`DotNetAgentDev/appsettings.json` 中修改。项目默认使用 `deepseek-v4-flash`。

> DeepSeek 官方文档：[首次 API 调用](https://api-docs.deepseek.com/)、
> [Tool Calls](https://api-docs.deepseek.com/guides/function_calling)、
> [Chat Completion API](https://api-docs.deepseek.com/api/create-chat-completion)。

## 使用方式

1. 打开首页，填写旅行需求，或点击“填入示例”。
2. 点击“启动 Agent 团队”。
3. 在方案总览、每日行程、预算交通、风险提醒、Agent 轨迹间切换。
4. 点击右上角“历史方案”，查看长期记忆保存的计划与偏好。

推荐演示输入：

```text
出发地：上海
目的地：日本
天数：7
人数：1
预算：12000
节奏：轻松
偏好：美食、城市漫步、人文、夜景
备注：不想太赶，希望酒店靠近公共交通，并留出自由活动时间
```

## 系统架构

```mermaid
flowchart LR
    U["Web 用户"] --> API["ASP.NET Core API"]
    API --> C["Travel Coordinator Agent"]
    C --> I["行程 Agent"]
    C --> H["酒店 Agent"]
    C --> T["交通 Agent"]
    C --> R["风险 Agent"]
    I --> L["Agent Loop"]
    H --> L
    T --> L
    R --> L
    C --> B["预算 Agent"]
    B --> L
    L --> DS["DeepSeek / 离线决策引擎"]
    L --> REG["Tool Registry"]
    REG --> KB["本地旅游知识库"]
    REG --> MEM["JSON 长期记忆"]
    C --> MEM
```

详细设计见 [架构设计文档](docs/架构设计.md)。

## Agent 与工具

| Agent | 主要职责 | 工具 |
| --- | --- | --- |
| 主控 Agent | 拆解任务、并行调度、冲突处理、结果整合 | 调度专业 Agent |
| 行程规划 Agent | 候选点、区域排序、每日节奏 | `preference_memory`、`attraction_search`、`route_sort` |
| 酒店 Agent | 住宿位置、价格与便利度 | `hotel_search` |
| 交通 Agent | 大交通、跨城、市内交通 | `transport_estimate` |
| 风险 Agent | 天气、签证、安全、强度 | `weather_lookup`、`risk_check` |
| 预算 Agent | 汇总费用、检查超支、提出优化 | `budget_calculator` |

## 关键目录

```text
DotNetAgentDev/
├── DotNetAgentDev/
│   ├── Agents/          # ReAct 循环、专业 Agent、主控 Agent
│   ├── Data/            # 本地旅游知识库
│   ├── Infrastructure/  # JSON 记忆与数据读取
│   ├── Llm/             # DeepSeek 与离线模型客户端
│   ├── Models/          # 领域模型和 API 契约
│   ├── Services/        # 应用服务
│   ├── Tools/           # 8 个自定义工具与注册表
│   └── wwwroot/         # Web 界面
├── DotNetAgentDev.Tests/
└── docs/
```

运行时计划保存在 `DotNetAgentDev/App_Data/`，该目录已加入 `.gitignore`，不会提交用户历史数据。

## 安全与数据说明

- API Key 只能通过环境变量或本地配置注入，不得提交到 Git。
- 旅游价格、季节天气和政策内容为课程演示数据，不是实时结论。
- DeepSeek 不可用时自动降级，不会让整次规划直接失败。
- 所有输入先做边界校验；工具异常会转换为 Observation，由 Agent 继续处理。

## 课程交付文档

- [架构设计文档](docs/架构设计.md)
- [反思报告](docs/反思报告.md)
- [答辩与演示脚本](docs/答辩演示.md)

## 测试

```bash
dotnet test --collect:"XPlat Code Coverage"
```

测试覆盖预算边界、输入校验、离线 Agent 工具选择顺序、本地知识库查询与未知目的地回退。

## 已知边界

- 本地知识库不是实时 API，真实出行前需要二次核验。
- 在线模式输出受模型稳定性与账户额度影响，因此保留离线模式。
- 当前长期记忆为单机 JSON 文件，适合课程项目；生产环境应换成数据库并增加用户认证。

