# 数仓全量导入手册（上线首日）

> **这份文档答的是**：历史事件怎么灌进新数仓、灌完怎么确认数是对的。
>
> **不答**：新链的表怎么建、口径怎么定（那是 [`warehouse-model.md`](../../src/backend/domains/customer/warehouse/modeling/warehouse-model.md)）；
> 也不答业务系统那侧的桥接程序怎么写（那不在本仓）。
>
> ⚠ **动手前先读 ①**：`dim_cst_order` 只有批层，导入之后要靠第 4 步先种一轮；日常它自己会跑（2026-08-27 起）。

## 事实源

| 问题 | 去哪看 |
|---|---|
| 表长什么样、列的口径 | [`warehouse-model.md`](../../src/backend/domains/customer/warehouse/modeling/warehouse-model.md) |
| 引擎的已知界与缺口 | [`OPEN-ISSUES.md`](../../src/backend/frameworks/materialize/OPEN-ISSUES.md) |
| 页面字段的口径真源 | [`page-fields.md`](../data-dictionary/page-fields.md) |
| 导入完怎么验 | `scripts/verify-data-platform.sh` |

---

## ① `dim_cst_order` 的容量：已解，但换来一条操作要求

**导入走的是与新订单完全相同的链路**（业务系统的桥接程序 → OpenAPI 网关 → Kafka → 物化 worker），
没有 bulk load 旁路。于是 3 亿开平仓持仓的**每一个**都会在状态层里占一份中间状态。

实测（2026-08-18，Redis 8.6，`used_memory`）：

| 表 | 主体是什么 | 每主体 | 规模 | 峰值 |
|---|---|---|---|---|
| `dim_cst_profile` | 1 个客户 | 1 706 B | 5 M | 7.9 GiB |
| `dim_cst_stage` | 客户 × 档位 | 1 421 B / 客户（按平均 3 档） | 5 M 客户 | 6.6 GiB |
| `dim_cst_account` | 1 个交易账户 | 635 B | 1 M | 0.6 GiB |
| **`dim_cst_order`** | 1 个持仓 | 936 B | 300 M | **0**（已降级成只有批层） |

前三张加起来约 **15 GiB**。`dim_cst_order` 曾是硬阻塞——**主体数随累计成交量涨、与客户数无关**，
四轮状态层优化把它从 0.48 TiB 砍到 0.26 TiB，**但那是砍每主体的字节、砍不动主体数**。
最终的解法是让它整张退出流层（见下），而不是继续省字节。

**2026-08-18 已解**：`dim_cst_order` 的全部聚合作业降级成**只有批层**
（`OrderDim.BatchOnlyReason`）。这张表的状态层用量因此**归零**，导入峰值只剩前三张的约 15 GiB。

代价是一条**操作要求**，不是技术债：这 17 列**不会随事件到达出行**——灌完事件之后表是空的，
必须显式跑一轮重算（第 4 步）把历史种进去。

> ✅ **2026-08-27：日常那一半已经解了。** 三条作业现在各自挂两条时间线——
> `Every(5 分钟, window: 1 小时)` 保新鲜 + `ReconcileWeekly(周日 03:00, 回看 720 天)` 全量兜底，
> 且批层只扫窗口覆盖的那几个分区（beta 实测 `partitions=3/31`）。
> **第 4 步仍然要跑**：滑动窗口只有 1 小时，导入进来的历史事件 `occurred_at` 是旧的，
> 落在窗口之外；不跑就得等到第一个周日的兜底那一轮。

> **同日第二轮：那 15 条作业归成了 4 条**（15 列不变，是**作业**合并了）。
> 2026-08-27 佣金按腿拆列之后是 **3 条**：开仓 9 列 / 平仓 7 列各一条 `GROUP BY`，
> 撤单 1 列仍是单列作业。第 4 步要跑的因此是 3 个作业名——**旧名字会报「作业不存在」**，
> 包括已经删掉的 `cst.order.fees`。
>
> ⚠ **佣金与隔夜利息现在是四列**：`open_commission` / `open_swap` / `close_commission` /
> `close_swap`，**合计在读端点相加**。口径没变（仍是开 + 平），落法变了——
> 跨腿求和过不了窗口增量的判据，理由见 `OrderDim.WindowedScanReason`。
> **导入后这四列加起来会比旧链的单列大**（旧链只累加平仓腿），那是修正不是回归。

> 顺带修掉两列的乱序问题：`close_trade_id` / `close_price` 原是「末个到达」，
> 只有批层之后走 `MAX_BY(值, occurred_at)`，与到达顺序无关。

