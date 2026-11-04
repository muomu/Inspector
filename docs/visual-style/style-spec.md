# 视觉风格定义 v0.2 · 丝绸灰 / 玄色 · 烟墨 / 月白

> 版面：C 留白（已选），1120 宽、56 gutter、48 行高、88 章节间距；总览改为“集群状态优先”。
> 状态：草案。已用 9 张静态样张验证浅色与深色（1440 全页；1280 / 390 响应式抽查）；
> **已落到 Blazor**（`/styleguide` 与 `/` 总览，见 `NEXT.md`）；版面 A/B/C 已选 C；Grafana MCP 接入未验证。
>
> 主色调由用户指定：背景「丝绸灰 / 烟墨」，前景「玄色 / 月白」。
> 本文件给出具体色值、对比度与落地规则；样张是唯一的视觉事实。

## 0. 先定三个词

| 词 | 含义 | 不要混用 |
|---|---|---|
| **判定** | 单个检查项的结果：Ok / Abnormal / Error / InsufficientEvidence | 不要叫「状态」，不要和 RunStatus 混 |
| **生命周期** | 一次执行或一个 Finding 的进度：Pending / Running / Succeeded / Failed / Canceled / Skipped；Open / Resolved | `Succeeded` 只表示「跑完了」，**不等于健康** |
| **严重级** | Info / Warning / Critical | Warning 显示为「注意」，Critical 显示为「异常」 |

---

## 1. 设计原则

1. **结论先行**：首屏先给「今天怎么样」，再给异常、执行记录、方法。
2. **每条结论都有证据**：判定旁边必须能打开 ToolCallLog / 重算值 / 规则版本。
3. **颜色是二次编码**：形状 + 图标 + 文字先表达，颜色只做强化；朱红只给异常。
4. **中性底 + 单一强调色 + 独立语义色**：墨青只做品牌/交互，状态色不借品牌色。
5. **阅读宽度与数据宽度分开**：正文 1200/1120，表格允许横向滚动。
6. **用规则线，不用卡片墙**：2px 主规则线、1px 分隔线、2px 圆角；不用阴影堆层级。
7. **中文优先**：行高、字号、标点、数字都按 CJK 调；`lang="zh-CN"`。
8. **浅色默认、深色可切换**：两套 token 都要过对比度；不默认深色。

---

## 2. 色彩

### 2.1 浅色 · 丝绸灰 / 玄色

```css
:root {
  color-scheme: light;
  --paper:        #E6E7E4;  /* 丝绸灰 · 页面底 */
  --paper-raised: #EFEFEC;  /* 面板 / hover */
  --paper-sunken: #E2E3DE;  /* 证据块 / 代码块 */
  --ink:          #1A1A1A;  /* 玄色 · 主文字 */
  --ink-soft:     #3A3A3A;
  --muted:        #5B6670;
  --line:         #CFD0CB;
  --line-strong:  #7B7D76;
  --accent:       #14535A;  /* 墨青 */
  --accent-ink:   #0E3F45;
  --accent-soft:  #D9E5E4;
  --danger:       #B23A2F;  --danger-soft:  #F2DEDA;
  --warning:      #8A5A00;  --warning-soft: #EFE4C9;
  --ok:           #2E6B45;  --ok-soft:      #DDE9E0;
  --idle-soft:    #DCDDD8;
  --focus:        #0F6B63;
}
```

### 2.2 深色 · 烟墨 / 月白

```css
[data-theme="dark"] {
  color-scheme: dark;
  --paper:        #242B36;  /* 烟墨（深一档，保证对比度） */
  --paper-raised: #2C3441;
  --paper-sunken: #1C222C;
  --ink:          #D6ECF0;  /* 月白 · 主文字 */
  --ink-soft:     #C3D9DE;
  --muted:        #A7B3B8;
  --line:         #3B475B;  /* 烟墨 · 分隔线 */
  --line-strong:  #6F7D93;
  --accent:       #8FC3C3;
  --accent-ink:   #B7DADA;
  --accent-soft:  #263B3E;
  --danger:       #E08A80;  --danger-soft:  #3A2528;
  --warning:      #D6B06A;  --warning-soft: #3A3020;
  --ok:           #8EC3A0;  --ok-soft:      #1F2C24;
  --idle-soft:    #2A303A;
  --focus:        #9FD3D3;
}
```

