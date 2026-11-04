# 页面迁移约定（C 留白 · DesignLayout）

> 供把旧页面迁到新壳时逐条对照。参考实现：`Home.razor`（结果页）、`Systems.razor`（配置页）、`StyleGuide.razor`。
> 样式只来自 `wwwroot/css/design-system.css`，页面里禁止写颜色、禁止行内 hex、禁止新增 CSS 文件。

## 1. 页面骨架

```razor
@page "/xxx"
@layout DesignLayout
@inject WorkspaceContextService Workspace
@inject NavigationManager Nav

<PageTitle>… · Growth Inspector</PageTitle>

<nav class="breadcrumb" aria-label="面包屑">
    <a href="@RunsHref">执行历史</a>
    <span class="breadcrumb__sep" aria-hidden="true">/</span>
    <span>详情</span>
</nav>

<header class="page-head">
    <div class="page-head__main">
        <p class="kicker">执行 · Runs</p>
        <h1 class="page-title">执行历史</h1>
        <p class="page-meta"><span>…</span><span>…</span></p>
    </div>
    <div class="page-head__actions">
        <a class="btn btn--quiet" href="…">…</a>
    </div>
</header>
```

- 页面说明用 `<p class="page-lede">`，不要用旧 `.page-subtitle`。
- 章节固定为 `.section` > `.section__head`（`§ 01` + `h2.section__title` + `span.section__meta`）+ `.section__body`。
- 页面里不写 `<style>`、不写 `style="color: …"`；只有 meter 宽度、网格列这类布局数值可以内联。

## 2. 上下文（项目 / 环境）

- 布局顶栏已经解析并显示上下文；页面自己注入 `WorkspaceContextService`：

```csharp
workspace = await Workspace.ResolveAsync(WorkspaceContextService.ReadQueryValue(Nav));
```

- 结果页（异常 / 执行 / 报告）跟随当前环境；配置页跟随当前项目。
- 跳转链接带 `workspace.QuerySuffix`：

```csharp
private string ContextQuery => workspace.QuerySuffix;
private string FindingsHref => $"/findings{ContextQuery}";
```

- GET 筛选表单必须带隐藏字段，避免 submit 丢掉 query：

```razor
<form class="toolbar" method="get">
    <input type="hidden" name="workspace" value="@workspace.Current?.Key" />
    …
</form>
```

## 3. 筛选工具栏

```razor
<form class="toolbar" method="get">
    <input type="hidden" name="workspace" value="@workspace.Current?.Key" />
    <label class="filter">
        <span class="filter__label">状态</span>
        <select class="select" name="status">…</select>
    </label>
    <label class="filter">
        <span class="filter__label">开始日期（起）</span>
        <input class="input" type="date" name="from" value="@fromFilter" />
    </label>
    <div class="toolbar__actions">
        <button class="btn btn--primary" type="submit">筛选</button>
        <a class="filter-reset" href="@ResetHref">重置</a>
    </div>
</form>
```

## 4. KPI

```razor
<div class="kpi-grid">
    <div class="kpi"><span class="kpi__value">@openCount</span><span class="kpi__label">未恢复</span><span class="kpi__note">全部严重级</span></div>
    <div class="kpi kpi--warn">…</div>
    <div class="kpi kpi--bad">…</div>
    <div class="kpi kpi--pending">…</div>
</div>
```

## 5. 表格

```razor
<div class="table-wrap" role="region" aria-label="执行记录" tabindex="0">
    <table class="table table--wide">
        <caption class="sr-only">…</caption>
        <thead>
            <tr>
                <th scope="col">检查单</th>
                <th scope="col" class="num">耗时</th>
                <th scope="col"><span class="sr-only">操作</span></th>
            </tr>
        </thead>
        <tbody>
            <tr>
                <td><a class="table__title" href="…">@name</a><span class="table__sub">…</span></td>
                <td class="num">@DisplayText.FormatDuration(ms)</td>
                <td class="actions"><a class="btn btn--quiet" href="…">详情</a></td>
            </tr>
        </tbody>
    </table>
</div>
```

