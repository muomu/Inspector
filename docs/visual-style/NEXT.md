# 交接 · 全站迁移完成（C 留白）

> 新会话开场读本文件，再读 `docs/visual-style/style-spec.md`、`docs/visual-style/page-patterns.md`、
> `design-system.css`、`DesignLayout.razor`、`Home.razor`、`Systems.razor`。
>
> **交接状态（2026-11-04）**：所有页面已迁到 `DesignLayout` + C 留白，旧 `MainLayout` / `NavMenu` / `app.css` 页面样式已删除。
> 基线：`dotnet build` 0 warning / 0 error；`dotnet test` **186 passed**；全部路由 200；1440/1280/1024/390 × 浅/深无页面横向溢出。

## 1. 已冻结的设计

- **版面 C 留白**：1120 宽 / 56 gutter / 48 行高 / 88 章节间距 / 16px 正文 / 46px 标题。
- **浅色**：丝绸灰 `#E6E7E4` + 玄色 `#1A1A1A` + 墨青 `#14535A`；朱红 `#B23A2F` 只给异常。
- **深色**：烟墨 `#242B36`（本色 `#3B475B` 作线/面板）+ 月白 `#D6ECF0` + 墨青 `#8FC3C3`。
- **背景纹样**：16px 经纬 + 8px 斜纹，约 3%，只在页面页边；内容区是一张 `paper-raised` 的「报告纸」，左右 1px 线。
- **状态**：Ok / 注意 / 异常 / 待证 / 中性 + Running；`Error / Mismatch / 证据不足 = 待证`；`Succeeded = 完成`，不是健康。
- **IA**：结果优先 + 配置分离；总览/异常/执行/报告 + 体系/设置；项目/环境顶栏上下文。
- **载体**：手写 CSS + token + 原生控件，零新增依赖。

## 2. 已完成

### 壳与设计系统

- `DesignLayout`：项目/环境 GET 切换、主题切换、跳过正文、`总览 / 异常 / 执行 / 报告 / 体系 / 设置` 六个一级入口；
  导航高亮覆盖子路由（`/runs/{id}` 仍高亮「执行」；`/runbooks`、`/checklists`、`/systems` 都算「体系」）。
- `design-system.css` 全站生效：token 挂 `:root`，`body` 铺页面底纹，`.app` 纵向铺满，`.ds-shell` 是内容纸；
  深色走 `html[data-theme="dark"]`。页面级模式（KPI / toolbar / table / result / report / entry / item-block）已沉淀，
  页面里没有行内颜色与 hex（`/styleguide` 的色板样张除外）。
- 旧 `MainLayout.razor`、`MainLayout.razor.css`、`NavMenu.razor` 删除；`app.css` 只留框架级样式（校验、`#blazor-error-ui`）。
- `Routes.razor` 默认布局改为 `DesignLayout`；`NotFound` / `Error` 页也迁到新壳。

### 页面

| 路由 | 结构 |
|---|---|
| `/` | 集群结论 → 服务/依赖网格 → 需要处理 → 最近执行；节点以执行快照期望项为准，缺失结果「待证 · 无结果」。 |
| `/findings` | 页头 → 筛选（状态/级别，保留 `?status=&severity=`）→ 4 KPI（未恢复 / 注意 / 异常 / 待证观察）→ `.finding` 列表 → 待证观察表 → 健康态；按当前环境过滤。 |
| `/runs` | 页头 → 筛选（检查单/状态/起止日期）→ 宽表（时间/检查单+环境/状态/耗时/Token/触发）；按当前环境过滤。 |
| `/runs/{id}` | 面包屑 → 页头 → 运行头（五态计数 + meter）→ 检查项 `details.result` → 工具调用 → 观察 → 执行快照（折叠）→ 通知/摘要（折叠）。 |
| `/reports` | 页头 + 生成今日日报 → 日报卡（元数据/兜底 chip/6 项 KPI/Lark 状态/正文折叠）→ 最近发送留痕表；日报为全局口径。 |
| `/systems` | **新增**体系落地页：知识资产入口 → 检查单与频率 → MCP server → 项目与环境；按当前项目过滤。 |
| `/runbooks`、`/runbooks/{id}` | 手册列表（归档筛选 + 新建）与 Markdown 编辑页（正文等宽、保存/归档）；按当前项目过滤。 |
| `/checklists`、`/checklists/{id}` | 检查单列表（归档筛选 + 新建）与编辑页（基本信息 / 检查项 item-block / 引用手册 / 立即运行）；按当前项目过滤。 |
| `/settings` | 项目 / 环境 / MCP server 三段：表格 + 新建 panel + 条件式编辑 panel；掩码与只写不读语义保留。 |
| `/styleguide` | 视觉规范冻结稿（含色板样张的 hex，是唯一例外）。 |

### 收敛的共享逻辑