> 命名解释：`烟墨 #3B475B` 是深色族的「本色」，直接作分隔线与次级面板；
> 页面底取同族更深一档 `#242B36`，否则朱红/次要文字过不了 4.5:1。
> 这是对色名的工程化处理，不是改色名。
>
> 丝绸灰同理：取浅色侧 `#E6E7E4`。DULUX 30BB 62/004 的 `#CBCDCC` 作为页面底时，
> 朱红 / 琥珀 / 次要文字只有 3.7–4.0:1；若坚持用漆卡值，必须同步加深语义色。

### 2.3 用法

| Token | 用途 | 禁止 |
|---|---|---|
| `--accent` | 链接、选中、focus、品牌标记、运行中 | 不当状态色；不铺大面积底 |
| `--danger` | Abnormal / Critical、失败生命周期 | 不给 Error / Mismatch / 待证 |
| `--warning` | Warning（注意） | 不当品牌色；不与赭石/橙色混用 |
| `--ok` | Ok、已恢复 | 不给 `Succeeded`（它只是完成） |
| `--muted` | 次要文字、元数据 | 不用于 12px 以下正文 |
| `--paper-*` | 背景层级 | 不用纯白/纯黑做大面积底 |

### 2.4 对比度

已计算的核心文本对：

| 前景 / 背景 | 浅色 | 深色 |
|---|---|---|
| 主文字 / 页面底 | 玄 / 丝绸灰 **14.0:1** | 月白 / 烟墨 **11.6:1** |
| 墨青 / 页面底 | **7.0:1** | **7.3:1** |
| 朱红 / 页面底 | **4.8:1** | **5.5:1** |
| 琥珀 / 页面底 | **4.8:1** | **7.0:1** |
| 通过绿 / 页面底 | **5.1:1** | **7.1:1** |
| 次要文字 / 页面底 | **4.7:1** | **6.6:1** |

浅色软底：danger-soft 4.6:1、warning-soft 4.7:1、ok-soft 5.1:1、accent-soft 6.7:1。
深色软底：danger-soft 5.5:1、warning-soft 6.3:1、ok-soft 6.8:1、accent-soft 6.1:1。

> 实现时在 `/styleguide` 里对每个 badge/软底配对重新算；不能只信这张表。
> `--line` 只用于装饰性分隔；可交互控件用 `--line-strong`（浅色 3.4:1、深色 3.0–3.4:1）。

---

## 3. 字体

### 3.1 字体族

| 角色 | 字体 | 字重 | 许可 | 备注 |
|---|---|---|---|---|
| 标题 / 报告题 | Noto Serif SC | 400 | OFL | 只用于 h1/h2/报告题/大数字；≥20px |
| 正文 / UI | Noto Sans SC | 400 / 500 | OFL | 正文 15/24；表格 13.5/20 |
| 数字 / ID | IBM Plex Mono | 400 / 500 | OFL | IDs、时间、指标、金额；`tabular-nums` |

回退：`PingFang SC` → `Microsoft YaHei` → `WenQuanYi Zen Hei` → sans-serif。
中文正文不整站换衬线；衬线只做标题。字体许可全文在 `assets/fonts/LICENSE-*.txt`。

### 3.2 字号与行高

| 角色 | 字号 / 行高 | 用途 |
|---|---|---|
| Display | 44–48 / 1.18 | 总览页主标题（A 42 / C 46；集群题 48） |
| H2 | 22 / 1.24 | 章节标题 |
| H3 | 18–19 / 1.35 | Finding 标题、callout 标题 |
| Body | 15 / 24 | 正文（B 14.5；C 16 / 28.8） |
| Small | 13 / 20 | 表格、辅助正文 |
| Meta | 11.5 / 17 | 时间、项目、规则版本 |
| Micro | 9.5–10.5 / 1.4 | 小标签、表头、kicker；不用作正文 |

- 中文行高比西文 +0.1em（Material 的 CJK 口径）；标题 1.24，正文 1.6–1.72。
- 数字统一 `font-variant-numeric: tabular-nums`；表格右对齐。
- 强调用颜色/字重，不用斜体；`em` 在报告题里只用于朱红强调，且 `nowrap` 保护语义块。
- 12px 以下只放标签，不放正文。

### 3.3 加载

- 自托管 woff2，`font-display: swap`；样张内嵌 5 个文件（约 3.8MB），正式实现按需子集化。
- 远端部署时按 unicode-range 切片；本地工具可以全量。
- 不用 MiSans / HarmonyOS Sans 作基座（许可可撤销，子集化边界不清）。

