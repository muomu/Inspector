# 巡检知识资产：手册 → 检查单 / 检查项映射

本文说明 Growth 巡检知识资产的组成与来源：每张检查单挂哪些手册、每个检查项在查什么、正文从哪里来。
手册正文与检查项本身在 `seed/growth/`，导入工具在 `tools/GrowthInspector.Seeder/`。

## 1. 资产全貌

| 资产 | 数量 | 位置 | 说明 |
|---|---|---|---|
| 手册 | 6 份（通用 1 / 专用 5） | `seed/growth/runbooks/*.md` + `runbooks.json` | 纯 Markdown，承载真实运维知识 |
| 检查单 | 4 张 | `seed/growth/checklists.json` | 运行健康、触达准入与告警口径、消费链（均 Hourly）、数仓（Daily） |
| 检查项 | 30 项 | 同上 | 只有标题、自然语言正文、严重级别 |
| 项目 / 环境 | 1 个项目、2 个环境 | `seed/growth/project.json` | Project `Growth`（Code=`growth`）、`prod`（默认）/ `beta` |
| MCP server | 1 个 | 同上 | 名称 `grafana`，stdio，真实只读 Grafana MCP |

三层关系：手册承载知识 → 检查单引用手册（`ChecklistRunbookRef`）→ 检查项是一段自然语言
（查什么 / 什么算健康 / 异常先看哪）。判定由执行 agent 取证后自行给出，框架不再按阈值复核。

## 2. 来源

- **真实手册原文**（从 `tradepass.growth` 仓库拷贝，逐字节一致，仅一级标题即原题）：
  - `docs/runbooks/gatekeeper-consent-hooks.md` → 触达准入
  - `docs/runbooks/workflow-user-event-consumer-dlq.md` → 消费链
  - `docs/data-warehouse.md` → 数仓
  - `docs/runbooks/warehouse-full-import.md`、`docs/runbooks/warehouse-job-rerun.md` → 数仓
- **通用手册**：`growth-overview.md` 从 `src/backend/SERVICE-MAP.md`、`docs/README.md`、`docs/data-warehouse.md`
  提炼服务拓扑、六域、数据流向与依赖，文末标注来源。
- **外呼三项判据**：tradepass 仓库 runbook 没有写 SIP 线路 / 媒体节点 / 暂停外呼，判据取自真实 Grafana
  告警规则（线路断开超 5 分钟、媒体节点整池不可用超 3 分钟、线路暂停外呼超 15 分钟）。

约定：**检查项正文不点名工具、Prometheus 指标、SQL、表名、消费组名或 topic 名**，只写业务口径；
真实手册原文保留其内部指标名与表名（它们只存在于手册，供 agent 按需查证）。

## 3. 四张检查单

### 3.1 运行健康巡检（Growth）· Hourly · 11 项

引用：无（通用手册《Growth 系统总览：服务拓扑、依赖与数据流向》自动注入）。

| 检查面 | 检查项 |
|---|---|
| 六个业务域（platform / customer / reach / flow / sales / metrics）| 各一项，覆盖域内 svc 与 worker 是否在跑、有无重启或崩溃、对外服务是否可用 |
| 五项依赖（MySQL / Redis / Kafka / StarRocks / Aether）| 各一项，覆盖能否连上、读写是否正常、异常先看哪 |

全部 Critical。

### 3.2 触达准入与告警口径巡检（Growth）· Hourly · 7 项

引用：`触达准入层：外部询问 hook 的监控与告警`。

| 检查面 | 检查项 | 级别 |
|---|---|---|
| 外部询问 hook | 询问失败消息未发出、询问 hook 熔断、询问失败率偏高、准入判定结论异常 | Critical / Warning |
| 外呼 | SIP 线路断开超 5 分钟、媒体节点整池不可用、线路暂停外呼超 15 分钟 | Critical |

### 3.3 消费链巡检（Growth）· Hourly · 6 项

引用：`用户事件消费链失败消息处置（原生 Kafka 消费组 + 死信 topic）`。

| 检查面 | 检查项 | 级别 |
|---|---|---|
| 用户事件消费链 | 原生消费组位点滞后、死信消息到达、订阅缺主题静默丢事件 | Warning / Critical |
| CAP 发件箱 | CAP outbox 待投递积压 | Warning |
| UMP 投递 | UMP 分片死信与发件滞后 | Critical |
| 工作流 | 工作流卡单与任务残留 | Warning |

### 3.4 数仓巡检（Growth）· Daily · 6 项

引用：`数仓设计与建模规范`、`数仓全量导入手册（上线首日）`、`数仓作业重跑`。

| 检查面 | 检查项 | 级别 |
|---|---|---|
| 数据质量 | DWD 昨日分区为空、枚举漂移、日量环比异常、DWS 聚合产出滞后 | Critical / Warning |
| 一致性 | 归属对账差异 | Critical |
| 作业 | 作业失败与重跑未对账 | Critical |

## 4. 检查项写法与验收

- 每项一段 3–4 句：第一句查什么；第二句什么算健康；第三句异常先看哪；可加一句分级/处置；
- 正文只写业务口径，不出现工具名、指标名、SQL、表名、消费组名、topic 名；
- 严重级别（Critical / Warning）决定 finding 的告警级别；
- 验收标准：**人读得懂、能按文本去查**；agent 执行时允许因取不到数而落「待证」（InsufficientEvidence）。

## 5. seed 结构与复现

```
seed/growth/
  project.json      项目 / 环境 / Grafana MCP
  runbooks.json     手册清单（正文在 runbooks/*.md）
  checklists.json   4 张检查单 + 30 个检查项
  runbooks/         1 份通用总览 + 5 份真实手册原文
```

```bash
# 导入（仓库根目录执行；目标库不存在会自动建库建表）
dotnet run --project tools/GrowthInspector.Seeder -- --db /tmp/inspector-seed-test.db --seed seed/growth

# 核对（重复执行不会产生重复行）
sqlite3 /tmp/inspector-seed-test.db "select count(*) from Projects; select count(*) from Runbooks; select count(*) from ChecklistItems;"
```

本次落地核对结果：

| 表 | 行数 |
|---|---|
| Projects | 1 |
| Environments | 2 |
| McpServers | 1（grafana） |
| Runbooks | 6（General 1 / Specialized 5） |
| Checklists | 4 |
| ChecklistItems | 30 |
| ChecklistRunbookRefs | 5 |

## 6. 决定与已知边界

- **去 mock**：旧 seed 的 mock MCP server、结构化断言字段、双判与 `RuleVersion` 全部删除，旧库删库重建；
- **执行通道**：只接真实只读 Grafana MCP（Prometheus / Loki / Tempo / Grafana API）；MySQL / StarRocks 表级判据没有 SQL 通道，会落「待证」；
- **不编数字**：源文档没有给出 DWS 产出的分钟阈值，检查项正文写「最新成功结果落在最近一个产出周期内」，不沿用旧的 mock 阈值；
- **外呼三项**：仓库手册未覆盖，判据取真实 Grafana 告警规则，正文仍按业务口径描述；
- **手册保留原文**：真实手册里的指标名、表名、消费组名保持原样，检查项正文不引用它们。
