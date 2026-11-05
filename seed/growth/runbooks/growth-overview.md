# Growth 系统总览：服务拓扑、依赖与数据流向

> 面向巡检的入门手册：先知道系统由哪些服务和依赖组成，再按域与数据链路定位异常。
> 详细的领域职责见各域 README，数仓规范见《数仓设计与建模规范》，事件消费失败处置见《用户事件消费链失败消息处置》。

## 1. 六个域与服务拓扑

Growth 按业务域拆分，每个域一个对外接口进程（svc），需要后台任务的域另有 worker，另有通话与迁移类进程。对外的 Kubernetes Service 名等于 Aether 服务编码，统一为 `tradepass-growth-<域>-svc`。

| 域 | 职责 | 对外的 svc | 后台 / 专用进程 |
|---|---|---|---|
| platform | 平台基础：审计、配置存储、租户设置与变量、数据字典、大模型账号与网关 | platform-svc | — |
| customer | 客户数据：身份识别与事件接入、事件目录、标签分群、画像与客户列表 | customer-svc | customer-worker（分群重建、评分扫描、设备与自定义事件转发） |
| reach | 触达：准入、统一消息投递（UMP）、人工与 AI 外呼 | reach-svc | reach-worker（投递与准入后台）、callmedia-svc（SIP 中继与媒体节点） |
| flow | 编排：流转图、自动化规则、群发计划、新客户入口 | flow-svc | flow-worker（工作流引擎运行时、三条用户事件消费链） |
| sales | 销售：客户归属与分配、团队、跟进任务、销售积分、预筛 | sales-svc | sales-worker（订阅者、成员对账、积分结算） |
| metrics | 指标：数仓计算、报表查询、销售工作台、渠道质量评分 | metrics-svc | 物化 worker（单副本，消费 Kafka 并写数仓） |

镜像与域的对应关系（一个镜像里装多个域的生产工程）：

| 镜像 | 主要承载 |
|---|---|
| platform-svc | platform |
| customer-svc / customer-worker | customer（含少量 platform、reach、sales 工程） |
| reach-svc / reach-worker | reach（含少量 platform、customer、sales 工程） |
| callmedia-svc | 通话媒体，主要为 reach 服务 |
| flow-svc / flow-worker | flow（含少量 platform、customer、reach、sales 工程） |
| sales-svc / sales-worker | sales（含少量 platform、customer、reach、flow 工程） |
| metrics-svc / metrics-worker | metrics（含少量 platform、customer、reach、flow、sales 工程） |
| growth-migrator | 结构迁移 Job |
| dlq-redriver | 死信回灌工具 |

跨域调用只走接口与事件：各域在 `/_internal/remote/{契约名}/{方法}` 下挂内部契约，Aether 不上报这个前缀、网关不对外开放；业务事件经 Kafka 传递。域之间按层次单向依赖，platform 被所有域依赖，customer 被 reach、flow、sales、metrics 读，reach 与 sales 被 flow、metrics 读。

## 2. 数据流向：ODS → DWD → DWS → ADS

```
外部系统 / WebApp
      │  OpenAPI Webhook、主动拉取、业务操作
      ▼
ODS：Kafka（原始事件留档，保留 365 天，支持按消费组重放）
      │
      ▼
DWD：StarRocks 明细与画像（PE.Materialize 流作业消费事件后写入）
      │
      ▼
DWS：StarRocks 聚合汇总（流层即时累加 + 批层定期校准，数仓里没有物化视图）
      │
      ▼
ADS：Redis / MySQL / 服务内存（查询加速、看板、决策缓存）
```

- **ODS**：业务写库与事件发布经 CAP 发件箱（outbox）落 Kafka；Kafka 是原始事件的唯一留档层，也是历史补偿与重放的来源。
- **DWD**：明细事实表按事件追加，客户标签宽表按主键做列级局部更新；两条路都以 StarRocks 为落点。
- **DWS**：聚合作业产出日汇总（`_1d`）与累计汇总（`_td`），流层保新鲜、批层做校准，不允许一张表混放多个统计周期。
- **ADS**：DWS 结果推送到 Redis / MySQL / 服务内存，供页面、报表与决策读取；禁止跳过 DWS 从 DWD 重复加工。

层次调用方向单向（ODS → DWD → DWS → ADS），禁止逆向引用；DWS 每张表只放一个统计周期。

## 3. 关键依赖

| 依赖 | 用途 | 断链时的典型表现 |
|---|---|---|
| MySQL | 业务库：客户、触达、编排、销售的事实与状态；CAP 的 outbox / inbox；后台任务租约 | 写库失败、发件箱只进不出、任务抢不到租约 |
| Redis | 状态层、限流与频控计数、后台任务租约、画像行缓存、查询缓存 | 状态丢失或键被逐出、限流失效、缓存未命中升高 |
| Kafka | 事件总线：业务事件、原生消费组、死信主题、数据同步广播 | 消费位点滞后、死信堆积、事件链路静默断流 |
| StarRocks | 数仓：DWD 明细与画像、DWS 聚合 | 导入报错、分区为空、聚合作业产出滞后 |
| Aether | 统一入口：服务注册与路由、鉴权、行级数据权限、员工目录 | 路由缺失或冲突、鉴权失败、数据权限解析报错 |

依赖不是平级的：MySQL / Redis / Kafka 是大多数 svc 与 worker 的运行前提，StarRocks 主要影响画像与指标域，Aether 影响所有对外入口与跨域鉴权。

## 4. 巡检视角

- **域级**：先确认该域的 svc 与 worker 都在运行、近期无重启或崩溃，再看该域对外接口是否可用。
- **依赖级**：确认能否连上、读写是否正常，异常时先看连接错误与依赖自身负载，再回到使用它的域。
- **链路级**：消费组位点是否推进、死信是否到达、发件箱是否积压、工作流实例是否卡单、投递分片是否落死信。
- **数仓级**：昨日分区是否为空、枚举是否漂移、日量环比是否异常、DWS 产出是否滞后、业务库与数仓是否对得上、作业失败或重跑后是否完成对账。

巡检发现异常时，按“域 → 依赖 → 数据链路 → 数仓”的顺序缩小范围；能读到证据就给结论，证据不足如实说明缺口。

---

整理自 tradepass.growth 仓库文档
