# Growth Inspector

线上巡检框架（Story：TPG-2690）。本地运行 .NET 10 Blazor（Interactive Server）应用，用三层模型
（运维手册 / 检查单 / 检查项）+ DeepSeek agent + MCP 工具执行可配置的检查单。

> v1 已完成（TPG-2717 收口）：三层模型、通用 / 专用知识注入、检查单内串行 + 检查单间并行的后台队列、
> agent 取证判定、finding 去重与规则版本、Lark mock 通道、每日日报、管理 MCP server、
> 执行历史 / 日报页面（异常列表并入总览「需要处理」，详情见 `/findings/{id}`）。
> 检查项已从「结构化断言」改为**自然语言描述**——标题 + 一段正文（查什么 / 什么算健康 /
> 异常先看哪）+ 严重级别；判定由执行 agent 依正文取证后自行给出，框架不再按阈值复核。
> 知识资产直接取自 `tradepass.growth` 仓库的真实运维手册；检查项正文不点名工具、指标或 SQL；
> mock MCP server 与双判链路已删除，执行侧只登记真实、只读的 Grafana MCP。
> LLM API key 改由 `/settings` 的「模型接入」配置（DataProtection 加密落库，不再读环境变量）；
> 真实 Lark webhook 仍按 v1 口径不接入，Lark 链路以 mock 验收。

## 核心模型

- **Runbook（运维手册）**：纯 Markdown 的长期记忆，`Kind = 通用 | 专用`。通用手册项目级、自动注入该项目下所有检查单；
  专用手册默认不注入，可被检索工具命中、被检查单显式引用后全文注入；引用不跨 Project；
- **Checklist（检查单）**：一次可调度执行的单元，归属 Project，执行时选定 Environment，一张单一个频率；
- **ChecklistItem（检查项）**：最小可执行单元，1:N 从属于检查单，只有 `Title`、`Body`、`Severity`。
  `Body` 是一段自然语言：查什么、什么算健康、异常先看哪；不绑定工具、取值路径、比较符或阈值。

## 判定模型

agent 通过 MCP 工具多轮取证后输出结构化 claim（`Ok` / `Abnormal` / `InsufficientEvidence`）：

- `Ok` / `Abnormal` → 落同名结果；按检查项级别触发 finding 与 Lark；
- `InsufficientEvidence` → 落「待证」，不建 finding、不发 Lark，只进日报与页面；
- 执行出错（LLM / MCP / claim 解析失败）→ 落 `Error`，同样不建 finding；
- 规则外发现只进 `observations[]`，日报「观察」段展示。

finding 按 `(Project, Environment, Checklist, ChecklistItem)` 去重；同键最多一条 Open（SQLite 部分唯一索引兜底）。
恢复时关闭并各发一次通知，不再有「判据变更」关闭路径。

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
  GrowthInspector.Tests/           单元 / 集成测试
tools/
  GrowthInspector.Seeder/          把 seed/ 下的知识资产幂等导入 SQLite
