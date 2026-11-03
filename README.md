# Growth Inspector

动态 Runbook 线上巡检框架（Story：TPG-2690）。本地运行 .NET 10 Blazor（Interactive Server）应用，
用 MAF Workflow + DeepSeek + MCP 执行可配置的检查单。

> 当前是**代码骨架**：数据模型、页面、编排入口与各层接线已就位；
> MCP 接入、DeepSeek 调用、MAF 编排、finding 去重告警、日报发送等按子任务逐步接入（见「当前实现状态」）。

## 三层模型

- **Runbook（运维手册）**：纯 Markdown 的长期记忆（技术栈、指标语义、告警规则、排查经验），不解析、不承载判定；
- **Checklist（检查单）**：一次可调度执行的单元，归属 Project，执行时选定 Environment，一张单一个频率；
- **ChecklistItem（检查项）**：最小可执行单元，1:N 从属于检查单，声明数据源提示 / 预期 / 阈值 / 级别。

执行上下文 = 引用到的全部手册全文 + 当前检查单全部检查项 + 该检查项的 finding 历史（finding 接入后）。

## 目录结构

```
src/
  GrowthInspector.Core/            领域模型、抽象接口、配置选项
  GrowthInspector.Data/            EF Core SQLite：DbContext、转换器、建库入口、审计清理占位
  GrowthInspector.Agents/          DeepSeek IChatClient、MCP 工具调用与检查项执行器（占位）
  GrowthInspector.Orchestration/   检查单执行编排（骨架：写快照 → 逐项执行 → 落库）
  GrowthInspector.Notifications/   Lark webhook 通知
  GrowthInspector.Web/             Blazor Interactive Server 页面、调度心跳、日报服务
tests/
  GrowthInspector.Tests/           冒烟测试（建库、编排写快照 / 结果）
tools/
  GrowthInspector.MockMcpServer/   v1 端到端验收用 mock MCP server（占位，由 TPG-2703 实现）
```

## 运行

前置：.NET SDK 10.0.4xx。

```bash
dotnet run --project src/GrowthInspector.Web
```

首次启动会自动创建 SQLite 库：`src/GrowthInspector.Web/data/inspector.db`（`EnsureCreated`，不做迁移）。
启动后按左侧导航使用：总览 / 运维手册 / 检查单 / 执行历史 / 异常 / 日报 / 设置。

DeepSeek 密钥只走本地环境变量（不入库、不进 Git）：

```bash
export DEEPSEEK_API_KEY="sk-..."
```

未配置密钥时应用可正常启动，任何模型调用都会明确报错（不伪造结论）。

## 配置

`src/GrowthInspector.Web/appsettings.json`：

```json
{
  "Inspector": {
    "AuditRetentionDays": 90,
    "ToolResultMaxBytes": 8192,
    "ChecklistItemConcurrency": 8,
    "SchedulerTickSeconds": 30
  },
  "DeepSeek": {
    "BaseUrl": "https://api.deepseek.com",
    "Model": "deepseek-flash",
    "SummaryModel": "deepseek-flash"
  }
}
```

## 当前实现状态

| 能力 | 状态 | 承接子任务 |
|---|---|---|
| 数据模型 + SQLite 自动建库 | 已接 | TPG-2690（骨架） |
| 手册 / 检查单 / 检查项 / 执行历史 / 异常 / 日报 / 设置页面 | 已接（骨架交互） | TPG-2715 / TPG-2716 |
| 检查单执行入口（快照 + 结果落库） | 已接（占位执行器） | TPG-2707 / TPG-2709 |
| MCP client（ListTools / 工具调用 / 审计截断） | 占位 | TPG-2703 |
| DeepSeek 客户端 | 已接（无 key 降级） | TPG-2704 |
| MAF Workflow 编排（fan-out / fan-in / 摘要） | 占位 | TPG-2707 |
| 调度（按频率触发） | 心跳占位 | TPG-2706 |
| finding 去重与 Lark 告警 | 表结构就位，逻辑待接 | TPG-2713 |
| 每日 10:00 日报 | 生成与落库已接，Lark 发送待接 | TPG-2714 |
| 审计留存清理 | 占位 | TPG-2705 |
| mock MCP server | 占位 | TPG-2703 |

## 验证

```bash
dotnet build GrowthInspector.slnx
dotnet test tests/GrowthInspector.Tests
```

页面冒烟：`dotnet run` 后访问 `/`、`/runbooks`、`/checklists`、`/runs`、`/findings`、`/reports`、`/settings`，
均应返回 200，且 `src/GrowthInspector.Web/data/inspector.db` 自动生成。

## 约定

- 提交信息：首行 `type(scope): 概述 (issue 号)`，空行后写正文；不写 AI 署名。
- 分支：特性分支命名含 issue id（如 `feat/TPG-2703-mcp-client`）。
- 验收证据：本地可复现命令 + 页面 / 日志证据 + commit hash（见 TPG-2690 设计变更记录）。
- 框架不内置任何检查；真实数据源一律通过登记 MCP server 接入。
