# Growth Inspector

线上巡检框架（Story：TPG-2690）。本地运行 .NET 10 Blazor（Interactive Server）应用，用三层模型
（运维手册 / 检查单 / 检查项）+ DeepSeek agent + MCP 工具执行可配置的检查单。

> v1 已完成（TPG-2717 收口）：三层模型、通用 / 专用知识注入、检查单内串行 + 检查单间并行的后台队列、
> agent 取证 + 框架复核双判、finding 去重与规则版本、Lark mock 通道、每日日报、管理 MCP server、
> 执行历史 / 异常 / 日报页面。真实 DeepSeek key 与真实 Lark webhook 按 v1 口径不接入，全链路以 mock 验收。

## 核心模型

- **Runbook（运维手册）**：纯 Markdown 的长期记忆，`Kind = 通用 | 专用`。通用手册项目级、自动注入该项目下所有检查单；
  专用手册默认不注入，可被检索工具命中、被检查单显式引用后全文注入；引用不跨 Project；
- **Checklist（检查单）**：一次可调度执行的单元，归属 Project，执行时选定 Environment，一张单一个频率；
- **ChecklistItem（检查项）**：最小可执行单元，1:N 从属于检查单。判定型检查项声明**结构化断言**
  （工具名 / 参数模板 / 取值路径 / 比较符 / 阈值 / 窗口 / 级别 / `RuleVersion`）；
  无断言的信息型检查项只收集证据、不参与告警。断言描述「健康」状态：满足 = Ok，不满足 = Abnormal。

## 判定模型

agent 通过 MCP 工具多轮取证后输出结构化 claim；框架按检查项的 `ValuePath` 从 `ToolCallLog` 重算一遍：

- 一致 → 落 `Ok` / `Abnormal`，按级别触发 finding 与 Lark；
- 不一致或证据不足 → 落 `Error`（`FrameworkVerdict = Mismatch`），不建 finding、不发 Lark，只进日报；
- 规则外发现只进 `observations[]`，日报「观察」段展示。

finding 按 `(Project, Environment, Checklist, ChecklistItem)` 去重并绑定 `RuleVersion`；规则变更时旧 finding
以「判据变更」关闭，新版本重开（同去重键最多一条 Open，SQLite 部分唯一索引兜底）。

## 执行模型

- 每张检查单一个 agent 会话，检查项按 `SortOrder` **串行**执行；检查单之间**并行**（无 MAF、无项级 fan-out）；
- 手动与调度统一进后台队列，不阻塞 Blazor circuit；同一 `(检查单, 环境)` 互斥，重入记 `Skipped`；
- 每项结束立即落库；单项失败不中断整轮；应用启动时把遗留 `Running` / `Pending` 置 `Canceled`（不做补跑）；
- 执行侧审计（快照 / 结果 / 工具调用 / 摘要 / 通知留痕）默认留存 90 天，由清理任务定期删除；`Finding` 不清。

## 目录结构

```
src/
  GrowthInspector.Core/            领域模型、抽象接口、配置选项
  GrowthInspector.Data/            EF Core SQLite：DbContext、转换器、建库入口、审计清理
  GrowthInspector.Agents/          DeepSeek 客户端、MCP client、工具基座、执行 / 反思 agent
  GrowthInspector.Management/      管理 service 与进程内管理 MCP server（Streamable HTTP）
  GrowthInspector.Orchestration/   执行编排、后台队列、频率调度扫描、摘要 agent
  GrowthInspector.Notifications/   finding 状态机、Lark mock 通道、每日日报
  GrowthInspector.Web/             Blazor 页面、调度宿主、日报页面服务
tests/
  GrowthInspector.Tests/           单元 / 集成 / 端到端测试
tools/
  GrowthInspector.MockMcpServer/   stdio mock MCP server（QueryMetric / QuerySql / GetServiceHealth）
  GrowthInspector.Seeder/          把 seed/ 下的知识资产幂等导入 SQLite
seed/growth/                       通用 / 专用手册、检查单、mock MCP 登记
docs/inspection-knowledge.md       A/B/C 判据 → 手册 → 检查单 / 检查项的映射说明
```

## 运行

前置：.NET SDK 10.0.4xx。

```bash
dotnet build GrowthInspector.slnx
dotnet test tests/GrowthInspector.Tests
dotnet run --project src/GrowthInspector.Web        # http://localhost:5273
```

首次启动自动创建 SQLite 库：`src/GrowthInspector.Web/data/inspector.db`（`EnsureCreated`，不做迁移；
数据模型变更后删库重建）。

导入 Growth 知识资产（6 份手册、3 张检查单、31 个检查项、mock MCP 登记）：

