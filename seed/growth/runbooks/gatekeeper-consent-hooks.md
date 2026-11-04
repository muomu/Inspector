# 触达准入层：外部询问 hook 的监控与告警

给客户发消息前，`ump-worker`（后台判定）与各宿主（当场判定）会问租户系统的询问 hook「能不能发」。本手册列出这条链路要盯的指标与告警口径，以及落到检查项时必须取反的地方。

来源：`tradepass.growth/docs/runbooks/gatekeeper-consent-hooks.md`。

## 1. 指标从哪来

- 后台判定的指标由 `ump-worker` 经 OTLP 导出，服务名 `service.name = ump-worker`，Meter 名 `Tradepass.Growth.Gatekeeper`。部署侧要给 worker 注入 `OTEL_EXPORTER_OTLP_ENDPOINT`；不注入时整个遥测不注册（不影响启动），此时**看不到指标不等于没有问题**。
- 触达限制页的「测试」按钮与发送接口的当场判定也会产生询问请求指标。
- 导出到 Prometheus 后的名字：计数器名字里已带 `_total`，不会再加；耗时直方图会被加上单位后缀，变成 `gatekeeper_consent_latency_milliseconds_bucket` / `_sum` / `_count`。照代码里的名字查直方图查不到。
- 计数器从没发生过时，整条序列不存在。查不到 `gatekeeper_consent_failed_rows_total` 说明从没出现过询问失败，不是没接上。

## 2. 指标

| 指标（Prometheus 里的名字） | 标签 | 含义 |
|---|---|---|
| `gatekeeper_consent_requests_total` | `tenant`、`hook`、`outcome` | 发往询问 hook 的请求数；`outcome` = `ok` 或失败原因（`timeout` / `unreachable` / `http_error` / `bad_response` / `blocked_address` / `circuit_open`） |
| `gatekeeper_consent_latency_milliseconds_*` | `tenant`、`hook` | 询问请求耗时 |
| `gatekeeper_consent_breaker_open` | `tenant`、`hook` | 熔断是否断开（1 = 断开）：采样 30 秒内至少 20 次请求且全部失败就断开 30 秒，期间不发请求、直接判问不到；只报当前在用的 hook |
| `gatekeeper_consent_failed_rows_total` | `tenant`、`hook`、`reason` | 重试窗口用完仍问不到、判为询问失败（不发送）的人数 |
| `gatekeeper_admission_verdicts_total` | `kind`、`outcome` | 各类限制给出的结论数（`pass` / `block` / `unavailable`） |

`hook` 标签是 hook 编号（配置页上每个 hook 的编号），不是名字。

## 3. 告警枚举（6 条）

| 序号 | 告警 | 来源表达式 | 检查项落地 |
|---|---|---|---|
| 1 | 询问失败导致消息没发 | `increase(gatekeeper_consent_failed_rows_total[5m]) > 0` | 判定型：`increase(...) ≤ 0` |
| 2 | 询问 hook 熔断 | `max_over_time(gatekeeper_consent_breaker_open[2m]) == 1` 持续 1 分钟 | 判定型：`max_over_time(...) ≤ 0` |
| 3 | 询问失败率高（可选） | `sum by (tenant, hook) (rate(gatekeeper_consent_requests_total{outcome!="ok"}[5m])) / sum by (tenant, hook) (rate(gatekeeper_consent_requests_total[5m])) > 0.2` | 判定型：`失败率 ≤ 0.2` |
| 4 | SIP 线路不可用 | 来源手册未给出表达式 | 信息型：只取证，不设断言 |
| 5 | 媒体节点异常 | 来源手册未给出表达式 | 信息型：只取证，不设断言 |
| 6 | 暂停外呼 | 来源手册未给出表达式 | 信息型：只取证，不设断言 |

**不要自己编第 4–6 条的 PromQL**：来源手册只覆盖了前 3 条。这三条要等对应告警定义（SIP 中继探活、媒体节点健康、外呼降速/暂停）补齐后再设断言，并把 `RuleVersion` 从 1 升到 2。

## 4. 排查要点

- 「未发出」页签（日志页）能看到每个被顺延 / 被挡的人、是哪个 hook、原因与细节；自动化规则的执行日志里对应动作显示「已顺延」或「被挡」。
- `blocked_address`：hook 地址解析到了本机或链路本地 / 云元数据地址，是租户配置问题或域名被指向了不该去的地方。
- `circuit_open`：熔断中，先看同一 hook 之前的失败原因。
- 「未发出」页签里原因是 `evaluation_retry`（判定出错，稍后重试）或意外原因码（`consent.error` 检查实现抛异常、`consent.deadline` 超过判定整体截止、`consent.no_verdict` 漏回）的顺延行：判定这一位出了错，按 1 / 2 / 5 / 10 分钟、之后每 10 分钟退避重判，故障恢复后下一次到点就照常发出。细节列是异常类型名或意外原因码。
- 原因是 `evaluation_error`（判定持续出错 30 分钟后放弃）的行：判定从第一次出错起持续出错超过放弃窗口（默认 30 分钟），消息没有发出，不会再自动重试。这不是租户 hook 回答「不让发 / 问不到」，是判定自身出了问题（配置库 / 数仓不可用、检查实现缺陷），查 `ump-worker` 日志里同一时间的错误。
- 关闭整个类型（「外部询问 hook」总开关）后，所有 hook 都不再被问，消息直接放行；已经在顺延中的人会在下一次到点时放行。
- 已知风险：「测试」按钮回的「连不上 / 超时 / 对方报错」三档仍可能被有权限的管理员用来探测内网地址与端口（放开内网地址的代价，已接受）。

## 5. 判据变更

- 新增或修改告警表达式时：先改手册，再改检查项与阈值，最后递增 `RuleVersion`；
- 旧 finding 以「判据变更」关闭，新规则从下一个窗口开始判定，不追认历史；
- 3 条信息型检查项补上断言时，Severity 与 Window 一并补齐，并把 `RuleVersion` 升到 2。
