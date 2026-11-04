#!/usr/bin/env python3
"""生成 Growth Inspector 视觉样张：3 种版面 × 3 个关键页。

用法：
    python3 docs/visual-style/build.py

输出：
    docs/visual-style/v{1,2,3}/{overview,run,findings}.html
    docs/visual-style/index.html

样张只用于视觉评审，不修改应用代码；数据为 illustrative sample。
"""

from pathlib import Path

ROOT = Path(__file__).resolve().parent

LAYOUTS = [
    ("a", "v1", "A 报告栏", "1200 / 40px", "1200 阅读宽度，表格横向滚动兜底，行高 40；默认候选。"),
    ("b", "v2", "B 全宽", "100% / 36px", "边到边，行高 36；信息密度最高，适合宽表。"),
    ("c", "v3", "C 留白", "1120 / 48px", "1120 窄栏，行高 48，章节间距 88；已选：更克制、密度更低。"),
]

PAGES = [
    ("overview", "总览"),
    ("run", "执行详情"),
    ("findings", "异常"),
]

NAV_ITEMS = [
    ("overview", "总览"),
    ("findings", "异常"),
    ("run", "执行"),
    ("report", "报告"),
    ("system", "体系"),
    ("settings", "设置"),
]

BRAND_MARK = """<svg class="brand__mark" viewBox="0 0 24 24" fill="none" aria-hidden="true">
  <rect x="3.25" y="3.25" width="17.5" height="17.5" stroke="currentColor" stroke-width="1.4"/>
  <path d="M8 7.5v9M12 7.5v4.5M16 7.5v6.5" stroke="currentColor" stroke-width="1.4"/>
  <path d="M3.25 16.75h17.5" stroke="currentColor" stroke-width="1.4"/>
</svg>"""


def status(kind: str, label: str, glyph: str, sub: str = "") -> str:
    sub_html = f'<span class="status__sub">{sub}</span>' if sub else ""
    return (
        f'<span class="status status--{kind}">'
        f'<span class="status__glyph" aria-hidden="true">{glyph}</span>'
        f'<span>{label}</span>{sub_html}</span>'
    )


def chip(text: str, kind: str = "") -> str:
    cls = f"chip chip--{kind}" if kind else "chip"
    return f'<span class="{cls}">{text}</span>'


def nav(active: str) -> str:
    out = []
    for key, label in NAV_ITEMS:
        if key == active:
            out.append(
                f'<a class="nav__link is-active" href="{key}.html" aria-current="page">{label}</a>'
            )
        elif key in {"overview", "run", "findings"}:
            out.append(f'<a class="nav__link" href="{key}.html">{label}</a>')
        else:
            out.append(
                f'<a class="nav__link is-disabled" href="#" aria-disabled="true" tabindex="-1" '
                f'title="样张未覆盖，正式重做时补齐">{label}</a>'
            )
    return "\n        ".join(out)


def variant_bar(layout: str, page: str) -> str:
    links = []
    for letter, folder, name, spec, _ in LAYOUTS:
        active = " is-active" if letter == layout else ""
        current = ' aria-current="true"' if letter == layout else ""
        links.append(
            f'<a class="{active.strip()}"{current} href="../{folder}/{page}.html" '
            f'title="{name} · {spec}" aria-label="{name} · {spec}">{letter.upper()}</a>'
        )
    return (
        '<div class="variant-bar" role="navigation" aria-label="版面样张切换">'
        '<span class="variant-bar__label">版面</span>'
        + " ".join(links)
        + '<span class="variant-bar__note">样张</span></div>'
    )


def page_shell(layout: str, folder: str, page: str, title: str, content: str) -> str:
    return f"""<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="color-scheme" content="light dark">
  <title>{title} · Growth Inspector 视觉样张</title>
  <link rel="stylesheet" href="../assets/tokens.css">
  <link rel="stylesheet" href="../assets/base.css">
  <link rel="stylesheet" href="../assets/components.css">
</head>
<body data-layout="{layout}" data-page="{page}">
  <a class="skip" href="#main">跳到正文</a>

  <header class="masthead">
    <div class="masthead__inner">
      <a class="brand" href="../index.html">
        {BRAND_MARK}
        <span>
          <span class="brand__name">Growth Inspector</span>
          <span class="brand__sub">Inspection report</span>
        </span>
      </a>

      <nav class="nav" aria-label="主导航">
        {nav(page)}
      </nav>

      <div class="masthead__context">
        <span class="context-label">项目 / 环境</span>
        <select class="select" aria-label="选择项目与环境">
          <option>Growth · prod</option>
          <option>Growth · beta</option>
        </select>
        <button class="btn btn--primary" type="button">立即巡检</button>
        <button class="btn btn--quiet theme-toggle" type="button" data-theme-toggle aria-pressed="false">深色</button>
      </div>
    </div>
  </header>

  <main id="main" class="shell">
{content}
  </main>

  <footer class="footer">
    <div class="footer__inner">
      <span>Growth Inspector · 视觉样张 v0.2</span>
      <span>数据源 Grafana · thanos（样张数据，非真实巡检结果）</span>
      <span>RPT-2026-11-04-01</span>
    </div>
  </footer>

  {variant_bar(layout, page)}
  <script src="../assets/theme.js" defer></script>
</body>
</html>
"""


# ---------------------------------------------------------------------------
# 总览
# ---------------------------------------------------------------------------


