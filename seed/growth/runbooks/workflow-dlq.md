# 专用手册：用户事件消费链失败消息处置（原生 Kafka 消费组 + 死信 topic）

工作流模块的三条用户事件消费链（规则触发 / 图级中断 / 等事件投递）走 `Confluent.Kafka` 原生订阅，不走 CAP 静态订阅——`cap.received` 里不会再有这三条链的消息。它们的失败出口是死信 topic `{源 topic}.dlq`，由消费循环自己落。

仍在 CAP 上的两条内部管道消费（实例终态回写 `workflow.instance.terminated`、实例轨迹写入 `growth.workflow`）的失败处置归 CAP 通用口径（`cap.received` 的 Failed 扫描），不在本手册范围。

来源：`tradepass.growth/docs/runbooks/workflow-user-event-consumer-dlq.md`。

## 1. 消费组与位点

| 链路 | 消费组 | 订阅面 |
|---|---|---|
| 规则触发 | `tradepass-growth-worker.workflow-rule-trigger.native` | 用户事件全集（26 个 topic） |
| 图级中断 | `tradepass-growth-worker.workflow-interrupt.native` | 同上 |
| 等事件投递 | `tradepass-growth-worker.workflow-wait-event.native` | 同上 |

- 组名里的 `.native` 是刻意的：CAP 侧的有效组名是 `{前缀}.{链名}.v1`，两者落进同一个 Kafka 组会互抢分区、反复再均衡。
- 位点起点是 `AutoOffsetReset.Latest`（新组从流末尾起）。要补历史时由运维显式回填起点；不要改配置成 `Earliest`——从流起始追历史会让规则触发与图级中断对大量旧事件重新求值（可能真的建实例 / 终止实例）。规则触发另有 7 天重放守卫兜一道，中断链没有。
- 改组名等于重新开始消费，新组按 `AutoOffsetReset` 决定起点。

## 2. 订阅面缺 topic = 静默丢事件（优先查这条）

原生消费者在启动时按 broker 元数据筛掉不存在的 topic，并记一条 Warning：

```
图级中断 消费链：N 个订阅 topic 在 broker 上还不存在，暂不订阅（librdkafka 订阅全有全无，
留着会让整链一条都读不到；topic 通常由第一次写入创建，本链会周期性补订）：<名单>
```

- **为什么筛而不是硬订**：librdkafka 的订阅是全有全无的。订阅里只要有一个 topic 不存在，整批 fetch 持续报 `UnknownTopicOrPart` 且永不推进，已存在的那些 topic 上的消息也一条都读不到。
- **筛掉的后果**：那些 topic 上的事件在被补订之前不会被消费。消费者会周期性重试补订（约每分钟一次，补上时记一条「补订 N 个新出现的 topic」），但补订之前发布的事件可能已被 `Latest` 跳过。

| 情形 | 动作 |
|---|---|
| 日志出现「N 个订阅 topic 还不存在」 | 按运维事故处理：确认 26 个用户事件 topic 与 26 个 `.dlq` topic 都在（重启 worker 让 topic 预建任务跑一遍） |
| 缺的是 `.dlq` topic | 死信发不出去（只留 Critical 日志），必须补建 |
| 已丢的事件 | 按补投流程手工重放；`dlq-failure-type` / `dlq-reason` 无从查（压根没消费），按时间窗对上游 topic 回捞 |

生产正常情况下不该出现这条 Warning：宿主启动时会把用户事件全集与死信 topic 一并预建，且注册顺序保证预建先于消费者启动。

## 3. 失败怎么分流

消费循环**分区内严格串行**（Kafka offset 是串行的，原生没有「跳过这条继续下一条」的能力）。

| 失败类型 | 处置 | 死信头 `dlq-failure-type` |
|---|---|---|
| 负载解码失败 | 不重试，直接落死信 | `decode` |
| 业务侧显式标注不可重试 | 不重试，直接落死信 | `not_retryable` |
| 白名单内的可恢复故障（数据库层 / 超时 / 网络） | 原地退避重试 3 次（1s + 4s + 10s），到顶落死信 | `retry_exhausted` |
| 其余未分类异常 | 直接落死信，不重试 | `unclassified` |

