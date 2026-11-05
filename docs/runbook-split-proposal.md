# Seed 手册拆分稿（B 项，待拍板后实施）

> 背景：执行链路已改为「整轮一个会话」，手册在每轮开场的共享前缀里注入一次（不再每检查项注入）。
> 本稿把手册在**内容层**拆小，进一步缩短前缀、减少无关注入；长内容靠 `read_runbook(section)` 渐进披露。
> 本文档只是拆分方案，未改动任何 seed 文件。

## 现状与注入量

| 手册 | 体积 | 注入面（每轮） | 频率 |
|---|---|---|---|
| growth-overview（通用） | 6.0 KB | **所有检查单**（4 张全注入） | Hourly×3 + Daily×1 |
| data-warehouse | 28.4 KB | 数仓巡检 | Daily |
| warehouse-full-import | 12.2 KB | 数仓巡检 | Daily |
| workflow-user-event-consumer-dlq | 8.4 KB | 消费链巡检 | Hourly |
| warehouse-job-rerun | 6.1 KB | 数仓巡检 | Daily |
| gatekeeper-consent-hooks | 4.9 KB | 触达巡检 | Hourly |

问题点：

1. **通用手册是全局放大器**：6 KB × 4 检查单 × 每小时，且它是前缀的一部分——即使全部命中缓存，也按缓存价逐轮付费。
2. **data-warehouse 28 KB 单篇过大**：数仓巡检每轮开场就背 ~16K tokens，其中大部分（建模规范、维度设计）与「分区为空 / 枚举漂移 / 环比异常」三个检查项无关。
3. 引用粒度太粗：检查单只能整篇引用，无法只带「本次巡检要用的章节」。

## 拆分原则

1. 单篇（Specialized）目标 **≤ 3 KB**；通用手册目标 **≤ 2 KB**。
2. 按检查项的取证需求拆主题：一篇手册回答一类问题（一个告警怎么判、怎么排）。
3. 判定要用的口径（什么算健康、看哪个指标）留在正文；长篇背景、规范、历史放章节，agent 需要时 `read_runbook(section)` 取。
4. 标题保持「系统能 + 问题域」格式，让 `search_runbooks` 的中文子串命中稳定。

## 拆分明细

### 通用：growth-overview.md → growth-topology.md（≈1.5 KB）

只保留：服务拓扑一页纸（域 → 服务 → 关键依赖）、「去哪本手册查什么」的索引表。
移除：各域详细说明（拆到对应专用手册开头）、数据流细节（并入数仓/消费链手册）。
**通用手册是索引，不是百科**——它每轮都注入，贵。

### data-warehouse.md（28.4 KB）→ 4 篇

| 新篇 | 主题（对应检查项） | 预估 |
|---|---|---|
| warehouse-partition-health | 分区产出与空分区判定：健康口径、查哪张表、DWD 昨日分区为空 | 2 KB |
| warehouse-quality-drift | 枚举漂移与日量环比：口径、对比方法、正常波动范围 | 2.5 KB |
| warehouse-modeling-spec | 建模/维度设计规范（背景知识，按需披露，不再默认引用） | 8 KB（不引用） |
| warehouse-ops-notes | 依赖连通、脚本位置、历史事故（并入 job-rerun 或保留备用） | 4 KB（不引用） |

数仓巡检的引用改为：`warehouse-partition-health` + `warehouse-quality-drift` + 既有 `warehouse-job-rerun`。
注入量 46 KB → ~13 KB。

### warehouse-full-import.md（12.2 KB）→ 1 篇瘦身 + 章节

只在上线首日有用：正文压到「全量导入 checklist + 失败重做入口」（≈2 KB），其余（分批参数、校验 SQL、回滚细节）全部下沉为章节。**默认不再被巡检引用**（属一次性运维，按需 search）。

### workflow-user-event-consumer-dlq.md（8.4 KB）→ 2 篇

| 新篇 | 主题 | 预估 |
|---|---|---|
| workflow-consumer-lag | 位点滞后与订阅缺主题：健康口径、消费组指标、静默丢事件判定 | 2.5 KB |
| workflow-dlq-playbook | 死信到达与 UMP 分片死信处置：重放步骤、发件滞后排查 | 3 KB |

消费链巡检引用两篇，注入量 8.4 KB → ~5.5 KB，且每篇与检查项一一对应。

### gatekeeper-consent-hooks.md（4.9 KB）→ 保持 1 篇，正文瘦身

判定口径（失败率阈值、熔断窗口）前置；排查步骤下沉章节。目标 ≤ 3 KB。

### warehouse-job-rerun.md（6.1 KB）→ 保持 1 篇

已经单一主题；把「作业参数表」下沉章节即可。目标 ≤ 3 KB。

## 拆分后的注入量对比

| 检查单 | 现在（通用+引用） | 拆分后 |
|---|---|---|
| 运行健康巡检（Hourly, 11 项） | 6 KB | 1.5 KB |
| 触达巡检（Hourly） | 11 KB | 4.5 KB |
| 消费链巡检（Hourly） | 14 KB | 7 KB |
| 数仓巡检（Daily） | 46 KB | 14.5 KB |

## 验收

1. `wc -c` 全部引用手册达标（单篇 ≤ 3 KB，通用 ≤ 2 KB）。
2. 重新 seed 后跑一轮各检查单，快照 SnapshotJson 里的手册段字节数下降到上表量级。
3. 检查项判定口径无损：每张检查单的检查项正文里提到的「健康口径」在新手册正文中都能找到（search 命中）。