def overview_content() -> str:
    services = [
        ("management-svc", "ok", "✓", ""),
        ("openapi-svc", "ok", "✓", ""),
        ("growth-worker", "warn", "▲", "CAP outbox 680"),
        ("ump-worker", "bad", "■", "死信 37 / 15m"),
        ("materialize-worker", "ok", "✓", ""),
        ("callmedia-svc", "ok", "✓", ""),
    ]
    deps = [
        ("MySQL", "ok", "✓", ""),
        ("Redis", "ok", "✓", ""),
        ("Kafka", "ok", "✓", ""),
        ("StarRocks", "ok", "✓", ""),
    ]

    def nodes(items):
        out = []
        for name, kind, glyph, meta in items:
            cls = "node" + (f" node--{kind}" if kind != "ok" else "")
            meta_html = (
                f'<span class="node__meta">{meta}</span>' if meta else ""
            )
            out.append(
                f'<div class="{cls}">'
                f'<span class="node__glyph" aria-hidden="true">{glyph}</span>'
                f'<div class="node__body">'
                f'<span class="node__name">{name}</span>'
                f'{meta_html}'
                f'</div></div>'
            )
        return "".join(out)

    findings = f"""
      <article class="finding finding--critical">
        <div class="finding__severity finding__severity--critical">
          <span aria-hidden="true">■</span> Critical
        </div>
        <div class="finding__main">
          <h3 class="finding__title">死信 topic 到达量超阈值</h3>
          <p class="finding__meta">
            <span class="mono">growth.ekyc.dlq</span>
            <span>37 条 / 15m</span>
            <span>规则 v3</span>
            <span>首次 10:42</span>
          </p>
        </div>
        <div class="finding__side">
          <a href="run.html#evidence-deadletter">查看证据 →</a>
        </div>
      </article>

      <article class="finding finding--warning">
        <div class="finding__severity finding__severity--warning">
          <span aria-hidden="true">▲</span> Warning
        </div>
        <div class="finding__main">
          <h3 class="finding__title">CAP outbox 待投递积压</h3>
          <p class="finding__meta">
            <span class="mono">cap.published · Scheduled</span>
            <span>680 条</span>
            <span>规则 v2</span>
            <span>首次 09:15</span>
          </p>
        </div>
        <div class="finding__side">
          <a href="run.html">查看证据 →</a>
        </div>
      </article>
    """

    runs = [
        ("17:20", "运行健康巡检", "prod", "bad", "异常", "■"),
        ("17:00", "触达准入与告警口径巡检", "prod", "ok", "通过", "✓"),
        ("16:28", "数仓质量巡检", "prod", "ok", "通过", "✓"),
    ]
    run_rows = "".join(
        f'<a class="run-row" href="run.html">'
        f'<span class="run-row__time">{time}</span>'
        f'<span class="run-row__name">{name}</span>'
        f'<span class="run-row__env">{env}</span>'
        f'<span class="run-row__status">{status(kind, label, glyph)}</span>'
        f'<span class="run-row__arrow" aria-hidden="true">→</span>'
        f'</a>'
        for time, name, env, kind, label, glyph in runs
    )

    return f"""
    <header class="page-head">
      <div class="page-head__main">
        <p class="kicker">集群诊断 · Cluster diagnosis</p>
        <h1 class="page-title">今日集群诊断</h1>
        <p class="page-meta">
          <span>Growth</span><span>prod</span><span>2026-11-04 17:20</span><span>31 / 31 检查项</span>
        </p>
      </div>
      <div class="page-head__actions">
        <a class="btn btn--quiet" href="run.html">判定方法</a>
      </div>
    </header>

    <section class="cluster-hero" aria-labelledby="cluster-title">
      <h2 class="cluster-title" id="cluster-title">
        集群 <span class="nowrap">8 / 10 正常</span>；<span class="nowrap"><em>2 个对象</em>需要处理</span>。
      </h2>
      <p class="cluster-copy">
        Growth · prod 的 6 个服务与 4 项依赖中，死信 topic 与 CAP outbox 两处需要跟进；
        另有 1 项工作流卡单因取值路径缺失待证，未计入告警。
      </p>
      <div class="cluster-stats">
        <span class="cluster-stat"><strong>8</strong>正常</span>
        <span class="cluster-stat cluster-stat--warn"><strong>1</strong>注意</span>
        <span class="cluster-stat cluster-stat--bad"><strong>1</strong>异常</span>
        <span class="cluster-stat cluster-stat--pending"><strong>1</strong>项待证</span>
        <span class="cluster-stat__note">10 个对象 · 数据源 Grafana · thanos</span>
      </div>
      <div class="cluster-meter" role="img" aria-label="10 个对象：8 正常、1 注意、1 异常">
        <span class="cluster-meter__seg cluster-meter__seg--ok" style="width: 80%"></span>
        <span class="cluster-meter__seg cluster-meter__seg--warn" style="width: 10%"></span>
        <span class="cluster-meter__seg cluster-meter__seg--bad" style="width: 10%"></span>
      </div>
    </section>

    <section class="section" aria-labelledby="s-cluster">
      <div class="section__head">
        <span class="section__index">§ 01</span>
        <h2 class="section__title" id="s-cluster">集群状态</h2>
        <span class="section__meta">Growth · prod</span>
      </div>
      <div class="section__body">
        <div class="cluster-group">
          <div class="cluster-group__head"><h3>服务</h3><span>6</span></div>
          <div class="node-grid node-grid--services">{nodes(services)}</div>
        </div>
        <div class="cluster-group">
          <div class="cluster-group__head"><h3>依赖</h3><span>4</span></div>
          <div class="node-grid node-grid--deps">{nodes(deps)}</div>
        </div>
      </div>
    </section>

    <section class="section" aria-labelledby="s-findings">
      <div class="section__head">
        <span class="section__index">§ 02</span>
        <h2 class="section__title" id="s-findings">需要处理</h2>
        <span class="section__meta">2 条未恢复</span>
      </div>
      <div class="section__body">
        <div class="finding-list">{findings}</div>
        <p class="note" style="margin-top: 14px;">
          判定型检查项只有“断言不成立 + 框架复核一致”才进入 Finding；Error / Mismatch 只进日报观察。
        </p>
      </div>
    </section>

    <section class="section" aria-labelledby="s-recent">
      <div class="section__head">
        <span class="section__index">§ 03</span>
        <h2 class="section__title" id="s-recent">最近执行</h2>
        <span class="section__meta"><a href="run.html">查看执行详情 →</a></span>
      </div>
      <div class="section__body">
        <div class="run-list">{run_rows}</div>
      </div>
    </section>
    """