---

## 4. 版面

### 4.1 三版

| 变量 | A 报告栏 | B 全宽 | C 留白（已选） |
|---|---|---|---|
| `--content` | 1200px | none | 1120px |
| `--gutter` | 32px | 24px | 56px |
| `--row` | 40px | 36px | 48px |
| `--section-gap` | 48px | 36px | 88px |
| `--prose-size` | 15px | 14.5px | 16px |
| 表格 | 同栏宽，横向滚动兜底 | 边到边 | 同栏宽，更窄 |
| 适合 | 默认候选 | 宽表 / 高密度 | 已选：集群状态优先 / 演示 |

三版只改 `body[data-layout]` 下的变量，不改结构。正式实现只保留选中一版。
主色调与版面正交：A/B/C 都共用浅色/深色 token。

### 4.2 间距与栅格

- 间距阶：4 / 8 / 12 / 16 / 20 / 24 / 32 / 40 / 48 / 64。
- 页头：上 40、下 24；底部 2px `--ink` 规则线。
- 章节：`§ 01` 编号 + 22px 宋体标题 + 1px 规则线 + 右侧 mono 元数据。
- 页面结构以纵向流为主；局部 2/4 列（集群网格 3/4 列、异常页 KPI 4 列、Finding 92/1fr/180）。
- <1040：导航换行、页头竖排；<960：集群网格 2 列、KPI/Finding 单列；<720：单列 + 20px gutter。

### 4.3 表格

- 表头 9.5px mono、字距 0.08em、`--line-strong` 下边线；行高 40/36/44，`--line` 分隔；不加斑马纹。
- 数字列右对齐、mono、`tabular-nums`；状态列不右对齐。
- 宽表用 `.table-wrap { overflow-x: auto }`；正式实现给滚动区 `tabindex="0"` + `role="region"` + 可读 label。
- 表格可以比正文宽（B 版边到边；A/C 同栏宽 + 滚动）。

### 4.4 背景纹样

- 页面底：16px 经纬线 + 8px 斜纹（45°），浅色用玄色线、深色用月白线，alpha 0.025–0.03。
- 目的：让丝绸灰/烟墨有织物质感，同时保留“巡检记录纸”的经纬感；不是装饰插画。
- 只铺 `body`；内容区是一张 `paper-raised` 的「报告纸」，左右 1px 线；正文、表格、证据块都不叠纹样。
- 页面背景（有纹样）与内容纸（无纹样）形成前景/背景对比；页头、页脚保持不透明。
- 打印时 `body` 回到纯白，纹样关闭。
- 实现：
  ```css
  body {
    background-color: var(--paper);
    background-image:
      repeating-linear-gradient(0deg, var(--pattern-line) 0 1px, transparent 1px 16px),
      repeating-linear-gradient(90deg, var(--pattern-line) 0 1px, transparent 1px 16px),
      repeating-linear-gradient(45deg, var(--pattern-diag) 0 1px, transparent 1px 8px);
  }
  ```

### 4.5 形状

- 圆角 2px（按钮/输入/证据块 3px）；不用大圆角卡片。
- 阴影只给原型切换条；页面元素用 1px 线 + 色阶层级。
- 主按钮墨青实底、对应主题的纸色字；次按钮透明底 + 1px 线；安静按钮只留文字。

---

## 5. 状态语义

### 5.1 五态 + Running

| 视觉态 | 记号 | 浅色 | 深色 | 语义 | 典型枚举 |
|---|---|---|---|---|---|
| Ok | ✓ | `#2E6B45` | `#8EC3A0` | 断言成立且复核一致 | `CheckResultStatus.Ok`、`FrameworkVerdictStatus.Ok` |
| 注意 | ▲ | `#8A5A00` | `#D6B06A` | Warning 级异常 | `Abnormal` + `Severity.Warning` |
| 异常 | ■ | `#B23A2F` | `#E08A80` | Critical / Abnormal 级异常 | `Abnormal` + `Severity.Critical` |
| 待证 | ◇ | `--muted` 空心 | `--muted` 空心 | 证据不足 / 复核不一致，**不是故障** | `Error`、`Mismatch`、`InsufficientEvidence`（断言型） |
| 中性 | — | `--muted` | `--muted` | 不参与判定 / 未执行 / 信息型 | `Pending`、`Skipped`、信息型 `InsufficientEvidence` |
| 运行中 | ◉ | `--accent` 脉冲 | `--accent` 脉冲 | 正在执行 | `RunStatus.Running` |

