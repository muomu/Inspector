# 巡检知识资产：判据 → 手册 → 检查单 / 检查项映射

本文说明 Growth 巡检知识资产（TPG-2691）的组成与来源：每条判据对应哪份手册、哪张检查单、哪些检查项，检查项的断言从哪里来、怎么落。手册正文与检查项本身在 `seed/growth/`，导入工具在 `tools/GrowthInspector.Seeder/`。

## 1. 资产全貌

| 资产 | 数量 | 位置 | 说明 |
|---|---|---|---|
| 手册 | 6 份（通用 1 / 专用 5） | `seed/growth/runbooks/*.md` + `runbooks.json` | 纯 Markdown，只承载知识 |
| 检查单 | 3 张 | `seed/growth/checklists.json` | 运行健康（Hourly）、告警口径（Hourly）、数仓质量（Daily） |
| 检查项 | 31 项 | 同上 | 判定型 28 项、信息型 3 项 |
| 项目 / 环境 | 1 个项目、2 个环境 | `seed/growth/project.json` | Project `Growth`（Code=`growth`）、`prod`（默认）/ `beta` |
| MCP server | 1 个 | 同上 | 名称 `mock`，stdio，工具名前缀即 `mock__` |

三层关系：手册承载知识 → 检查单引用手册（`ChecklistRunbookRef`）→ 检查项声明结构化断言。判定权在框架，agent 只取证；断言成立 = 健康。

## 2. A / B / C 判据与映射

来源判据只读、不改源仓库，三份来源文档：

- **A. 触达准入层告警口径**：[gatekeeper-consent-hooks.md](../../tradepass.growth/docs/runbooks/gatekeeper-consent-hooks.md)
- **B. 用户事件消费链与失败处置**：[workflow-user-event-consumer-dlq.md](../../tradepass.growth/docs/runbooks/workflow-user-event-consumer-dlq.md)
- **C. 数仓设计与数据质量**：[data-warehouse.md](../../tradepass.growth/docs/data-warehouse.md)

### 2.1 A 判据 → 手册 → 检查单

| 判据 | 手册 | 检查单 | 检查项 |
|---|---|---|---|
| 询问失败行数 / 熔断 / 失败率三条 PromQL | 《触达准入层：外部询问 hook 的监控与告警》+ 通用手册 | 触达准入与告警口径巡检 | 3 项判定型 |
| SIP 线路 / 媒体节点 / 暂停外呼（枚举要求，来源无表达式） | 同上 | 同上 | 3 项信息型 |

告警表达式是**故障条件**，进检查项时取反成健康条件：

| 来源告警 | 检查项断言 |
|---|---|
| `increase(gatekeeper_consent_failed_rows_total[5m]) > 0` | `mock__QueryMetric` → `$.value` `Lte` `0`，Critical，5m |
| `max_over_time(gatekeeper_consent_breaker_open[2m]) == 1` | `mock__QueryMetric` → `$.value` `Lte` `0`，Critical，2m |
| 失败请求速率 / 总请求速率 > 0.2 | `mock__QueryMetric` → `$.value` `Lte` `0.2`，Warning，5m |

### 2.2 B 判据 → 手册 → 检查单

| 判据 | 手册 | 检查单 | 检查项 |
|---|---|---|---|
| 26 个 `.dlq` topic 到达量、`.native` 组 lag、`cap.received` 失败扫描失效口径 | 《用户事件消费链失败消息处置（原生 Kafka 消费组 + 死信 topic）》 | 运行健康巡检 | 死信 topic 到达量（Critical）、Kafka 消费组 lag（Warning） |
| 分区内串行、可恢复故障阻塞重试、位点不动 | 《消息积压与工作流卡单》 | 同上 | 工作流卡单（Warning） |
| CAP 消费失败口径 → outbox 待投递 | 《CAP outbox 积压处置》 | 同上 | CAP outbox 待投递积压（Warning） |

对应断言：

| 检查项 | 工具 | 取值路径 | 断言 | 级别 | 窗口 |
|---|---|---|---|---|---|
| Kafka 原生消费组 lag | `mock__QueryMetric` | `$.value` | `Lt 10000` | Warning | 15m |
| 死信 topic 到达量 | `mock__QueryMetric` | `$.value` | `Lte 0` | Critical | 15m |
| CAP outbox 待投递积压 | `mock__QuerySql` | `$.count` | `Lt 100` | Warning | 15m |
| 工作流卡单 | `mock__QuerySql` | `$.count` | `Lte 0` | Warning | 30m |

### 2.3 C 判据 → 手册 → 检查单