---

## ② 顺序：桥接程序不保证，而 25 列靠它

`Agg.First` / `Agg.Last` 在**流层**是「到达序」，排序表达式只进批层
（见 `warehouse-model.md` 里 G4 那一节）。全仓 25 列靠它：

- `dim_cst_profile`：`kyc_status` / `owner_id` / `team_id` / 最近触达三列 / 意向四列 …
- `dim_cst_stage`：`owner_id_at` / `team_id_at` / `evidence_*`
- `dim_cst_order`：开仓六列（`account_id` / `symbol` / `side` / `volume_lots` / `open_trade_id` / `open_price`）
  + 平仓两列（`close_trade_id` / `close_price`）

**乱序导入的后果**：这 25 列在流层会取到「不是最早 / 不是最新」的那一行。数字完全合法、不报错。

**兜底是批层对账**——它从事实表读、用 `MIN_BY` / `MAX_BY` 排序，**与到达序无关**。
`FirstFunc` / `LastFunc` 的排序表达式是构造函数必填参数，49 条声明全部已经传了。

### 🔴 但对账有一条前置条件（OPEN-ISSUES #83）

> **对账必须在摄取排空、且分发器刷完之后跑，不能与摄取并行。**

机制是两个代码事实：`BatchDispatcher` 对攒批表**入队即返回**（级联据此立刻跑、落库要等刷批），
而对账走原始 sink、是**同步** Stream Load。于是一条「对账之前入队、对账之后才落库」的流层写入
会把对账的修正盖回去——目标表是 `PartialUpdate`，后落地的赢，两次写都合法。

并行跑的症状：数在两个答案之间反复，每一次都合法，而下一轮对账又改回来。

---

## ③ 步骤

每一步都有**判据**，判据不满足不要往下走。

### 第 0 步 · 起飞前检查

```bash
# 表结构与声明一致（worker 的 SchemaMode 是 Disabled，dev 库不会自动变）
./scripts/verify-data-platform.sh
```

判据：**377 / 0 / 0**（这是 2026-08-18 的基线；条数会随用例增长，**失败与跳过必须都是 0**）。

再确认三件：

- ① 的容量方案已定并已落地；
- Redis 的 `maxmemory` 按 ① 的选择留足，且 `maxmemory-policy` **不是** `allkeys-*`
  （状态被随机逐出 = 累加列静默从零起算）；