# ---------------------------------------------------------------------------
# 执行详情
# ---------------------------------------------------------------------------


def run_content() -> str:
    rows = [
        ("management-svc 进程心跳", "uptime / health", "ok", "Ok", "✓", "Ok", "healthy", "1 次 · 62 ms", "v1", "—"),
        ("死信 topic 到达量", "growth.ekyc.dlq", "bad", "异常", "■", "Abnormal", "37", "3 次 · 1.1 s", "v3", "F-01"),
        ("CAP outbox 待投递积压", "cap.published · Scheduled", "warn", "注意", "▲", "Abnormal", "680", "1 次 · 840 ms", "v2", "F-02"),
        ("工作流卡单", "wf_instance · 30m", "pending", "待证", "◇", "未判定", "路径缺失", "1 次 · 1.2 s", "v4", "观察"),
        ("Kafka 原生消费组 lag", "worker.*.native", "ok", "Ok", "✓", "Ok", "8,420", "1 次 · 1.0 s", "v1", "—"),
        ("gatekeeper 询问失败率", "5m / tenant", "ok", "Ok", "✓", "Ok", "0.04", "1 次 · 900 ms", "v1", "—"),
        ("DWD 昨日分区", "dwd_cdp_user_login_di", "ok", "Ok", "✓", "Ok", "1,204,331", "1 次 · 1.3 s", "v2", "—"),
        ("UMP 分片堆积", "Pending · 10m", "ok", "Ok", "✓", "Ok", "0", "1 次 · 800 ms", "v1", "—"),
    ]

    table_rows = []
    for title, sub, kind, label, glyph, agent, recon, evidence, rule, finding in rows:
        finding_html = (
            f'<a href="findings.html">{finding}</a>' if finding.startswith("F-") else finding
        )
        evidence_html = (
            f'<a href="#evidence-deadletter">{evidence}</a>'
            if title.startswith("死信")
            else evidence
        )
        table_rows.append(
            f"""<tr class="{'is-selected' if title.startswith('死信') else ''}">
          <td><span class="table__title">{title}</span><span class="table__sub">{sub}</span></td>
          <td>{status(kind, label, glyph)}</td>
          <td class="mono">{agent}</td>
          <td class="mono">{recon}</td>
          <td class="num">{evidence_html}</td>
          <td class="mono">{rule}</td>
          <td>{finding_html}</td>
        </tr>"""
        )

    return f"""
    <nav class="breadcrumb" aria-label="面包屑">
      <a href="overview.html">总览</a>
      <span class="breadcrumb__sep">/</span>
      <a href="overview.html">执行记录</a>
      <span class="breadcrumb__sep">/</span>
      <span>运行健康巡检 · prod</span>
    </nav>

    <header class="run-head">
      <div>
        <p class="kicker">执行详情 · Run evidence</p>
        <h1 class="run-head__title">运行健康巡检 · prod</h1>
        <p class="run-head__meta">
          <span class="mono">0fdf8154-66d8-4dea-be41-ef69da49b0c7</span>
          <span>2026-11-04 17:20:14</span>
          <span>2.4 s</span>
          <span>调度</span>
          <span>Growth</span>
        </p>
      </div>
      <div class="run-head__side">
        {status("bad", "完成 · 有异常", "■")}
        <span class="note">18 / 18 项已落库</span>
      </div>
    </header>

    <section class="section section--tight" aria-labelledby="s-verdict">
      <div class="section__head">
        <span class="section__index">§ 01</span>
        <h2 class="section__title" id="s-verdict">判定摘要</h2>
        <span class="section__meta">双判一致 17 / 18</span>
      </div>
      <div class="section__body">
        <div class="verdict">
          <span class="verdict__label">本次结论</span>
          <span class="verdict__value"><em>1 项异常</em> · 1 项注意 · 1 项待证</span>
          <span class="verdict__meta">覆盖 18 / 18 · 工具调用 12 次 · 0 次告警风暴</span>
        </div>
        <div class="meter" role="img" aria-label="18 项结果：通过 15，注意 1，异常 1，待证 1">
          <span class="meter__seg meter__seg--ok" style="width: 83.33%"></span>
          <span class="meter__seg meter__seg--warn" style="width: 5.56%"></span>
          <span class="meter__seg meter__seg--bad" style="width: 5.56%"></span>
          <span class="meter__seg meter__seg--pending" style="width: 5.55%"></span>
        </div>
        <div class="meter__legend">
          <span><b>15</b> 通过</span>
          <span><b>1</b> 注意</span>
          <span><b>1</b> 异常</span>
          <span><b>1</b> 待证</span>
          <span class="muted">颜色只做二次编码；形状/图标/文字先表达语义</span>
        </div>
      </div>
    </section>

    <section class="section" aria-labelledby="s-items">
      <div class="section__head">
        <span class="section__index">§ 02</span>
        <h2 class="section__title" id="s-items">检查项判定</h2>
        <span class="section__meta">展示 8 / 18 项</span>
      </div>
      <div class="section__body">
        <div class="table-wrap">
          <table class="table">
            <caption class="sr-only">运行健康巡检的检查项判定与证据</caption>
            <thead>
              <tr>
                <th scope="col">检查项</th>
                <th scope="col">判定</th>
                <th scope="col">Agent 判定</th>
                <th scope="col">框架复核</th>
                <th scope="col" class="num">证据</th>
                <th scope="col">规则</th>
                <th scope="col">Finding</th>
              </tr>
            </thead>
            <tbody>
              {''.join(table_rows)}
            </tbody>
          </table>
        </div>
        <p class="note" style="margin-top: 10px;">表中仅列出需要看的 8 项；其余 10 项均通过，未展开。</p>

        <details class="evidence" id="evidence-deadletter" open>
          <summary>证据 03 · 死信 topic 到达量 · 3 次工具调用</summary>
          <div class="evidence__body">
            <div class="evidence__grid">
              <div>
                <p class="evidence__label">检查项</p>
                <p class="evidence__value">死信 topic 到达量（growth.ekyc.dlq）</p>
              </div>
              <div>
                <p class="evidence__label">断言</p>
                <p class="evidence__value"><code>$.value</code> Lte 0 · 窗口 15m · 级别 Critical</p>
              </div>
              <div>
                <p class="evidence__label">Agent claim</p>
                <p class="evidence__value">Abnormal · 37 条 · “10:40 后持续新增，疑似消费者重试耗尽”</p>
              </div>
              <div>
                <p class="evidence__label">框架复核</p>
                <p class="evidence__value">一致 · 37 ≤ 0 不成立 → Abnormal · Finding 已建</p>
              </div>
            </div>
            <pre class="code">{{
  "metric": "ekyc_outbox_dead_letters",
  "window": "15m",
  "value": 37,
  "series": [3, 5, 8, 11, 15, 22, 28, 31, 35, 37]
}}</pre>
            <p class="note">工具：grafana__query_prometheus · thanos · 3 次调用 · 1.1 s · 结果 8 KB 内，未截断。</p>
          </div>
        </details>

        <details class="evidence">
          <summary>证据 02 · CAP outbox 待投递积压 · 1 次工具调用</summary>
          <div class="evidence__body">
            <div class="evidence__grid">
              <div>
                <p class="evidence__label">检查项</p>
                <p class="evidence__value">CAP outbox 待投递积压（cap.published · Scheduled）</p>
              </div>
              <div>
                <p class="evidence__label">断言</p>
                <p class="evidence__value"><code>$.value</code> Lt 500 · 窗口 15m · 级别 Warning</p>
              </div>
              <div>
                <p class="evidence__label">Agent claim</p>
                <p class="evidence__value">Abnormal · 680 条 · “积压从 09:15 开始抬升”</p>
              </div>
              <div>
                <p class="evidence__label">框架复核</p>
                <p class="evidence__value">一致 · 680 &lt; 500 不成立 → Abnormal · Finding 已建</p>
              </div>
            </div>
            <p class="note">工具：grafana__query_prometheus · thanos · 1 次调用 · 840 ms。</p>
          </div>
        </details>

        <details class="evidence">
          <summary>证据 04 · 工作流卡单 · 待证，为什么不是异常</summary>
          <div class="evidence__body">
            <div class="evidence__grid">
              <div>
                <p class="evidence__label">检查项</p>
                <p class="evidence__value">工作流卡单（wf_instance · 30m）</p>
              </div>
              <div>
                <p class="evidence__label">断言</p>
                <p class="evidence__value"><code>$.count</code> Lte 0 · 级别 Critical</p>
              </div>
              <div>
                <p class="evidence__label">Agent claim</p>
                <p class="evidence__value">未给出可复核的 claim · 工具返回中没有 <code>$.count</code></p>
              </div>
              <div>
                <p class="evidence__label">框架复核</p>
                <p class="evidence__value">Error · 取值路径缺失 → 不建 Finding、不发 Lark，进日报观察</p>
              </div>
            </div>
            <p class="note">
              “待证”不是失败，也不等于健康；它表示这次没有足够证据下结论。补上查询或换工具后重跑即可。
            </p>
          </div>
        </details>
      </div>
    </section>

    <section class="section" aria-labelledby="s-snapshot">
      <div class="section__head">
        <span class="section__index">§ 03</span>
        <h2 class="section__title" id="s-snapshot">快照与知识注入</h2>
        <span class="section__meta">默认折叠 · 不把手册全文铺在页面上</span>
      </div>
      <div class="section__body">
        <details class="evidence">
          <summary>通用手册 · Growth 通用运维手册（7,563 字）</summary>
          <div class="evidence__body">
            <p class="evidence__value">服务拓扑、指标口径、告警升级路径；已注入本次 agent 会话。</p>
            <p class="note">快照版本：2026-11-03 18:20 · 只读 · 完整内容见「体系 / 手册」。</p>
          </div>
        </details>
        <details class="evidence">
          <summary>专用手册 · 3 份引用（死信 / 积压与卡单 / CAP outbox）</summary>
          <div class="evidence__body">
            <p class="evidence__value">被检查单显式引用的专用知识，仅在本次执行注入。</p>
            <p class="note">引用不跨 Project；快照里保留标题与版本，正文按需展开。</p>
          </div>
        </details>
        <details class="evidence">
          <summary>AI 摘要 · 模板兜底（未配置模型 key 时的降级）</summary>
          <div class="evidence__body">
            <p class="evidence__value">摘要标记 <code>FallbackUsed = 1</code>，不伪装成模型生成；日报里同样标注。</p>
          </div>
        </details>
        <details class="evidence">
          <summary>Lark 通知 · 本轮 0 条（mock 通道）</summary>
          <div class="evidence__body">
            <p class="evidence__value">v1 只写 LarkNotification 留痕，不发起真实请求；finding 建单时会显示在这里。</p>
          </div>
        </details>
      </div>
    </section>

    <section class="section" aria-labelledby="s-observe">
      <div class="section__head">
        <span class="section__index">§ 04</span>
        <h2 class="section__title" id="s-observe">观察</h2>
        <span class="section__meta">不进告警</span>
      </div>
      <div class="section__body">
        <div class="callout">
          <div class="inline-actions" style="justify-content: space-between;">
            {status("pending", "待证", "◇", "工作流卡单")}
            {chip("日报观察", "")}
          </div>
          <p class="callout__copy" style="margin-top: 10px;">
            本轮 1 项因取值路径缺失无法复核。它不进 Finding、不发 Lark，但会在日报「观察」段出现，
            避免“静默吞掉”。这也解释了为什么首页的“异常 1”与详情里的一行“待证”不是矛盾。
          </p>
        </div>
      </div>
    </section>
    """