seed/growth/                       真实手册、检查单、Grafana MCP 登记
docs/inspection-knowledge.md       手册 → 检查单 / 检查项的映射说明
```

## 设计系统（全站已迁移）

- `/styleguide`：C 留白冻结稿（丝绸灰 / 玄色，烟墨 / 月白），含色板、字体、五态、表格、集群节点、证据与空态。
- 全站统一 `DesignLayout`（项目/环境上下文、主题切换、`总览 / 执行 / 报告 / 体系 / 设置`）与
  `wwwroot/css/design-system.css`（token 挂 `:root`，页面底纹 + `paper-raised` 内容纸）；旧 `MainLayout` / `app.css` 页面样式已删除。
- 结果页：`/` 总览（集群结论 → 服务/依赖网格 → 需要处理（未恢复按严重级分层 + 最近已恢复折叠段）→ 最近执行）、
  `/findings/{id}`（异常详情：现状与证据 → 时间线）、`/findings` 重定向回总览「需要处理」、
  `/runs` + `/runs/{id}`（运行头 + 判定 meter → 检查项折叠 → 待证观察 → 工具调用 → 观察 → 快照 → 通知/摘要）、`/reports`（日报 + 发送留痕）。
- 配置页：`/systems`（体系落地页：知识资产 / 检查单 / MCP / 项目环境）→ `/runbooks`、`/checklists`（含编辑页）、`/settings`。
- 状态只经 `StatusPresenter`（五态 + Running；`Succeeded = 完成`，`Error / 证据不足 = 待证`，朱红只给 Critical）；
  跨页文案收敛到 `DisplayText`，执行判定分布收敛到 `RunVerdictBuilder`。
- 设计规范与页面模式：`docs/visual-style/style-spec.md`、`docs/visual-style/page-patterns.md`；静态样张：`docs/visual-style/index.html`。

## 运行

前置：.NET SDK 10.0.4xx。

```bash
dotnet build GrowthInspector.slnx
dotnet test tests/GrowthInspector.Tests
dotnet run --project src/GrowthInspector.Web        # http://localhost:5273
```

首次启动自动创建 SQLite 库：`src/GrowthInspector.Web/data/inspector.db`（`EnsureCreated`，不做迁移；
数据模型变更后删库重建）。

导入 Growth 知识资产（6 份手册、4 张检查单、30 个检查项、Grafana MCP 登记）：

```bash
dotnet run --project tools/GrowthInspector.Seeder -- --db /tmp/inspector.db --seed seed/growth
# 然后以该库启动应用
Inspector__SqlitePath=/tmp/inspector.db dotnet run --project src/GrowthInspector.Web
```

Grafana MCP 以 stdio 子进程登记，命令指向本机只读包装脚本（`/home/muomu/.inspector/bin/mcp-grafana.sh`），
环境变量与 token 只注入该脚本，不进数据库与 Git。

环境变量（token 与开关都不入 Git）：

```bash
export INSPECTOR_MCP_TOKEN="..."            # 管理 MCP 的 bearer token（缺失时管理端点 401）
export INSPECTOR_MCP_CONFIG_WRITE=true      # 可选：开启 config-write 档（默认只读）
export INSPECTOR_MCP_ADMIN_WRITE=true       # 可选：开启 admin-write 档（默认只读）
```

LLM API key 不走环境变量：启动后在 `/settings` 的「§ 04 模型接入」填写 key、BaseUrl、模型并保存。
key 用 ASP.NET Core DataProtection 加密后写入 SQLite，页面只回显末四位；保存后**对新任务立即生效**，
运行中的检查单继续用旧 key 跑完。密钥环在 `src/GrowthInspector.Web/data/protection-keys/`，
换机器或删掉密钥环会让已存的 key 解不开（按「未配置」处理，重新填一次即可）。
未配置 key 时执行项落 `Error` 并明确「未完成 LLM 判定」，不伪造结论，日报摘要走模板兜底。

管理 MCP endpoint：`http://127.0.0.1:5273/mcp`（Streamable HTTP，stateless，仅 loopback；
无 token 401、非法 Origin/Host 403；只读档 22 个工具，两档写开关全开 43 个工具）。

## 配置

`src/GrowthInspector.Web/appsettings.json`：

- `Inspector`：审计留存天数、工具结果截断、检查单并发上限、调度 tick、每小时 / 每天触发时刻、检索条数、时区；
- `DeepSeek`：BaseUrl 与模型（设置页未保存过时的**出厂预填值**）、thinking 开关（执行关闭、摘要开启）、超时；
- `ManagementMcp`：端点路径、绑定地址、token / 写开关的环境变量名、允许的 Origin 列表。

## 验收

自动化：

```bash
dotnet build GrowthInspector.slnx                 # 0 warning / 0 error
dotnet test tests/GrowthInspector.Tests           # 全部通过
```