- **重试是阻塞的**：可恢复故障期间该分区停摆最多约 15 秒，该分区上全部客户的触发 / 中断 / 等事件都在排队。这是「保序」的代价。
- `unclassified` 直接放弃不是偷懒：未分类异常多半是代码 bug，拿分区去赌它会不会自愈，换来的是「一个 bug 让整个分区长时间停摆」。

## 4. 什么时候介入

| 情形 | 动作 |
|---|---|
| `.dlq` topic 有消息堆积 | 按第 5 节排查，判断是否补投 |
| 三个 `.native` 组位点长时间不动（非停机时间） | 消费组停摆：先查服务实例健康与日志里的「不可恢复退出」 |
| 大量死信集中在同一 `dlq-source-topic` | 多半是系统性故障（库不可写 / 引擎未就绪），先止血再补投 |
| 死信里 `dlq-failure-type=decode` 且集中同一 topic | 上游发布侧线格式变了，先找发布方对齐，不要只补投 |

告警接法：对 26 个 `{用户事件}.dlq` topic 做消费 lag 扫描（或直接数到达量），命中即告警；三个 `.native` 组的 consumer lag 另行按「持续 > 15 分钟」告警。原来的「`cap.received WHERE StatusName='Failed' AND Name IN（用户事件名册）`」那条规则对这三条链已失效，留着会漏报。

## 5. 排查顺序

1. **读死信**：定位信息全在消息头里。

   ```bash
   kcat -b "$KAFKA" -t deposit.received.dlq -C -o beginning -c 10 -f \
     'key=%k\nheaders=%h\npayload=%s\n---\n'
   ```

2. **看对应实例**：该客户在该窗口是否有停在 WaitEvent 的实例 / 有在途流转实例（`wf_instance` 状态为 `Active` / `Suspended`）。
3. **判断是否还需要投**：

   - 实例已走超时 / 已完成 / 已中断 → 不补投，消息作废即可（延迟事件投给已推进的实例会被路由器的当前步判别吸收，补投无害但也无益）；
   - 实例仍在等待 → 补投；
   - `dlq-failure-type=decode` → 先修上游线格式，不要盲目补投。

## 6. 死信消息的形状

死信 topic 的消息 value 是原始字节原样转发（不复编码），元数据全在消息头里：

| 头 | 含义 |
|---|---|
| `dlq-source-topic` | 原 topic（裸名，不带 CAP topic 前缀） |
| `dlq-source-partition` / `dlq-source-offset` | 原分区与位点（补投时定位这一条的唯一坐标） |
| `dlq-source-group` | 判它失败的那个消费组（三条链各有死信，据此知道是谁放弃的） |
| `dlq-failure-type` | 见第 3 节分流表 |
| `dlq-reason` | 异常类型 + 消息（截断 2048 字符；完整异常在进程日志里） |
| `dlq-attempts` | 已消耗的可恢复重试次数（确定性错误为 0；重试到顶时 = 3） |
| `dlq-failed-at` | 落死信时刻 |

消息 key 是原 key（客户键），故死信 topic 内同样按客户保序。

## 7. 怎么补投

只把 value 原样发回 `dlq-source-topic`，key 与头不必带（消费侧只读 value）：

```bash
kcat -b "$KAFKA" -t deposit.received.dlq -C -o beginning -c 1 \
  | kcat -b "$KAFKA" -t deposit.received -P
```

不要经 CAP 发布接口重发：那会把消息套上 CAP 信封（`cap-msg-name` / `cap-msg-type` 等头，以及 CAP 的序列化形状），而原生消费者按裸 DTO JSON（camelCase）解析——套了之后必然解码失败，补一条死一条。

补投后原死信消息留着（topic 按保留策略自然过期），不要手工删。

## 8. 什么时候不用管

- 消息超过 `maxWaitSeconds` 窗口才补投 → 实例已超时，补投无意义；
- 死信来自非用户事件 topic → 不归本链路管（三条链只订用户事件全集）；
- `dlq-failure-type=unclassified` 且日志显示是代码 bug → 先修代码再补投，否则补一条死一条。
