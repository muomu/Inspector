# 专用手册：CAP outbox 积压处置

CAP 的 outbox 是「业务写入与消息发布同事务」的出口：业务数据与待发消息在同一个数据库事务里提交，再由后台 relay 把待发消息投递到 Kafka（at-least-once）。outbox 堆积意味着「业务已经认为发出去了，但 Kafka 还没收到」。

来源：`tradepass.growth/docs/runbooks/workflow-user-event-consumer-dlq.md` 中关于 CAP 表与失败扫描的口径。

## 1. 机制与状态

- 待发表：`cap.published`。状态含义：
  - `Scheduled`：已与业务数据同事务落库，等 relay 投递；
  - `Succeeded`：已投递成功；
  - `Failed`：重试用尽，等人工处置。
- 重试字段：`Retries`（已重试次数）、`ExpiresAt`（超过该时刻不再重投）、`Added`（入队时间）。
- 已消费表：`cap.received`。`StatusName = 'Failed'` 的行是消费失败等补投的记录（这三条链之外仍走 CAP 的消费管道才有）。
- relay 是后台任务，随宿主启动；它停了以后 outbox 只进不出，而业务侧看到的仍然是「提交成功」。

## 2. 判据

- `Scheduled` 且未过 `ExpiresAt` 的堆积是**延迟**，不是丢失；
- 超过 `ExpiresAt`，或状态是 `Failed`，才是需要人工处置的消息；
- 15 分钟窗口内待投递（`Scheduled`）行数 < 100 为健康；
- 持续增长且 relay 在跑，通常是 Kafka 不可写（broker、topic 不存在、鉴权）或大批量事务导致单次投递变慢。

## 3. 排查顺序

1. Kafka 是否可写：broker 心跳、目标 topic 是否存在、账号是否有生产权限；
2. relay 是否在跑：进程心跳、日志里 relay 的异常与重连记录；
3. 表本身：`Scheduled` 的 `Added` 时间分布（是新堆积还是旧尾巴）、`Retries` 分布、`ExpiresAt` 是否临近；
4. 是否单批大事务：一次提交几十万行时 relay 要分批投递，短时间内行数涨得快属于正常。

## 4. 处置

- relay 恢复后会自动补发，**不需要手工重发**；
- `Failed` 或已过 `ExpiresAt` 的行：先确认目标 topic 可用，再走人工补投工具/脚本；
- 不要直接改 `StatusName`：改成 `Succeeded` 等于把没发出去的消息标记成已发，后续没有任何手段能发现丢失；
- 不要手工删 outbox 行：删了等于丢消息，且没有审计记录；
- 补投范围要写进日报：时间窗、条数、目标 topic、操作人。

## 5. 与死信的区别

- outbox 是「还没发出去」；死信 topic 是「发出去了但消费失败」。两者不是同一批消息，不要合并统计，也不要互相替代处置。
- outbox 积压时，死信 topic 通常是安静的；死信堆积时，outbox 通常是干净的。两者同时异常，先查 Kafka。

## 6. 判定口径

- `cap.published` 中 `Scheduled` 行数 < 100（15 分钟窗口）为健康；
- 超过阈值按 `Warning` 进日报；`Failed` 行出现且持续 2 个窗口升级为 `Critical`；
- 判据或表名变化时递增检查项 `RuleVersion`。