# ---------------------------------------------------------------------------
# 异常
# ---------------------------------------------------------------------------


def findings_content() -> str:
    items = f"""
      <article class="finding finding--critical">
        <div class="finding__severity finding__severity--critical">
          <span aria-hidden="true">■</span> Critical
        </div>
        <div class="finding__main">
          <h3 class="finding__title">死信 topic 到达量超阈值</h3>
          <p class="finding__meta">
            <span class="mono">growth.ekyc.dlq</span>
            <span>37 条 / 15m · 阈值 ≤ 0</span>
            <span>规则 v3</span>
            <span>首次 10:42 · 最近 17:20</span>
          </p>
        </div>
        <div class="finding__side">
          {chip("未恢复", "danger")}
          <a href="run.html#evidence-deadletter">查看证据 →</a>
        </div>
      </article>

      <article class="finding finding--warning">
        <div class="finding__severity finding__severity--warning">
          <span aria-hidden="true">▲</span> Warning
        </div>
        <div class="finding__main">
          <h3 class="finding__title">CAP outbox 待投递积压</h3>
          <p class="finding__meta">
            <span class="mono">cap.published · Scheduled</span>
            <span>680 条 · 阈值 &lt; 500</span>
            <span>规则 v2</span>
            <span>首次 09:15 · 最近 17:20</span>
          </p>
        </div>
        <div class="finding__side">
          {chip("未恢复", "warning")}
          <a href="run.html">查看证据 →</a>
        </div>
      </article>

      <article class="finding finding--resolved">
        <div class="finding__severity finding__severity--ok">
          <span aria-hidden="true">✓</span> 已恢复
        </div>
        <div class="finding__main">
          <h3 class="finding__title">Kafka 原生消费组 lag 回落</h3>
          <p class="finding__meta">
            <span class="mono">worker.*.native</span>
            <span>8,420 · 阈值 &lt; 10,000</span>
            <span>规则 v1</span>
            <span>恢复于 11:05</span>
          </p>
        </div>
        <div class="finding__side">
          {chip("已恢复", "ok")}
          <a href="run.html">查看证据 →</a>
        </div>
      </article>
    """

    return f"""
    <header class="page-head">
      <div class="page-head__main">
        <p class="kicker">异常 · Findings</p>
        <h1 class="page-title">异常与观察</h1>
        <p class="page-meta">
          <span>Growth</span><span>prod</span><span>最近 7 天</span><span>2 条未恢复</span>
        </p>
      </div>
      <div class="page-head__actions">
        <a class="btn btn--ghost" href="overview.html">回到总览</a>
        <a class="btn btn--primary" href="run.html">查看最近执行</a>
      </div>
    </header>

    <div class="filterbar" role="search" aria-label="筛选异常">
      <div class="field">
        <label class="field__label" for="f-status">状态</label>
        <select class="select" id="f-status">
          <option>未恢复</option>
          <option>已恢复</option>
          <option>全部</option>
        </select>
      </div>
      <div class="field">
        <label class="field__label" for="f-severity">级别</label>
        <select class="select" id="f-severity">
          <option>全部级别</option>
          <option>Critical</option>
          <option>Warning</option>
        </select>
      </div>
      <div class="field">
        <label class="field__label" for="f-env">环境</label>
        <select class="select" id="f-env">
          <option>prod</option>
          <option>beta</option>
        </select>
      </div>
      <div class="field">
        <label class="field__label" for="f-window">时间</label>
        <select class="select" id="f-window">
          <option>最近 7 天</option>
          <option>今天</option>
          <option>最近 30 天</option>
        </select>
      </div>
      <div class="filterbar__actions">
        <button class="btn btn--primary" type="button">筛选</button>
        <button class="btn btn--quiet" type="button">重置</button>
      </div>
    </div>

    <div class="kpi-grid" style="margin-top: 6px;">
      <div class="kpi">
        <p class="kpi__label">未恢复</p>
        <p class="kpi__value">2</p>
        <p class="kpi__note">1 Critical · 1 Warning</p>
      </div>
      <div class="kpi">
        <p class="kpi__label">今日新增</p>
        <p class="kpi__value">2</p>
        <p class="kpi__note">均来自 17:20 轮次</p>
      </div>
      <div class="kpi">
        <p class="kpi__label">已恢复</p>
        <p class="kpi__value">1</p>
        <p class="kpi__note">Kafka lag · 11:05</p>
      </div>
      <div class="kpi">
        <p class="kpi__label">待证观察</p>
        <p class="kpi__value">1</p>
        <p class="kpi__note">工作流卡单 · 不进告警</p>
      </div>
    </div>

    <section class="section" aria-labelledby="s-list">
      <div class="section__head">
        <span class="section__index">§ 01</span>
        <h2 class="section__title" id="s-list">未恢复与已恢复</h2>
        <span class="section__meta">按严重级、首次出现排序</span>
      </div>
      <div class="section__body">
        <div class="finding-list">{items}</div>
        <p class="note" style="margin-top: 14px;">
          Finding 按 (Project, Environment, Checklist, ChecklistItem) 去重，绑定 RuleVersion；
          规则变更时旧 finding 以“判据变更”关闭，新版本重开。
        </p>
      </div>
    </section>

    <section class="section" aria-labelledby="s-observe">
      <div class="section__head">
        <span class="section__index">§ 02</span>
        <h2 class="section__title" id="s-observe">待证观察</h2>
        <span class="section__meta">不进 Finding / 不发 Lark</span>
      </div>
      <div class="section__body">
        <div class="callout">
          <div class="inline-actions" style="justify-content: space-between;">
            {status("pending", "待证", "◇", "工作流卡单 · wf_instance")}
            {chip("日报观察", "")}
          </div>
          <p class="callout__copy" style="margin-top: 10px;">
            工具返回里没有断言需要的 <code>$.count</code>，框架复核判 Error。它不会出现在上面的 Finding 列表，
            也不会触发 Lark；但会在日报「观察」段出现，避免静默吞掉。
          </p>
        </div>
      </div>
    </section>

    <section class="section" aria-labelledby="s-healthy">
      <div class="section__head">
        <span class="section__index">§ 03</span>
        <h2 class="section__title" id="s-healthy">健康态预览</h2>
        <span class="section__meta">生产无故障时</span>
      </div>
      <div class="section__body">
        <div class="empty empty--ok">
          <span class="empty__glyph" aria-hidden="true">✓</span>
          <h3 class="empty__title">本次巡检未发现异常</h3>
          <p class="empty__copy">
            29 项通过，2 项待证观察；没有任何异常 Finding。没有异常时，
            页面也应该是一份可读的报告，而不是一块空白。
          </p>
          <p style="margin-top: 16px;"><a class="btn btn--ghost" href="overview.html">打开今日报告</a></p>
        </div>
      </div>
    </section>
    """