- `ChecklistItemAgentExecutorTests`：claim 解析、三态 → `CheckResultStatus`、超时与失败口径；
- `ManagementMcpTests` / `ManagementMcpGuardTests`：鉴权 401、Origin/Host 403、只读档工具过滤、写档工具、端到端调用；
- `SchedulingTests` / `OrchestrationTests`：频率扫描、互斥 / Skipped、启动清理、串行落库、单项失败继续、并发；
- `FindingNotifierTests` / `DailyReportTests`：去重、恢复、InsufficientEvidence 不建 finding、mock 发送、日报汇总与 10:00 触发；
- `LlmSettingsTests` / `LlmChatClientTests`：key 加密落库与掩码回显、清除只清 key、三字段必填、试连失败分类、
  按 scope 解析配置快照（新任务新 key、在跑任务旧 key）；
- `ClusterOverviewBuilderTests` / `StatusPresenter` 相关：快照缺失、部分执行、严重级缺失等容易产生假健康的边界。

手工冒烟：

1. 空库启动 → 自动建表，`/`、`/runbooks`、`/checklists`、`/runs`、`/findings`、`/reports`、`/settings`、`/styleguide` 均 200；
2. seed 导入后手动触发 / 定时触发 → 后台队列执行、快照含通用手册与引用专用手册、每项立即落库；
3. 管理 MCP：无 token 401、非法 Origin 403、带 token 可 initialize / tools/list；只读档无写工具；
   写档可 `project_create` / `checklist_create` / `checklist_item_create` / `checklist_trigger`；
4. 凭据 write-only：`project_update` 写入 webhook 后 `project_get` 只返回 `****1234`；
5. 归档：`project_update { isArchived: true }` 后默认列表隐藏，恢复后重新可见；
6. 跨 Project 引用：`checklist_runbook_add` 引其他 Project 的手册被拒绝；
7. 日报：到点生成日报并写 `LarkNotification(DailyReport, IsMock=true)` 与 `AiSummary(DailyReport)`，日报页可见；
8. 模型接入：`/settings` 填 key / BaseUrl / 模型 → 「测试连接」按表单值真发一次最小请求（15s 超时，失败按 401 /
   网络 / 模型名分类提示）→ 保存后回显 `****abcd`，「清除密钥」只清 key、保留地址与模型并显示「密钥缺失」。

## 已知边界

- 执行侧只接真实、只读的 Grafana MCP：Prometheus / Loki / Tempo / Grafana API 能回答的判据可取证；
  MySQL / StarRocks 表级判据没有 SQL 通道，会落「待证」，这是当前口径接受的结果；
- 检查项正文按约定不点名数据源，agent 需要从项目上下文与手册里自己找线索；
- DeepSeek key 存在设置页（DataProtection 加密落库）：未配置时执行项落 `Error` 并明确「未完成 LLM 判定」；
  加密只挡「单独拷走 .db」与误提交，拿到整个 `data/` 目录（含密钥环）仍可解；
- 一个 Model 通吃执行与摘要，不能再给摘要单独指定模型（`ThinkingForSummary` 仍可单独开）；
- Lark 全 mock：只写 `LarkNotification`，不发起真实请求；
- 日报「同日只发一次」是进程内记账，重启后当天可能重发一次；
- 管理写 last-write-wins（无 `Revision` / `expected_revision`），无 `ManagementAudit`、软删 / 回收站（v2）；
- 归档是软过滤，不级联：归档 Project 后其环境 / 检查单仍可单独编辑；
- 管理 MCP 工具返回 `{ ok, data | error }` 文本，未启用 structuredContent；
- `EnsureCreated` 无迁移，模型变更后删库重建：本次新增 `LlmSettings` 表，老库不会自动建表
  （`/settings` 会提示加载失败），升级需删库重建；本机旧库已按 `data/inspector.db.bak-<时间戳>` 备份。

## 约定

- 提交信息：首行 `type(scope): 概述 (issue 号)`，空行后写正文；不写 AI 署名。
- 分支：特性分支命名含 issue id（如 `feat/TPG-2703-mcp-client`），完成后合入 `master`。
- 验收证据：本地可复现命令 + 页面 / 日志证据 + commit hash。
- 框架不内置任何检查；真实数据源一律通过登记 MCP server 接入。