### 5.2 枚举映射

| 枚举 | 值 | 视觉态 / 文案 |
|---|---|---|
| `RunStatus` | Pending | 中性「等待中」 |
| | Running | 运行中「执行中」 |
| | Succeeded | 中性「完成」——**不写“成功”** |
| | Failed | 失败（danger） |
| | Canceled | 中性「已取消」 |
| | Skipped | 中性「已跳过」 |
| `CheckResultStatus` | Ok | Ok |
| | Abnormal | 按 Severity：Warning → 注意；Critical → 异常；Info → 中性「Abnormal · Info」 |
| | InsufficientEvidence | 断言型 → 待证；信息型 → 中性「信息」 |
| | Error | 待证 |
| `VerdictStatus` | Ok / Abnormal / InsufficientEvidence | Ok / 注意或异常 / 待证 |
| `FrameworkVerdictStatus` | Ok / Abnormal | Ok / 注意或异常 |
| | InsufficientEvidence / Error / Mismatch | 待证；Mismatch 子标签「复核不一致」 |
| `FindingStatus` | Open | chip「未恢复」 |
| | Resolved | chip「已恢复」 |
| `FindingCloseReason` | Recovered | 子标签「检查恢复」 |
| | RuleChanged | 子标签「判据变更」 |
| `Severity` | Info / Warning / Critical | 中性·信息 / 注意 / 异常 |

### 5.3 硬规则

1. **`Succeeded` ≠ 健康**：执行完成只说明跑完了；健康由检查项判定和 Finding 决定。
2. **`Error` ≠ 故障**：Error / Mismatch / 证据不足统一为「待证」，空心、低饱和；不建 Finding、不发 Lark，只进日报观察。
3. **信息型不报警**：信息型检查项的 `InsufficientEvidence` 是正常结果，显示中性「信息」。
4. **颜色三通道**：任何状态都必须同时有颜色 + 字形 + 文字；字形 `aria-hidden`，文字可读。
5. **单一 helper**：`StatusPresenter` 输入 `(resultStatus, severity, itemKind, findingStatus, runStatus)`，输出 `{state, label, glyph, sub, ariaLabel}`；禁止页面里复制 switch，禁止一个 class 承担多种语义。
6. **严重级与视觉态分离**：左侧可保留 `CRITICAL/WARNING` 作为严重级标签，状态列使用五态。
7. **Running 动效**：`prefers-reduced-motion: reduce` 时停用脉冲。
8. **两套主题同一语义**：浅/深只改色值，不新增状态、不改文案。

---

## 6. 信息架构

### 6.1 一级导航

```
总览 / 异常 / 执行 / 报告        体系 / 设置
```

- **总览**：集群状态（8 / 10）、服务/依赖网格、需要处理、最近执行；判定方法为页头链接。
- **异常**：筛选、摘要、未恢复/已恢复、待证观察、健康态。
- **执行**：运行列表 → 运行详情（判定、证据、快照、观察）。
- **报告**：日报 / 周报（样张未覆盖，正式实现补）。
- **体系**：项目/环境、手册、检查单/检查项、MCP server。
- **设置**：凭据、调度、保留期等后台配置；不放在总览。

### 6.2 顶栏上下文

- 项目 + 环境选择器 + 「立即巡检」+ 主题切换（深色/浅色）。
- 切项目/环境后，总览、异常、执行、报告全部跟随；「体系」内的配置也按项目过滤。
- 默认项目/环境来自上次选择或项目默认环境；不做隐式“全部环境”。

### 6.3 页面模板

| 页面 | 首屏 | 下方 |
|---|---|---|
| 总览 | 集群 8/10 一句结论 + 服务/依赖网格 | 需要处理 → 最近执行 → 判定方法（链接） |
| 异常 | 标题 + 筛选 + 4 个 KPI | 未恢复/已恢复列表 → 待证观察 → 健康态 |
| 执行详情 | 面包屑 + 运行头（完成/有异常） | 判定摘要 + meter → 检查项表 → 证据折叠 → 快照折叠 → 观察 |

### 6.4 详情与折叠