- `StatusPresenter`（`Services/StatusPresenter.cs`）：唯一状态映射，页面只消费 `StatusPresentation`。
- `DisplayText`（`Services/DisplayText.cs`）：`NameOrMissing / Shorten / FormatDuration / TriggerText / FrequencyText / RunbookKindText / CloseReasonText / OrDash / ArchivedLabel`。
- `RunVerdictBuilder`（`Services/RunVerdictBuilder.cs`）：执行五态计数 + meter 文案 / 宽度。
- 测试：`DisplayTextTests`、`RunVerdictBuilderTests`、`StatusPresenterTests`、`ClusterOverviewBuilderTests`。

## 3. 验收结果（2026-11-04）

- `dotnet build GrowthInspector.slnx`：**0 warning / 0 error**（顺手修掉 `ClusterOverviewBuilderTests` 的 xUnit2031 警告）。
- `dotnet test tests/GrowthInspector.Tests`：**186 passed**（原 160 + DisplayText / RunVerdict 26）。
- 路由：`/`、`/styleguide`、`/findings`、`/runs`、`/runs/{id}`、`/reports`、`/settings`、`/systems`、`/runbooks`、`/runbooks/{id}`、`/checklists`、`/checklists/{id}`、`/not-found`、`/Error` 全部 200；未知路由 404 走 `NotFound`。
- 横向溢出：14 条路由 × 1440/1280/1024/390 共 56 组，`documentElement.scrollWidth - clientWidth` 全为 **0**（宽表只在 `.table-wrap` 内滚动）。
- 交互：增强导航保留主题与上下文；`?theme=dark` 首屏即深色；主题切换写 localStorage 并同步 `aria-pressed`；
  GET 筛选保留 `?workspace=`；`details.result` 可展开；`/runbooks`、`/settings`、`/checklists` 的 InteractiveServer 交互正常。
- 结构：全站 `lang="zh-CN"`；所有 `<th>` 带 `scope`；表格滚动区 `role="region" tabindex="0" aria-label`；无旧壳 class（`sidebar`、`nav-menu`、`app-shell`）。
- 截图（`docs/visual-style/screenshots/`）：
  - `app-home-full.png` / `app-home-dark-full.png` / `app-home-1280.png` / `app-home-390.png` / `app-home-dark-390.png`
  - `app-findings-full.png` / `app-findings-dark-full.png` / `app-findings-1280.png` / `app-findings-1024.png` / `app-findings-390.png`
  - `app-runs-full.png` / `app-runs-dark-full.png` / `app-runs-1024.png` / `app-runs-390.png`
  - `app-run-detail-full.png` / `app-run-detail-dark-full.png` / `app-run-detail-1024.png` / `app-run-detail-390.png`
  - `app-reports-full.png` / `app-reports-390.png`
  - `app-settings-full.png` / `app-settings-dark-full.png` / `app-settings-390.png`
  - `app-systems-full.png` / `app-systems-dark-full.png` / `app-systems-390.png`
  - `app-runbooks-full.png` / `app-runbook-edit-full.png` / `app-checklists-full.png` / `app-checklist-edit-full.png` / `app-checklist-edit-390.png`

## 4. 已知风险 / 未做

- **真实库当前仍以「待证」为主**：最近一轮检查项全部 `Error`（未配置 `DEEPSEEK_API_KEY`），页面按待证展示，未用假数据粉饰；另有 8 项流水线检查待证。
- **宽表在窄屏只在 `.table-wrap` 内横向滚动**（表格内部滚动，页面不横向溢出）；没有做卡片化的移动端表格。
- **顶栏在 390 宽度占 4 行**（品牌 / 上下文 / 操作 / 导航），可用但偏长；未做折叠菜单。
- **`/reports` HTML 体积较大**（日报正文与 5 条通知报文都折叠在 `<details>` 里，仍随 SSR 下发，约 600KB）；如需进一步减重可改成按需加载正文。
- **主题只走手动切换**，未接 `prefers-color-scheme`；字体约 3.7MB，远端部署需要子集化。
- **日报是全局口径**（`AiSummary(DailyReport)` 无项目/环境字段），`/reports` 不跟随顶栏上下文，页内已明示。
- **演示叙事、Grafana MCP、真实 Lark / DeepSeek key 不在本步范围。**

## 5. 启动

```bash
cd /home/muomu/Inspector
dotnet build GrowthInspector.slnx
dotnet test tests/GrowthInspector.Tests
dotnet run --project src/GrowthInspector.Web    # http://127.0.0.1:5273
```

管理 MCP token 可选；不配也可以跑页面。旧进程可能还占着 5273，先确认端口。

静态样张：`docs/visual-style/index.html`；如需本地服务：

```bash
cd docs/visual-style && python3 -m http.server 8088
```

## 6. 后续可做

1. 移动端顶栏收敛（上下文 / 操作进「更多」），宽表可考虑「关键列 + 展开行」。
2. `/reports` 正文按需加载；`/runs` 列表分页 / 无限滚动（现在 200 条封顶）。
3. `prefers-color-scheme` 双轨；字体子集化。
4. Finding / ChecklistItem 独立详情路由（目前异常页与执行详情内联展示）。
5. 真实 DeepSeek key 与 Lark webhook 接入后，复核五态在真实数据下的表现。
