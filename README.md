# Growth Inspector

线上巡检框架（Story：TPG-2690）。本地运行 .NET 10 Blazor（Interactive Server）应用，用三层模型
（运维手册 / 检查单 / 检查项）+ DeepSeek agent + MCP 工具执行可配置的检查单。

> 当前处于 **v1 实施中**：共享数据模型、页面骨架与各层接线已就位；MCP 接入、DeepSeek 调用、
> 后台队列、双判复核、finding 告警、日报、管理 MCP 等按子任务逐步接入（见「当前实现状态」）。

## 核心模型

- **Runbook（运维手册）**：纯 Markdown 的长期记忆，`Kind = 通用 | 专用`。通用手册项目级、自动注入该项目下所有检查单；
  专用手册默认不注入，可被检索工具命中、被检查单显式引用后全文注入；引用不跨 Project；
- **Checklist（检查单）**：一次可调度执行的单元，归属 Project，执行时选定 Environment，一张单一个频率；
- **ChecklistItem（检查项）**：最小可执行单元，1:N 从属于检查单。判定型检查项声明**结构化断言**
  （工具名 / 参数模板 / 取值路径 / 比较符 / 阈值 / 窗口 / 级别 / `RuleVersion`）；
  无断言的信息型检查项只收集证据、不参与告警。

## 判定模型

agent 通过 MCP 工具多轮取证后输出结构化 claim；框架按检查项的 `ValuePath` 从 `ToolCallLog` 重算一遍：

- 一致 → 落 `Ok` / `Abnormal`，按级别触发 finding 与 Lark；
- 不一致或证据不足 → 落 `Error`，不建 finding、不发 Lark，只进日报；
- 规则外发现只进 `observations[]`，日报「观察」段展示。

finding 按 `(Project, Environment, Checklist, ChecklistItem)` 去重并绑定 `RuleVersion`；规则变更时旧 finding
以「判据变更」关闭，新版本重开。

## 执行模型

- 每张检查单一个 agent 会话，检查项按 `SortOrder` **串行**执行；检查单之间**并行**（无 MAF、无项级 fan-out）；
- 手动与调度统一进后台队列，不阻塞 Blazor circuit；同一 `(检查单, 环境)` 互斥，重入记 `Skipped`；
- 每项结束立即落库；单项失败不中断整轮；应用启动时把遗留 `Running` 置 `Canceled`；
- 执行侧审计（快照 / 结果 / 工具调用 / 摘要）默认留存 90 天，由清理任务定期删除。

## 目录结构

```
src/
  GrowthInspector.Core/            领域模型、抽象接口、配置选项
  GrowthInspector.Data/            EF Core SQLite：DbContext、转换器、建库入口、审计清理
  GrowthInspector.Agents/          DeepSeek 客户端、MCP client、工具基座与执行 agent
  GrowthInspector.Management/      管理 service 与进程内管理 MCP server（Streamable HTTP）
  GrowthInspector.Orchestration/   检查单执行编排与后台队列
  GrowthInspector.Notifications/   Lark 通知（v1 mock 通道）
  GrowthInspector.Web/             Blazor 页面、调度器、日报
tests/
  GrowthInspector.Tests/           单元 / 集成测试
tools/
  GrowthInspector.MockMcpServer/   v1 端到端验收用 mock MCP server
```

## 运行

前置：.NET SDK 10.0.4xx。

```bash
dotnet run --project src/GrowthInspector.Web
```

首次启动自动创建 SQLite 库：`src/GrowthInspector.Web/data/inspector.db`（`EnsureCreated`，不做迁移；
数据模型变更后删库重建）。启动后按左侧导航使用：总览 / 运维手册 / 检查单 / 执行历史 / 异常 / 日报 / 设置。

环境变量（key 与 token 都不入 Git）：

```bash
export DEEPSEEK_API_KEY="sk-..."            # 未配置时应用可启动，模型调用明确报错、不伪造结论
export INSPECTOR_MCP_TOKEN="..."            # 管理 MCP 的 bearer token（缺失时管理端点拒绝请求）
export INSPECTOR_MCP_CONFIG_WRITE=true      # 可选：开启 config-write 档（默认只读）
export INSPECTOR_MCP_ADMIN_WRITE=true       # 可选：开启 admin-write 档（默认只读）
```

管理 MCP endpoint：`http://127.0.0.1:5273/mcp`（Streamable HTTP，仅 loopback；无 token 401、非法 Origin 403）。

## 配置

`src/GrowthInspector.Web/appsettings.json`：

- `Inspector`：审计留存天数、工具结果截断、检查单并发上限、调度 tick、每小时 / 每天触发时刻、agent 轮次与超时、检索条数、时区；
- `DeepSeek`：BaseUrl、执行 / 摘要模型、thinking 开关（执行关闭、摘要开启）、超时；
- `ManagementMcp`：端点路径、绑定地址、token / 写开关的环境变量名、允许的 Origin 列表。

## 当前实现状态

| 能力 | 状态 | 承接子任务 |
|---|---|---|
| 三层数据模型 + SQLite 自动建库 + 结构化断言 | 已落共享模型 | TPG-2690（地基） |
| 手册 / 检查单 / 检查项 / 执行历史 / 异常 / 日报 / 设置页面 | 骨架交互 | TPG-2715 / TPG-2716 |
| MCP client + McpServer 登记 + 工具审计截断 | 待接 | TPG-2703 |
| DeepSeek 客户端（thinking 开关 / token 计量） | 骨架（无 key 降级） | TPG-2704 |
| 调度：后台队列 / 频率 / 互斥 / 启动清理 / 快照 | 骨架心跳 | TPG-2706 |
| 执行编排：检查单内串行 + 检查单间并行（无 MAF） | 骨架（顺序执行） | TPG-2707 |
| agent 工具基座（MCP + 手册检索 / 披露 + 审计） | 待接 | TPG-2708 |
| 执行 agent：整单会话 + 取证 + 结构化 claim + 复核 | 占位执行器 | TPG-2709 |
| 反思 / 摘要 agent | 待接（反思可选） | TPG-2710 / TPG-2711 |
| 管理 MCP server（鉴权 / 写开关 / 凭据 write-only） | 接线点就位 | TPG-2712 |
| finding 去重 / 规则版本 / Lark 告警（mock） | 表结构就位 | TPG-2713 |
| 每日 10:00 日报（mock） | 规则化汇总已接 | TPG-2714 |
| 审计留存清理 | 接线就位 | TPG-2705 |
| 端到端验收（mock + 第二项目不改代码） | 待收口 | TPG-2717 |

## 验证

```bash
dotnet build GrowthInspector.slnx
dotnet test tests/GrowthInspector.Tests
```

页面冒烟：`dotnet run` 后访问 `/`、`/runbooks`、`/checklists`、`/runs`、`/findings`、`/reports`、`/settings`，
均应返回 200，且 `src/GrowthInspector.Web/data/inspector.db` 自动生成。

## 约定

- 提交信息：首行 `type(scope): 概述 (issue 号)`，空行后写正文；不写 AI 署名。
- 分支：特性分支命名含 issue id（如 `feat/TPG-2703-mcp-client`）；子任务可合并回 `master`。
- 验收证据：本地可复现命令 + 页面 / 日志证据 + commit hash（见 TPG-2690 设计变更记录）。
- 框架不内置任何检查；真实数据源一律通过登记 MCP server 接入。
- v1 不包含：部署 / k8s / CI、多用户权限、真实数据源、框架侧护栏、补跑 / 断点续跑、MAF、
  软删 / 回收站 / `Revision` / `ManagementAudit`（后置 v2）。
