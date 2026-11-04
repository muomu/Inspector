# 下一步交接 · C 留白落地

> 新会话开场直接读本文件，再读 `docs/visual-style/style-spec.md`、`design-system.css`、`DesignLayout.razor`、`Home.razor`、`StyleGuide.razor`。
> 当前工作区（`/home/muomu/Inspector`）未提交；所有改动都在工作区里。
>
> **交接状态（2026-11-04）**：本会话只完成「新壳 + 总览」。`/` 与 `/styleguide` 用 `DesignLayout`；
> `/findings`、`/runs`、`/runs/{id}`、`/reports`、`/settings`、`/runbooks`、`/checklists` 仍是 `MainLayout` 旧壳，未动。
> 新会话从 §3 第 1 项（异常页）开始，基线是 build 0 warning / 160 tests 全绿。

## 1. 已冻结的设计

- **版面 C 留白**：1120 宽 / 56 gutter / 48 行高 / 88 章节间距 / 16px 正文 / 46px 标题。
- **浅色**：丝绸灰 `#E6E7E4` + 玄色 `#1A1A1A` + 墨青 `#14535A`；朱红 `#B23A2F` 只给异常。
- **深色**：烟墨 `#242B36`（本色 `#3B475B` 作线/面板）+ 月白 `#D6ECF0` + 墨青 `#8FC3C3`。
- **背景纹样**：16px 经纬 + 8px 斜纹，约 3%，只在页面页边；内容区是一张 `paper-raised` 的「报告纸」，左右 1px 线；正文、表格、证据都不叠纹样。
- **状态**：Ok / 注意 / 异常 / 待证 / 中性 + Running；`Error / Mismatch / 证据不足 = 待证`；`Succeeded = 完成`，不是健康。
- **IA**：结果优先 + 配置分离；总览/异常/执行/报告 + 体系/设置；项目/环境顶栏上下文。
- **载体**：手写 CSS + token + 原生控件，零新增依赖。

## 2. 已完成

- `/styleguide` 已落到真实 Blazor（`http://127.0.0.1:5273/styleguide`；`?theme=dark` 深色）。
- **总览 `/` 已迁到 C 留白**（`@layout DesignLayout`）：
  - 结构：集群 N/10 结论 → 服务/依赖网格 → 需要处理 → 最近执行；去掉大印章、KPI 卡、10 行执行表、判定方法长文。
  - 结论颜色：只有真实异常（Critical）才用朱红；仅 Warning 时用琥珀；Running / 部分执行 / 缺失结果一律不显示全清。
  - 集群网格按最近一轮「进程心跳 / 连通性」检查快照映射：节点集合以快照期望检查项为准，结果按 ItemId 左连接；
    缺失结果 → 「待证 · 无结果」，未跑完 / 非 Succeeded 不会给出全清结论。
  - 「需要处理」读真实 `Finding(Open)`；「最近执行」读最近 4 轮，链接到 `/runs/{id}`。
- **真实导航壳**（`DesignLayout`）：总览 / 异常 / 执行 / 报告 / 体系 / 设置；项目/环境下拉（真实项目 + 环境，GET query `?workspace=项目Id:环境Id`）；主题切换；跳到正文；页脚保留「视觉规范」二级入口。
- **单一 `StatusPresenter`**（`src/GrowthInspector.Web/Services/StatusPresenter.cs`）：输入 result/severity/itemKind/findingStatus/runStatus，输出 state/label/glyph/sub/ariaLabel；`StatusBadge.razor` 只消费结果，页面里没有状态映射 switch。
- **新增测试**：`StatusPresenterTests` + `ClusterOverviewBuilderTests` 共 24 个用例（五态 + Running + Finding chip + 严重级分离；缺失结果 / 部分执行 / 快照回退 / 严重级缺失）。
- 新增/改动文件：
  - `src/GrowthInspector.Web/Components/Pages/Home.razor`（重写）
  - `src/GrowthInspector.Web/Components/Layout/DesignLayout.razor`（重写）
  - `src/GrowthInspector.Web/Services/StatusPresenter.cs`
  - `src/GrowthInspector.Web/Services/ClusterOverviewBuilder.cs`
  - `src/GrowthInspector.Web/Services/WorkspaceContextService.cs`
  - `src/GrowthInspector.Web/Components/Shared/StatusBadge.razor`
  - `src/GrowthInspector.Web/wwwroot/css/design-system.css`（集群结论 / 节点 / 最近执行 / 响应式）
  - `src/GrowthInspector.Web/wwwroot/js/theme.js`（aria-pressed、enhancedload 同步）
  - `Program.cs`（注册 `WorkspaceContextService`）
  - `tests/GrowthInspector.Tests/StatusPresenterTests.cs`
  - `tests/GrowthInspector.Tests/ClusterOverviewBuilderTests.cs`