# ---------------------------------------------------------------------------
# 生成
# ---------------------------------------------------------------------------


def build_pages() -> None:
    content_map = {
        "overview": (overview_content, "今日巡检诊断"),
        "run": (run_content, "执行详情"),
        "findings": (findings_content, "异常与观察"),
    }

    for letter, folder, *_ in LAYOUTS:
        target = ROOT / folder
        target.mkdir(parents=True, exist_ok=True)
        for page, (builder, title) in content_map.items():
            html = page_shell(letter, folder, page, title, builder())
            (target / f"{page}.html").write_text(html, encoding="utf-8")
            print(f"wrote {folder}/{page}.html")


def build_index() -> None:
    light_swatches = [
        ("paper", "#E6E7E4", "页面底 · 丝绸灰"),
        ("paper-raised", "#EFEFEC", "面板"),
        ("ink", "#1A1A1A", "主文字 · 玄色"),
        ("muted", "#5B6670", "次要文字"),
        ("accent", "#14535A", "链接 / 激活 · 墨青"),
        ("danger", "#B23A2F", "仅异常"),
        ("warning", "#8A5A00", "注意"),
        ("ok", "#2E6B45", "通过"),
    ]
    dark_swatches = [
        ("paper", "#242B36", "页面底 · 烟墨"),
        ("paper-raised", "#2C3441", "面板"),
        ("ink", "#D6ECF0", "主文字 · 月白"),
        ("muted", "#A7B3B8", "次要文字"),
        ("accent", "#8FC3C3", "链接 / 激活"),
        ("danger", "#E08A80", "仅异常"),
        ("warning", "#D6B06A", "注意"),
        ("ok", "#8EC3A0", "通过"),
    ]

    def swatch_group(items):
        return "".join(
            f'<div class="swatch"><span class="swatch__chip" style="background:{value}"></span>'
            f'<span class="swatch__name">{name}</span><span class="swatch__value">{value}</span>'
            f'<span class="swatch__use">{use}</span></div>'
            for name, value, use in items
        )

    swatch_html = f"""
      <div class="palette-group">
        <h3>浅色 · 丝绸灰 / 玄色</h3>
        <div class="swatches">{swatch_group(light_swatches)}</div>
      </div>
      <div class="palette-group">
        <h3>深色 · 烟墨 / 月白</h3>
        <div class="swatches">{swatch_group(dark_swatches)}</div>
      </div>
    """

    cards = []
    for letter, folder, name, spec, desc in LAYOUTS:
        page_links = " · ".join(
            f'<a href="{folder}/{page}.html">{label}</a>' for page, label in PAGES
        )
        cards.append(
            f"""<article class="layout-card">
          <div class="layout-card__head">
            <span class="layout-card__letter">{letter.upper()}</span>
            <div>
              <h3>{name}</h3>
              <p class="mono">{spec}</p>
            </div>
          </div>
          <p class="layout-card__desc">{desc}</p>
          <div class="layout-card__links">{page_links}</div>
          <a class="layout-card__preview" href="{folder}/overview.html">
            <img src="screenshots/{letter}-overview.png" alt="{name} 总览页顶部预览" loading="lazy">
          </a>
        </article>"""
        )

    status_legend = f"""
      <div class="status-legend">
        <div class="status-legend__item">{status("ok", "Ok", "✓")}<p class="status-legend__desc">通过；低饱和。</p></div>
        <div class="status-legend__item">{status("warn", "注意", "▲")}<p class="status-legend__desc">Warning；琥珀。</p></div>
        <div class="status-legend__item">{status("bad", "异常", "■")}<p class="status-legend__desc">Abnormal / Critical；朱红。</p></div>
        <div class="status-legend__item">{status("pending", "待证", "◇")}<p class="status-legend__desc">Error / 证据不足；空心。</p></div>
        <div class="status-legend__item">{status("idle", "中性", "—")}<p class="status-legend__desc">Pending / Skipped。</p></div>
        <div class="status-legend__item">{status("running", "运行中", "◉")}<p class="status-legend__desc">accent 脉冲。</p></div>
      </div>
    """

    html = f"""<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Growth Inspector · 视觉风格样张 v0.2</title>
  <link rel="stylesheet" href="assets/tokens.css">
  <link rel="stylesheet" href="assets/base.css">
  <link rel="stylesheet" href="assets/components.css">
  <style>
    .review {{ max-width: 1240px; margin: 0 auto; padding: 0 32px 96px; }}
    .review-hero {{ padding: 56px 0 28px; border-bottom: 2px solid var(--ink); }}
    .review-hero h1 {{ font-size: 44px; max-width: 18em; }}
    .review-hero p {{ max-width: 70ch; margin-top: 14px; color: var(--ink-soft); }}
    .review-kicker {{ color: var(--accent); font-family: var(--mono); font-size: 10.5px; letter-spacing: .16em; text-transform: uppercase; }}
    .review-grid {{ display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 24px; margin-top: 22px; }}
    .layout-switch {{ display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 16px; margin-top: 26px; }}
    .layout-switch a {{ display: flex; flex-direction: column; gap: 3px; padding: 18px 20px; border: 1px solid var(--line-strong); background: var(--paper-raised); color: var(--ink); text-decoration: none; }}
    .layout-switch a:hover {{ border-color: var(--accent); }}
    .layout-switch a.is-selected {{ border-color: var(--accent); box-shadow: inset 0 0 0 1px var(--accent); }}
    .layout-switch strong {{ font-family: var(--serif); font-size: 22px; font-weight: 400; }}
    .layout-switch span {{ color: var(--muted); font-family: var(--mono); font-size: 11px; }}
    .layout-switch em {{ color: var(--muted); font-style: normal; font-size: 12px; }}
    .layout-card {{ border: 1px solid var(--line); background: var(--paper-raised); padding: 18px; }}
    .layout-card__head {{ display: flex; gap: 12px; align-items: baseline; }}
    .layout-card__letter {{ display: inline-flex; width: 28px; height: 28px; align-items: center; justify-content: center; border: 1px solid var(--ink); font-family: var(--mono); font-size: 12px; }}
    .layout-card h3 {{ font-family: var(--serif); font-size: 20px; font-weight: 400; }}
    .layout-card p {{ color: var(--muted); font-size: 12px; }}
    .layout-card__desc {{ min-height: 54px; margin: 10px 0 12px; }}
    .layout-card__links {{ font-family: var(--mono); font-size: 11px; }}
    .layout-card__preview {{ display: block; margin-top: 12px; border: 1px solid var(--line); background: var(--paper-raised); overflow: hidden; }}
    .layout-card__preview img {{ display: block; width: 100%; height: 260px; object-fit: cover; object-position: top; }}
    .layout-card__preview:hover img {{ opacity: .88; }}
    .palette-group {{ margin-top: 18px; }}
    .palette-group h3 {{ font-family: var(--serif); font-size: 18px; font-weight: 400; }}
    .palette-group h3 span {{ margin-left: 10px; color: var(--muted); font-family: var(--mono); font-size: 11px; }}
    .swatches {{ display: grid; grid-template-columns: repeat(8, minmax(0, 1fr)); gap: 10px; margin-top: 12px; }}
    .dark-preview {{ margin-top: 16px; border: 1px solid var(--line); background: var(--paper-raised); }}
    .dark-preview img {{ display: block; width: 100%; height: auto; }}
    .pattern-row {{ display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; margin-top: 14px; }}
    .pattern-row figure {{ margin: 0; }}
    .pattern-row img {{ display: block; width: 100%; height: 92px; object-fit: cover; border: 1px solid var(--line); }}
    .pattern-row figcaption {{ margin-top: 6px; color: var(--muted); font-family: var(--mono); font-size: 10px; }}
    .swatch {{ border-top: 1px solid var(--line); padding-top: 8px; }}
    .swatch__chip {{ display: block; height: 42px; border: 1px solid var(--line); }}
    .swatch__name {{ display: block; margin-top: 6px; font-family: var(--mono); font-size: 10px; }}
    .swatch__value {{ display: block; color: var(--muted); font-family: var(--mono); font-size: 10px; }}
    .swatch__use {{ display: block; color: var(--muted); font-size: 11px; }}
    .specimen {{ display: grid; grid-template-columns: 1.2fr .8fr; gap: 32px; }}
    .specimen__serif {{ font-family: var(--serif); font-size: 40px; line-height: 1.25; }}
    .specimen__body {{ max-width: 60ch; color: var(--ink-soft); }}
    .review h2 {{ font-size: 26px; margin-top: 44px; padding-bottom: 8px; border-bottom: 1px solid var(--line-strong); }}
    .review-note {{ margin-top: 10px; color: var(--muted); font-size: 12px; }}
    @media (max-width: 960px) {{
      .review-grid {{ grid-template-columns: 1fr; }}
      .layout-switch {{ grid-template-columns: 1fr; }}
      .pattern-row {{ grid-template-columns: 1fr; }}
      .swatches {{ grid-template-columns: repeat(4, 1fr); }}
      .specimen {{ grid-template-columns: 1fr; }}
    }}
  </style>
</head>
<body data-layout="a" data-page="index">
  <main class="review">
    <header class="review-hero">
      <p class="review-kicker">Growth Inspector / Visual direction v0.2</p>
      <h1>诊断报告：丝绸灰、玄色、墨青，和一份能读的巡检结论。</h1>
      <p>
        3 种版面 × 3 个关键页，共 9 张静态样张；浅色 / 深色两套 token，可在任一页右上角切换。
        目的是先拍版面（宽度、行高、章节间距），再落 <code>/styleguide</code>。所有数据为样张数据。
      </p>
      <p class="review-note" style="margin-top: 14px;">
        配套文档：<a href="style-spec.md">style-spec.md</a>（token / 字体 / 状态 / IA） ·
        <a href="README.md">README.md</a>（打开方式与评审清单）
      </p>
    </header>

    <div class="layout-switch" aria-label="三版版面">
      <a href="v1/overview.html"><strong>A 报告栏</strong><span>1200 · 40px</span><em>居中、留白均衡，最像报告</em></a>
      <a href="v2/overview.html"><strong>B 全宽</strong><span>100% · 36px</span><em>边到边、密度最高，适合宽表</em></a>
      <a class="is-selected" href="v3/overview.html"><strong>C 留白 · 已选</strong><span>1120 · 48px · 88px</span><em>更克制、密度更低；宽表和长页面更长</em></a>
    </div>
    <p class="review-note">点进任一版后，右下角可切 A/B/C；右上角可切深色 / 浅色。并排看：<a href="compare.html">compare.html</a>（总览 + 执行详情）。</p>

    <h2>色板</h2>
    {swatch_html}
    <p class="review-note">对比度已算：浅色玄/丝绸灰 14.0:1、墨青 7.0:1、朱红 4.8:1、琥珀 4.8:1、通过绿 5.1:1；深色月白/烟墨 11.6:1、墨青 7.3:1、朱红 5.5:1、琥珀 7.0:1、通过绿 7.1:1。软底配对与徽章待 /styleguide 复核。</p>
    <p class="review-note">背景纹样：16px 经纬 + 8px 斜纹，约 3% 不透明度；深色用月白线。只铺页面底，不铺面板 / 正文，打印自动关闭。</p>
    <div class="pattern-row">
      <figure><img src="screenshots/pattern-light.png" alt="浅色背景纹样 2 倍细节"><figcaption>浅色 · 丝绸灰 / 玄色线 · 2×</figcaption></figure>
      <figure><img src="screenshots/pattern-dark.png" alt="深色背景纹样 2 倍细节"><figcaption>深色 · 烟墨 / 月白线 · 2×</figcaption></figure>
    </div>

    <h2>字体</h2>
    <div class="specimen">
      <div>
        <p class="specimen__serif">巡检诊断：<br>每一条结论都有证据。</p>
        <p class="mono" style="margin-top: 14px;">0fdf8154-66d8-4dea-be41-ef69da49b0c7 · 17:20:14 · 2.4 s</p>
      </div>
      <div class="specimen__body">
        <p>标题用 Noto Serif SC，正文用 Noto Sans SC，ID / 时间 / 数字用 IBM Plex Mono。中文行高按 Material 的 CJK 口径比西文 +0.1em；表格数字统一 tabular-nums。</p>
        <p style="margin-top: 10px; color: var(--muted); font-size: 12px;">字体均为 OFL，可自托管；样张已内嵌 woff2。</p>
      </div>
    </div>

    <h2>深色预览</h2>
    <p class="review-note">烟墨作底、月白作字；烟墨 #3B475B 同时作分隔线。深色下朱红提亮为 #E08A80、琥珀为 #D6B06A、通过绿为 #8EC3A0，墨青提亮为 #8FC3C3。</p>
    <div class="dark-preview"><img src="screenshots/c-overview-dark.png" alt="C 留白深色总览预览" loading="lazy"></div>
    <p class="review-note">C 留白 · 已选 · 深色。也可以直接打开 <a href="v3/overview.html?theme=dark">深色样张</a>，或点样张右上角「深色」切换。</p>

    <h2>状态语义</h2>
    {status_legend}
    <p class="review-note">朱红只给 Abnormal / Critical；Error / Mismatch / 证据不足统一为“待证”，空心、低饱和。Running 用 accent 脉冲，不占状态色。</p>

    <h2>三版对比</h2>
    <p class="review-note">缩略图为总览页顶部；点开卡片进入该版的总览，再用卡片内链接看执行详情与异常。重点看：宽表在 1280 / 1440 下是否要横向滚动、章节间距是否够、首屏结论是否立得住。</p>
    <div class="review-grid">{''.join(cards)}</div>

    <h2>看什么</h2>
    <div class="layout-card">
      <p><strong>A 报告栏</strong>：1200 宽 + 40 行高，最像“报告”；1440/1280 宽表可放下，≤1024 窄屏横向滚动。</p>
      <p><strong>B 全宽</strong>：边到边 + 36 行高，信息密度最高；首屏能放下更多行，但长文阅读会累。</p>
      <p><strong>C 留白（已选）</strong>：1120 宽 + 48 行高 + 88 章节间距；更克制、密度更低，宽表和长页面更长。</p>
      <p class="review-note">筛选/按钮不提交；主题切换与执行详情里的证据折叠可用。右上角“深色”按钮会切换整套 token，并记住选择。</p>
    </div>
  </main>
</body>
</html>
"""
    (ROOT / "index.html").write_text(html, encoding="utf-8")
    print("wrote index.html")


if __name__ == "__main__":
    build_pages()
    build_index()