| 判据 | 手册 | 检查单 | 检查项与断言 |
|---|---|---|---|
| 空分区告警（分区 DWD 表每日行数 = 0；不分区表按 `local_date` 数当天行数） | 《数仓空分区、枚举漂移与对账》 | 数仓质量巡检 | DWD 昨日分区是否为空：`mock__QuerySql` `$.rows` `Gt 0`，Critical，24h |
| 枚举字段白名单校验、未知值立即告警（`kyc_status` / `call_result` / `deposit_channel`） | 同上 | 同上 | 3 项：`mock__QueryMetric` `$.value` `Lte 0`，Critical，24h |
| 日量环比 ±30% | 同上 | 同上 | 日量环比：`mock__QueryMetric` `$.value` `Lte 0.3`，Warning，7d |
| 聚合作业产出新鲜度（现网无物化视图，DWS 由物化引擎产出） | 同上 | 同上 | 聚合作业产出滞后：`mock__QueryMetric` `$.value` `Lt 120`（分钟），Warning，1h |
| 归属对账（DWD 宽表 vs 业务库） | 同上 | 同上 | 归属对账：`mock__QuerySql` `$.mismatch_count` `Lte 0`，Critical，24h |

### 2.4 运行面补充（不在 A/B/C 三份来源文档内）

进程心跳、依赖连通、任务残留、准入队列、UMP 分片与发件器来自 TPG-2691 交付说明的枚举，三份来源文档没有逐条覆盖。它们的判据以「进程与依赖是否存活」「积压是否可被处理」为口径，落在《Growth 通用运维手册》与《消息积压与工作流卡单》里；断言按 mock 契约写，接入真实数据源时校准。

| 检查项 | 工具 | 取值路径 | 断言 | 级别 |
|---|---|---|---|---|
| 六进程心跳（management-svc / openapi-svc / growth-worker / ump-worker / materialize-worker / callmedia-svc） | `mock__GetServiceHealth` | `$.status` | `Eq healthy` | Critical |
| 四项依赖连通（MySQL / Redis / Kafka / StarRocks） | `mock__GetServiceHealth` | `$.status` | `Eq up` | Critical |
| 任务卡死残留 | `mock__QueryMetric` | `$.value` | `Lte 0` | Warning |
| 准入队列积压 | `mock__QueryMetric` | `$.value` | `Lt 500` | Warning |
| UMP 分片堆积 | `mock__QuerySql` | `$.count` | `Lte 0` | Warning |
| UMP 发件器滞后 | `mock__QuerySql` | `$.count` | `Lte 0` | Warning |

## 3. 检查单与手册引用

| 检查单 | 频率 | 引用手册 | 检查项 |
|---|---|---|---|
| 运行健康巡检（Growth） | Hourly | 通用 + DLQ + 积压与卡单 + CAP outbox | 18 |
| 触达准入与告警口径巡检（Growth） | Hourly | 通用 + gatekeeper | 6 |
| 数仓质量巡检（Growth） | Daily | 通用 + 数仓 | 7 |

通用手册自动注入所有检查单；专用手册按检查单显式引用注入。引用不跨项目。

## 4. 「5 条 vs 6 条」差异说明

- TPG-2691 正文写 gatekeeper **「5 条 PromQL」**，但同一处的枚举列出了 **6 条**：失败行数、熔断、失败率、SIP 线路、媒体节点、暂停外呼。
- 本仓库**按 6 条落地**，理由：枚举是具体项，按 5 条落会少一条检查、漏掉一个面。落地结果：3 条判定型 + 3 条信息型。
- 但来源文档 `gatekeeper-consent-hooks.md` 的第 3 节只给出了前 3 条的表达式；SIP 线路、媒体节点、暂停外呼在三份来源里都没有告警定义。因此这 3 条**标信息型、不设断言**，不自行编造 PromQL。
- 补齐方式：拿到这三条的告警定义后补断言、补 Severity 与 Window，并把 `RuleVersion` 从 1 升到 2；旧 finding 以「判据变更」关闭。在此之前，这 3 项只收集证据、不参与告警。
- 若确认「5 条」才是准绳，需要明确舍弃哪一条（SIP 线路直接影响能否外呼，建议保留）。本实现按 6 条落，先保证不丢检查面，等口径定稿再收敛。

## 5. 断言字段口径