- Run 详情默认折叠：证据、快照/知识注入、AI 摘要、Lark 留痕。
- 不把整本手册、AI 摘要全文铺进 `<pre>`；只显示标题、字数、版本、注入状态。
- Finding、ChecklistItem 需要独立详情路由（当前只有内联）。
- 跨页复制函数收敛：`NameOrMissing`、`FormatDuration`、`TriggerText`、`StatusPresenter` 各一份。

---

## 7. 组件规则

| 组件 | 规则 |
|---|---|
| 表格 | 见 §4.3；表头 uppercase mono；数字右对齐；无斑马纹 |
| 状态 | 五态 + Running；字形 + 文字；子标签 mono |
| Finding 行 | 严重级左规则线 + 19px 宋体标题 + mono 元数据 + 右侧 chip/链接 |
| 证据 | `<details>`，summary 用 +/−；正文 `--paper-sunken` + 左侧 accent 2px；定义列表 + 代码块 |
| Callout | 1px 线 + paper-raised；引用型左侧 2px accent |
| 空态 | 1px 虚线；健康态用 `--ok-soft` + 绿字 + 一句可读结论 |
| 筛选 | 顶部横向；label mono；右侧筛选/重置；不做抽屉 |
| 按钮 | 主：accent 实底 + 对应主题的纸色字；次：1px 线；安静：纯文字；高度 34px |
| 选择器 | 原生 `<select>` 样式化；不造自定义 combobox |
| 主题切换 | 顶栏右侧「深色/浅色」文字按钮；`aria-pressed`；localStorage 记忆；URL `?theme=dark` 可分享 |

---

## 8. 无障碍

- 对比度：正文 4.5:1、非文本 3:1、指针目标 24×24（WCAG 2.2 AA）。
- `lang="zh-CN"`；所有 `<th scope>`；`<caption class="sr-only">`；跳过链接；focus 可见（不用 `outline:none` 吞焦点）。
- 滚动表格区 `tabindex="0"` + `role="region"` + `aria-label`；键盘可横向滚动。
- 原生 `<table>/<details>/<select>/<input type=date>`；不自绘下拉/日期面板。
- 颜色不作为唯一手段；状态有字形 + 文字。
- 200% 缩放不丢内容；正文不被 `overflow:hidden` 裁切。
- `prefers-reduced-motion` 停用脉冲；打印隐藏导航/操作/切换条。
- 深浅主题只换 token；`color-scheme` 跟随 `data-theme`，原生控件不出现“白底黑字”的割裂。

---

## 9. 深色主题

- 已随样张实现：顶栏「深色」按钮切换，localStorage 记忆；`?theme=dark` 可直接分享深色预览。
- 两套 token 都在 `tokens.css`；页面里禁止写死颜色。
- 深色底取烟墨同族更深一档 `#242B36`；`#3B475B` 作分隔线与次级面板；月白 `#D6ECF0` 作主文字。
- 深色下状态色整体提亮：朱红 `#E08A80`、琥珀 `#D6B06A`、通过绿 `#8EC3A0`、墨青 `#8FC3C3`。
- 深色表格 hover / selected / evidence 背景全部走 token；不出现纯白/纯黑块。
- 后续可加 `prefers-color-scheme` 双轨；本期默认浅色，手动切换。
- 正式实现要在 `<head>` 内联读取 localStorage / URL 并先设 `data-theme`，避免首屏闪白；样张用的是 `defer` 脚本，允许一闪。

---

## 10. 验收清单（全站迁移完成后逐项复检；`/` 与 `/styleguide` 已过）

- [ ] `/styleguide` 展示浅/深两套色板、字体、五态、表格、表单、空态、证据块；每个软底配对显示对比度。
- [ ] 1440 / 1280 / 1024 / 390 截图；浅色与深色都无页面横向溢出（表格内部滚动除外）。
- [ ] 200% 缩放不丢内容；键盘可走完筛选、表格滚动、证据展开、主题切换。
- [ ] 全站 `lang="zh-CN"`；`th` 全带 `scope`；无 `outline:none`。
- [ ] 页面/组件里没有硬编码 hex；颜色只从 token 取。
- [ ] RunStatus 不再显示“成功”；Error 不再用朱红；信息型不显示“待证”。
- [ ] `StatusPresenter` 单元测试覆盖 6 个枚举；无跨页复制 switch。
- [ ] 打印样式：隐藏导航/操作/切换条，正文黑白可读。
- [ ] `dotnet build` 0 warning / 0 error；无新增前端依赖。