- 宽表加 `table--wide`；所有 `th` 必须 `scope="col"`（或 `row`）。
- 数字列 `class="num"`（右对齐 + mono）。
- 表格里的按钮用 `btn btn--quiet` / `btn`，不要裸 `<button>`。

## 6. 状态

- 只用 `StatusPresenter` + `<StatusBadge Value="…" />`：

```razor
<StatusBadge Value="@StatusPresenter.FromRun(run.Status)" />
<StatusBadge Value="@StatusPresenter.FromResult(result.Status, severity, itemKind)" />
<StatusBadge Value="@StatusPresenter.FromSeverity(finding.Severity)" />
<StatusBadge Value="@StatusPresenter.FromFinding(finding.Status, finding.CloseReason)" />
<StatusBadge Value="@StatusPresenter.MissingResult()" />
```

- 严重级标签可用 `span class="chip chip--danger|chip--warning"`；已归档 / 中性用 `span class="chip"`。
- 禁止页面里再写 `Success → 成功`、`Error → 红色` 之类映射；`Succeeded` 文案由 `StatusPresenter` 给出「完成」。

## 7. 执行详情结构

- 判定头：`.run-hero` + `.run-title`（可含 `<em>`）+ `.run-copy` + `.run-stats`（`.run-stat`、`--warn` / `--bad` / `--pending`）+ `.run-meter` 分段。
  计数来自 `RunVerdictBuilder.Build(...)`（`RunVerdict`）。
- 检查项用 `<details class="result">`：`summary.result__summary` 内放 `span.result__title`、`<StatusBadge>`、`span.result__meta`；
  展开体 `.result__body` > `.result__grid`（`.result__label` / `.result__value`）+ `.result__section` + `<pre class="code">`。
- 快照 / 手册全文 / AI 摘要 / 通知一律折叠：`<details class="evidence"><summary>…</summary><div class="evidence__body">…</div></details>`。
- 长文本块用 `<pre class="code">`。

## 8. 表单 / 配置页

- 区块用 `.panel` + `.panel__title` + `.panel__copy`。
- 字段：`<div class="form-row">` 里放 `<label class="field"><span class="field__label">名称</span><input class="input" /></label>`；
  整行字段加 `field--wide`；文本域 `<textarea class="textarea">`。
- 操作行：`<div class="actions"><button class="btn btn--primary">保存</button><button class="btn">取消</button><span class="note">…</span></div>`。
- 归档 / 启用状态用 `.chip`；不要用旧 `.badge`。

## 9. 空态 / 错误

- 空态：`.empty` + `.empty__glyph`（◇）+ `.empty__title` + `.empty__copy`（可带链接）。
- 健康态空态加 `empty--ok`，字形 ✓。
- 加载失败 / 操作失败：`<p class="alert-line" role="alert"><strong>…</strong><span>@message</span></p>`，不要用红色（只有真实 Critical 异常才用朱红）。

## 10. 文案与 helper

- `DisplayText.NameOrMissing / Shorten / FormatDuration / TriggerText / FrequencyText / RunbookKindText / CloseReasonText / OrDash / WithArchived`
- 写完检查：页面里不再残留 `badge`、`panel` 之外的旧类（`form-col` 保留，样式已提供）、`muted` 可用但优先 `note`、`stat-grid` / `stat-card` / `table-scroll` / `page-subtitle` / `error-text` 全部删除。

## 11. 验收（每个页面）

- `dotnet build GrowthInspector.slnx` 通过（0 warning 目标）。
- 页面里 `grep` 不到：`MainLayout`、`badge`、`table-scroll`、`page-subtitle`、`stat-card`、`error-text`、`#[0-9a-fA-F]{3,6}`。
- 1440 / 390 宽度无页面横向溢出（宽表只在 `table-wrap` 内滚动）。
- 键盘可展开 `details`、可走完筛选；`details summary` 可聚焦。
