# Growth Inspector · 视觉风格样张 v0.2

这是「前端重做」的第一刀：**3 种版面 × 3 个关键页**，纯静态 HTML，不改应用代码。
目的不是定稿，而是先把版面（宽度、行高、章节间距）和状态语义拍下来。

## 打开

直接双击 `docs/visual-style/index.html`，或起一个本地静态服务：

```bash
cd docs/visual-style
python3 -m http.server 8088
# 打开 http://127.0.0.1:8088/
```

页面里的筛选、下拉、主按钮是样张，不提交；执行详情里的 `<details>` 证据折叠和右上角「深色 / 浅色」切换是真实可用的。
右下角「版面 A/B/C」是原型切换器，不属于设计本身。

## 目录

```
docs/visual-style/
  index.html              三版对比 + 色板 / 字体 / 状态图例
  compare.html            A/B/C × 总览 / 执行详情并排对比
  build.py                生成 9 个静态页 + index
  style-spec.md           视觉风格定义 v0.2（token / 类型 / 状态 / IA / 深浅主题）
  assets/
    tokens.css            primitive + semantic token，字体 @font-face
    base.css              版面、导航、页头、章节、页脚、三版变量
    components.css        按钮、状态、表格、证据、Finding、空态
    theme.js             浅色/深色切换 + localStorage + ?theme=dark
    fonts/                Noto Serif SC / Noto Sans SC / IBM Plex Mono（OFL）
  v1/ v2/ v3/             A / B / C 三种版面
    overview.html         总览
    run.html              执行详情
    findings.html         异常
  screenshots/            浅色/深色 1440 全页；1280/390 响应式（C 已选）
```

## 已定方向

- **版面**：C 留白（已选）；1120 宽、48 行高、88 章节间距，总览改为“集群状态优先”。
- **背景纹样**：16px 经纬 + 8px 斜纹，约 3% 不透明度；深色用月白线；只铺页面底，打印关闭。
- **母题**：诊断报告。丝绸灰底、玄色字、宋体标题、无衬线正文、等宽数字；深色为烟墨底、月白字。
- **色**：浅色丝绸灰 `#E6E7E4` + 玄色 `#1A1A1A` + 墨青 `#14535A`；深色烟墨 `#242B36` + 月白 `#D6ECF0` + 墨青 `#8FC3C3`；朱红只给 Abnormal / Critical。
- **色名与色值**：丝绸灰取浅色侧 `#E6E7E4`（漆卡 `#CBCDCC` 作底会让朱红 / 琥珀 / 次要文字掉到 3.7–4.0:1，需同步加深状态色）；烟墨本色 `#3B475B` 作分隔线 / 次级面板，页面底取同族 `#242B36`；玄色 `#1A1A1A`、月白 `#D6ECF0`。
- **状态**：Ok / 注意 / 异常 / 待证 / 中性 五态 + Running 脉冲；Error / Mismatch / 证据不足统一为「待证」，不是故障。
- **IA**：结果优先 + 配置分离；总览 / 异常 / 执行 / 报告为主轴，手册 / 检查单 / MCP 收进「体系」；项目 + 环境做顶栏上下文。
- **载体**：手写 CSS + token + 原生 HTML 控件，零新依赖。
- **主题**：浅色默认，深色可切换（顶栏「深色 / 浅色」或 URL `?theme=dark`）；两套 token 都过对比度。

## 三版差异

| 版 | 宽度 | 行高 | 章节间距 | 适合 |
|---|---|---|---|---|
| A 报告栏 | 1200 | 40px | 48px | 最像报告；1440/1280 下宽表可放下，≤1024 窄屏横向滚动 |
| B 全宽 | 100% | 36px | 36px | 信息密度最高；首屏能放下更多行，长文阅读偏累 |
| C 留白（已选） | 1120 | 48px | 88px | 更克制、密度更低；宽表和长页面会更长 |

三版共用同一套组件与状态语义，只改 `body[data-layout]` 下的 CSS 变量；正式实现时只保留选中的一版。

## 评审清单

- [ ] 首屏结论是否立得住：一眼看到「1 项异常 / 1 项注意 / 1 项待证」，而不是一堆徽章。
- [ ] 1440 与 1280 下，总览的集群网格 / 最近执行列表是否一眼可读；哪一版可接受。
- [ ] 执行详情：12,525px 的旧页面被压到约 2,100px；证据折叠、快照折叠是否够用。
- [ ] 状态是否一眼可分：Ok 低饱和、注意琥珀、异常朱红、待证空心、Running 脉冲；不看颜色也能靠符号和文字读懂。
- [ ] 深色下是否仍然成立：烟墨底、月白字、状态色提亮后仍能过 4.5:1，且表格 hover / selected / 证据块没有纯白块。
- [ ] 「待证」是否被误读成故障：它出现在详情和日报观察里，不进 Finding、不发 Lark。
- [ ] 健康态是否可读：异常页底部的「本次巡检未发现异常」空态，用于生产无故障时的演示。
- [ ] 中文字体与行高是否舒服：标题宋体、正文无衬线、数字等宽；14px 以下正文不使用。
- [ ] 背景纹样是否过密/过淡：网格 + 斜纹在 1440 与 390 下是否只添质感、不抢正文。
- [ ] 390 宽：导航折行、集群网格单列、宽表横向滚动，是否可接受。

## 字体与许可

- **Noto Sans SC / Noto Serif SC**：SIL Open Font License 1.1，上游 `notofonts/noto-cjk`；woff2 取自 Fontsource 构建。
- **IBM Plex Mono**：SIL Open Font License 1.1，Reserved Font Name "Plex"，上游 `IBM/plex`。
- 许可全文在 `assets/fonts/LICENSE-*.txt`；子集化/再分发时保留许可文本，衍生字体不要沿用保留名。

## 已知限制

- 数据是样张数据，不是真实巡检结果；指标名与 Grafana 真实数据源对齐，但数值是 illustrative。
- 「报告 / 体系 / 设置」三个导航项在样张里置灰，尚未覆盖。
- 筛选、按钮、下拉不提交；执行详情里的证据折叠可用。
- 样张内嵌了 5 个 woff2（合计约 3.8MB），仅用于离线评审；正式实现按需子集化。
- 深色已随样张实现切换；还没落 Blazor，也未做 `prefers-color-scheme` 自动跟随。

## 重新生成

```bash
cd docs/visual-style
python3 build.py
```

截图（需要 Playwright 的 Chromium）：

```bash
npx playwright screenshot --viewport-size="1440,1000" --full-page \
  file:///home/muomu/Inspector/docs/visual-style/v1/overview.html \
  screenshots/a-overview-full.png
```

## 下一步

1. 你从 A / B / C 里选一版（或指出某一版的局部）。
2. 把选中的版面冻成 `tokens.css` + `/styleguide` 活页面（色板、字体、状态、表格、表单、空态、对比度自检）。
3. 按 IA 骨架落 Blazor 页面：总览 / 异常 / 执行 / 报告 + 体系 + 设置，顶栏项目/环境上下文。
4. Grafana MCP spike：认证头、工具契约、3 条演示断言与真实指标映射。