- 判定型检查项必须给齐：`ToolName`、`ArgumentsTemplate`、`ValuePath`、`Operator`、`Threshold`、`Window`、`Severity`、`RuleVersion=1`；
- 信息型检查项这些字段都不给（`Severity` 也不给），只留 `Title`、`DataSourceHint`、`Expectation`；
- 只给半套（比如有 `ToolName` 没 `Threshold`）在 seeder 校验阶段直接报错，不会进库；
- 取反规则：来源里的故障条件 `> 0` / `== 1` / `> 0.2` 一律转成健康条件 `≤ 0` / `≤ 0` / `≤ 0.2`；「分区行数 = 0 异常」转成「行数 > 0 健康」；
- 阈值里的比例统一用 0–1 小数；时长窗口用 `5m` / `15m` / `30m` / `1h` / `24h` / `7d`。

## 6. seed 结构与复现

```
seed/growth/
  project.json      项目 / 环境 / mock MCP server
  runbooks.json     手册清单（正文在 runbooks/*.md，bodyFile 引用）
  checklists.json   3 张检查单 + 31 个检查项
  runbooks/         6 份纯 Markdown 手册
```

```bash
# 导入（仓库根目录执行；目标库不存在会自动建库建表）
dotnet run --project tools/GrowthInspector.Seeder -- --db /tmp/inspector-seed-test.db --seed seed/growth

# 核对（重复执行不会产生重复行）
sqlite3 /tmp/inspector-seed-test.db "select count(*) from Projects; select count(*) from Runbooks; select count(*) from ChecklistItems;"
sqlite3 /tmp/inspector-seed-test.db "select Kind, count(*) from Runbooks group by Kind;"
sqlite3 /tmp/inspector-seed-test.db "select ToolName, ValuePath, Operator, Threshold, Severity, RuleVersion from ChecklistItems where Title like '%lag%';"
```

本次落地核对结果：

| 表 | 行数 |
|---|---|
| Projects | 1 |
| Environments | 2 |
| McpServers | 1 |
| Runbooks | 6（General 1 / Specialized 5） |
| Checklists | 3 |
| ChecklistItems | 31（判定型 28 / 信息型 3） |
| ChecklistRunbookRefs | 8 |

所有检查项的 `RuleVersion` 都是 1；第二次导入全部走「更新」分支，行数不变。

## 7. mock 工具契约（v1）

检查项只依赖三个 mock 工具，工具名 = `{server 名}__{工具名}`：

| 工具 | 返回形状 | 用它的检查项 |
|---|---|---|
| `mock__GetServiceHealth` | `{"status": "healthy" \| "up" \| "degraded" \| "down"}` | 六进程心跳、四项依赖连通 |
| `mock__QueryMetric` | `{"value": <数字>}` | Kafka lag、DLQ、门槛类积压、任务残留、枚举漂移、日量环比、聚合滞后 |
| `mock__QuerySql` | 聚合列，如 `{"count": 0}`、`{"rows": 0}`、`{"mismatch_count": 0}` | CAP outbox、工作流卡单、UMP 分片与发件器、空分区、归属对账 |

mock server 实现（TPG-2703）必须按上表返回，否则断言复核取不到值，检查项落 `Error`（不建 finding）。改返回形状要同批改受影响的检查项并递增 `RuleVersion`。

## 8. 未决与风险

1. **3 条信息型检查项**（SIP 线路 / 媒体节点 / 暂停外呼）没有来源表达式，缺告警定义，暂时不产生 finding。
2. **mock 契约依赖 TPG-2703**：返回形状若与上表不一致，全部判定型检查项会落 `Error`。本文的契约表是双方对齐的依据。
3. **SQL 里的表名与列名是 v1 mock 口径的示意**：`cap.published`、`wf_instance`、`ump_delivery_shard`、`dwd_cdp_user_login_di`、`dwd_cst_label_wide` / `dim_cst_profile` 按现有文档口径写；`cap.published` 的状态列沿用来源文档的 `StatusName` 写法（MySQL 侧实际列名以部署为准）。接入真实数据源时校准并递增 `RuleVersion`。归属对账的业务库基准（MySQL 客户归属）也是 v1 口径假设，需随真实接入确认。
4. **阈值基线未经真实流量校准**：lag 10000、outbox 100、准入队列 500、聚合滞后 120 分钟都是 v1 占位基线，接入真实流量后按租户规模调整；调阈值走判据变更流程。
5. **Lark 通道是 mock**：v1 不真实发送，「收到通知」不能当处置完成的标志。
6. **服务名以 TPG-2691 交付说明为准**：`management-svc` / `openapi-svc` / `growth-worker` / `ump-worker` / `materialize-worker` / `callmedia-svc` 按任务说明枚举；源仓库的当前服务清单与历史命名有差异，接真实数据源时以 `SERVICE-MAP` 为准再校准一次。