- worker 是 `SingleReplica` 装配（[#76](../../src/backend/frameworks/materialize/OPEN-ISSUES.md)）——
  多副本前必须先换三样，导入期不要顺手加副本。

### 第 1 步 · 桥接程序按客户分区、按事件时序发

Kafka 按客户分区，**同一客户内保序**即可，跨客户不要求。

判据：拿一个客户抽样比对——`dwd_cst_*` 里该客户的行按 `occurred_at` 排序，
与业务系统那侧的原始次序一致。

> 做不到保序也能继续（第 4 步的对账会修那 25 列），但**别跳过第 4 步**。

### 第 2 步 · 灌

按事件族分批灌，不要一次全开。建议次序：注册 / 归属 → 核验 → 出入金 → 成交 → 触达。
理由是后面几族的派生列依赖前面的归属（事件时归属要真回源解析，归属表空会测出偏高的吞吐）。

**耗时**（⚠ 只答方向，答不出倍数，见 [#81](../../src/backend/frameworks/materialize/OPEN-ISSUES.md)）：
实测底座是 `WarehousePerfTests.MixedIngestThroughput` 的 **1 585 ~ 2 598 events/s**（四族混灌、单副本）。
按 6.6 亿条成交 + 1 亿条其它外推是**天**的量级，不是小时。**开始前先用同一条用例在生产参数下量一次**。

判据：Kafka 消费 lag 归零，且

```sql
SELECT COUNT(*) FROM <库>.dwd_cst_trade_di;   -- 与业务系统那侧的行数对齐
```

### 第 3 步 · 等排空 + 刷批

这一步就是 #83 的前置条件，**不能省**。

判据（三条都要）：

1. Kafka 全部相关 topic 的 consumer lag 为 0；
2. 停止桥接程序（不再有新事件进来）；
3. 等过一个攒批窗口（生产默认 `MaxBatchDelay` 15 秒，留够余量）。

> 引擎有 `IMaterializeEngine.FlushAsync`，但**对账路径不调它**，且它只保证「已入队的刷掉」、
> 保证不了「级联还没入队的」。所以这一步靠「停 + 等」，不靠调用它。

### 第 4 步 · 逐租户跑一轮对账

**不必逐租户跑**（2026-08-19 更正，此前本节写的是「必须逐租户」）：聚合语句是
`TenantScope.ByGroupKey`——租户列在 `GROUP BY` 里，一条 SQL 一次算对所有租户，
语句里根本没有租户参数。逐个租户跑一遍就是白扫 N 遍。

**窗口必须覆盖全部历史**（2026-08-27 起这一句成了硬要求）：`cst.order.*` 三条作业声明了
`WindowedScan`，批层**只算窗口内的源行**。给一个盖不住历史的窗口，症状是那些老持仓一行都不出——
不报错。取「最早一条成交明细之前」到「现在」。

要跑的是**那 25 列所属的作业 + order 全部 4 条**（⚠ `cst.order.*` 这一族的名字在源码里是
`$"cst.order.{name}"` 拼出来的，grep 整串找不到，别据此以为不存在）：

| 表 | 作业 |
|---|---|
| `dim_cst_profile` | `cst.profile.kyc_status` · `owner_id` · `team_id` · `last_outreach_*` · `intent_*` · `unreached_reason` · `device_os` |
| `dim_cst_stage` | `cst.stage.owner_id_at` · `team_id_at` · `evidence_table` · `evidence_id` |
| `dim_cst_order` | 🔴 **全部 3 条**（这张表只有批层，不跑历史就出不来）：`cst.order.open` · `cst.order.close` · `cst.order.cancel` |

> **order 那三条的来历**：2026-08-18 从 15 条单列作业归并成 4 条，2026-08-27 又把
> `cst.order.fees`（唯一不带 `execution_type` 谓词的一条）按腿拆进 `open` 与 `close`，剩 3 条。
> `open` 9 列、`close` 7 列都是多列作业（`aggregate-set`）；`cancel` 只有 1 列，仍是单列作业。
> **拿旧名字去跑会报「作业不存在」**——那是好事，比静默跑空强。

判据：每条作业的返回里 `Errors` 为空且 `RowsWritten > 0`。
**`RowsWritten == 0` 不是「没数据」，先怀疑租户没绑上**——那会让租户参数留在未绑定状态、谓词恒假、查回零行，
而日志那句「没有可算的行」读起来像上游今天没数据。

### 第 5 步 · 验

```bash
./scripts/verify-data-platform.sh
```

再加两条只有导入后才验得了的：

- **抽样对账**：取 20 个客户，把画像那 25 列与「按 `occurred_at` 排序从明细现算」逐列比；
- **总量对账**：`dim_cst_order` 行数 = 业务系统的持仓数；`deposit_total_usd` 合计与业务系统对齐。

---

## ④ 中断了怎么办

| 情形 | 处置 |
|---|---|
| 灌到一半 worker 挂 | 直接重启接着灌。摄取是 at-least-once，累加类聚合有幂等键去重 |
| 重启后发现有重复累加 | 去重集 TTL 是 **48 小时**，超龄事件重放会重复累加——**重跑第 4 步的对账**吸收掉（对账不读状态层，天然幂等） |
| 对账跑了一半失败 | 逐条重跑。对账幂等，重算不读状态层 |
| 数不对但不知道哪一步坏的 | 别从头灌。先按第 5 步的抽样对账定位到列，再看那一列的作业 |

⚠ **去重键的线格式在 2026-08-18 改过**（成员压成定长摘要）。升级跨过那次改动时，
旧的去重集成员一个都匹配不上——发布后 48 小时窗口内的重放会重复累加一次，同样由对账吸收。

---

## ⑤ 还没解的

| | 影响 | 去哪看 |
|---|---|---|
| ~~order 的列没有调度、也没有增量重算~~ | **2026-08-27 已解**：三条作业各挂「5 分钟窗口增量 + 周日全量兜底」两条时间线 | [#17](../../src/backend/frameworks/materialize/OPEN-ISSUES.md) |
| 对账与流层的落库次序无约束 | 靠本文第 3 步绕开，引擎侧未修 | [#83](../../src/backend/frameworks/materialize/OPEN-ISSUES.md) |
| 混灌吞吐基准只答方向 | 耗时估不准 | [#81](../../src/backend/frameworks/materialize/OPEN-ISSUES.md) |
| worker 单副本 | 横向扩不了，导入只能单线程吃 | [#76](../../src/backend/frameworks/materialize/OPEN-ISSUES.md) |

## 变更

| 日期 | 变更 |
|---|---|
| 2026-08-18 | 初版。容量数据来自当日实测（四轮状态层优化之后）；第 3 步那条前置条件来自 #83 的实测诊断 |