- 验收结果（2026-11-04）：
  - `dotnet build GrowthInspector.slnx` 0 warning / 0 error；`dotnet test` **160 passed**（原 136 + StatusPresenter / ClusterOverviewBuilder 24）。
  - `/`、`/styleguide`、旧 `/findings`、`/runs`、`/reports`、`/runbooks`、`/checklists`、`/settings` 均 200。
  - 1440 / 1280 / 390，浅/深：无页面横向溢出；`lang="zh-CN"`；主题 localStorage 记忆（含同一布局内的增强导航）；`aria-pressed` 同步。
  - 截图：`docs/visual-style/screenshots/app-home-full.png`（1440 浅色）、`app-home-dark-full.png`、`app-home-1280.png`、`app-home-390.png`、`app-home-dark-390.png`。

## 3. 下一步要做的

把异常 / 执行 / 报告页迁到 `DesignLayout`，然后才是把 `.ds` 提升为全局：

1. **异常 `/findings`**：标题 + 筛选 + 4 个 KPI（未恢复 / 注意 / 异常 / 待证观察）→ 未恢复/已恢复列表 → 待证观察 → 健康态；复用 `StatusPresenter`。
2. **执行 `/runs` + `/runs/{id}`**：运行列表沿用新壳；详情按「运行头 → 判定摘要 + meter → 检查项表 → 证据折叠 → 快照折叠 → 观察」重排；统一 `NameOrMissing` / `FormatDuration` / `TriggerText`。
3. **报告 `/reports`**：日报 / 周报；`AiSummary(DailyReport)` 只显示标题、字数、版本、注入状态，不铺全文。
4. **体系 / 设置**：新增体系落地页（手册 / 检查单 / 项目环境 / MCP），再把 `DesignLayout` 的「体系」从 `/runbooks` 指向落地页。
5. **壳收口**：迁移完成后把 `design-system.css` 的 `.ds` 作用域去掉、删 `MainLayout` 旧样式；`app.css` 里旧页面样式按页删除。
6. **上下文跨页**：新壳的 `?workspace=` 已能让总览跟随环境；旧页迁移时再接入 `WorkspaceContextService`，不要在旧页里复制解析逻辑。

## 4. 验收（继续沿用）

- `/` 与 `/styleguide` 200；旧路由全部可访问。
- `dotnet build GrowthInspector.slnx` 0 warning / 0 error。
- `dotnet test tests/GrowthInspector.Tests` 全绿（当前 160）。
- 1440 / 1280 / 390；浅/深；无页面横向溢出（表格内部滚动除外）。
- 页面里没有硬编码 hex；状态只走 `StatusPresenter`；`Error / Mismatch / 证据不足` 不出现朱红；`Succeeded` 不写「成功」。
- 主题切换记忆；`lang="zh-CN"`；`th scope`；滚动表格区 `tabindex="0"`。

## 5. 启动

```bash
cd /home/muomu/Inspector
dotnet build GrowthInspector.slnx
dotnet run --project src/GrowthInspector.Web    # http://127.0.0.1:5273
```

管理 MCP token 可选；不配也可以跑页面。旧进程可能还占着 5273，先确认端口。

静态样张：`docs/visual-style/index.html`；如需本地服务：
```bash
cd docs/visual-style && python3 -m http.server 8088
```

## 6. 已知风险

- **真实库当前 10/10 对象待证**：最近一轮检查项全部 `Error`（未配置 `DEEPSEEK_API_KEY`），总览按待证展示，未用假数据粉饰；另有 8 项流水线检查待证。
- **部分执行 / 历史快照**：节点集合与严重级以快照为准；快照缺失或损坏时回退当前检查项，但仍标记「结果不完整」，不会当作全清。
- **两套壳并存**：旧页面仍用 `MainLayout`（旧导航/旧样式）；从新壳点进旧页会换壳，这是迁移期的已知体验断层。
- **「体系」暂指 `/runbooks`**：体系落地页还没做；设置页仍承载项目/环境/MCP 的部分管理。
- **上下文只在总览生效**：`?workspace=` 会被新壳保留到导航，但旧页面不消费；迁移旧页时补。
- 设计系统字体约 3.7MB；本地可全量，远端再子集化。
- 演示叙事、Grafana MCP、日报内容不在本步范围。