```bash
dotnet run --project tools/GrowthInspector.Seeder -- --db /tmp/inspector.db --seed seed/growth
# 然后以该库启动应用
Inspector__SqlitePath=/tmp/inspector.db dotnet run --project src/GrowthInspector.Web
```

环境变量（key 与 token 都不入 Git）：

```bash
export DEEPSEEK_API_KEY="sk-..."            # 可选；未配置时执行 agent 明确报错、不伪造结论，摘要有模板兜底
export INSPECTOR_MCP_TOKEN="..."            # 管理 MCP 的 bearer token（缺失时管理端点 401）
export INSPECTOR_MCP_CONFIG_WRITE=true      # 可选：开启 config-write 档（默认只读）
export INSPECTOR_MCP_ADMIN_WRITE=true       # 可选：开启 admin-write 档（默认只读）
```

管理 MCP endpoint：`http://127.0.0.1:5273/mcp`（Streamable HTTP，stateless，仅 loopback；
无 token 401、非法 Origin/Host 403；只读档 22 个工具，两档写开关全开 43 个工具）。

## 配置

`src/GrowthInspector.Web/appsettings.json`：

- `Inspector`：审计留存天数、工具结果截断、检查单并发上限、调度 tick、每小时 / 每天触发时刻、agent 轮次与超时、检索条数、时区；
- `DeepSeek`：BaseUrl、执行 / 摘要模型、thinking 开关（执行关闭、摘要开启）、超时；
- `ManagementMcp`：端点路径、绑定地址、token / 写开关的环境变量名、允许的 Origin 列表。

## 验收（TPG-2717）

自动化：

```bash
dotnet build GrowthInspector.slnx                 # 0 warning / 0 error
dotnet test tests/GrowthInspector.Tests           # 136 passed
```

- `EndToEndMockTests`：真实 stdio mock MCP server + 脚本化 LLM，跑通
  快照 → 多轮取证 → agent claim → 框架复核双判（Ok / Abnormal / InsufficientEvidence）→
  finding → Lark mock 留痕 → AI 摘要；并验证第二 Project 只加数据、零改代码复跑；
- `ManagementMcpTests` / `ManagementMcpGuardTests`：鉴权 401、Origin/Host 403、只读档工具过滤、写档工具、端到端调用；
- `SchedulingTests` / `OrchestrationTests`：频率扫描、互斥 / Skipped、启动清理、串行落库、单项失败继续、并发；
- `FindingNotifierTests` / `DailyReportTests`：去重、规则版本迁移、恢复、mock 发送、日报汇总与 10:00 触发。

手工冒烟（已按此验收）：

1. 空库启动 → 自动建 13 张表，`/`、`/runbooks`、`/checklists`、`/runs`、`/findings`、`/reports`、`/settings` 均 200；
2. seed 导入后手动触发 / 定时触发 → 后台队列执行、快照含通用手册与引用专用手册、每项立即落库；
3. 管理 MCP：无 token 401、非法 Origin 403、带 token 可 initialize / tools/list；只读档无写工具；
   写档可 `project_create` / `checklist_create` / `checklist_item_create` / `checklist_trigger`；
4. 凭据 write-only：`project_update` 写入 webhook 后 `project_get` 只返回 `****1234`；
5. 归档：`project_update { isArchived: true }` 后默认列表隐藏，恢复后重新可见；
6. 跨 Project 引用：`checklist_runbook_add` 引其他 Project 的手册被拒绝；
7. 日报：到点生成日报并写 `LarkNotification(DailyReport, IsMock=true)` 与 `AiSummary(DailyReport)`，日报页可见。

## 已知边界（v1 口径）

- 真实 DeepSeek key 未接入：无 key 时执行项落 `Error` 并明确「未完成 LLM 判定」；端到端验收用脚本化 LLM 替身；
- Lark 全 mock：只写 `LarkNotification`，不发起真实请求；
- 日报「同日只发一次」是进程内记账，重启后当天可能重发一次；
- 复核不一致一律按 `Error` 处理、不告警（会吞真异常，触发条件与后续策略见 TPG-2690 评论）；
- 管理写 last-write-wins（无 `Revision` / `expected_revision`），无 `ManagementAudit`、软删 / 回收站（v2）；
- 归档是软过滤，不级联：归档 Project 后其环境 / 检查单仍可单独编辑；
- 管理 MCP 工具返回 `{ ok, data | error }` 文本，未启用 structuredContent；
- `EnsureCreated` 无迁移，模型变更后删库重建。

## 约定

- 提交信息：首行 `type(scope): 概述 (issue 号)`，空行后写正文；不写 AI 署名。
- 分支：特性分支命名含 issue id（如 `feat/TPG-2703-mcp-client`），完成后合入 `master`。
- 验收证据：本地可复现命令 + 页面 / 日志证据 + commit hash。
- 框架不内置任何检查；真实数据源一律通过登记 MCP server 接入。
