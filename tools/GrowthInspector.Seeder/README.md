# GrowthInspector.Seeder

把 `seed/` 目录里的知识资产幂等导入巡检框架的 SQLite 库。独立控制台项目，已加入
`GrowthInspector.slnx` 的 tools 目录，随解决方案一起做编译检查；运行时仍按 `dotnet run --project` 调用。

## 用法

```bash
# 在仓库根目录执行
dotnet run --project tools/GrowthInspector.Seeder -- --db /tmp/inspector-seed-test.db --seed seed/growth
```

| 参数 | 说明 |
|---|---|
| `--db <路径>` | 目标 SQLite 文件。不存在则 `EnsureCreated` 建库建表，父目录自动创建 |
| `--seed <目录>` | seed 目录，默认 `seed/growth` |
| `-h` / `--help` | 显示帮助 |

也支持 `--db=...` / `--seed=...` 写法。退出码：`0` 成功；`2` 参数或 seed 校验失败；`1` 写库失败。

`EnsureCreated` 不做迁移：模型变更后删除目标库文件重跑。

## seed 目录格式

```
seed/growth/
  project.json      项目、环境、MCP server
  runbooks.json     手册清单（正文给 body 或 bodyFile）
  checklists.json   检查单与检查项
  runbooks/*.md     手册正文（纯 Markdown）
```

所有 JSON 文件都拒绝未知字段：键名写错会直接报错，不会被静默忽略。导入前先整体校验，
校验失败不碰数据库。

### project.json

```json
{
  "project": { "name": "Growth", "code": "growth", "larkWebhookUrl": null, "opsContext": "Markdown 文本" },
  "environments": [{ "name": "prod", "isDefault": true }, { "name": "beta", "isDefault": false }],
  "mcpServers": [
    {
      "name": "grafana",
      "transport": "Stdio",
      "command": "/home/muomu/.inspector/bin/mcp-grafana.sh",
      "arguments": [],
      "isEnabled": true,
      "timeoutSeconds": 60,
      "maxResultBytes": 8192
    }
  ]
}
```

- 环境名唯一，且必须有且只有一个 `isDefault=true`；
- MCP server 名唯一；`transport` 取 `Stdio` / `Http`；`environment` 可选，为空表示项目内共用；
- 真实 MCP server 的命令与环境变量只写在本机包装脚本里，不进 seed 与 Git；
- 工具名按 `{server 名}__{工具名}` 拼（如 `grafana__query_prometheus`），检查项正文不再引用具体工具。

### runbooks.json

```json
{
  "runbooks": [
    { "title": "...", "kind": "General", "bodyFile": "runbooks/general-growth-ops.md" },
    { "title": "...", "kind": "Specialized", "body": "内联 Markdown" }
  ]
}
```

- `kind` 取 `General` / `Specialized`，至少要有一份 `General`；
- `body` 与 `bodyFile` 二选一，`bodyFile` 是相对 seed 目录的路径，不允许越出 seed 目录；
- 标题唯一，正文导入前统一换行与去行尾空白。

### checklists.json

```json
{
  "checklists": [
    {
      "name": "...",
      "description": "...",
      "frequency": "Hourly",
      "isEnabled": true,
      "runbookRefs": ["手册标题"],
      "items": [
        {
          "title": "MySQL 依赖连通",
          "body": "确认 MySQL 依赖能否连上、读写是否正常。健康口径为连接与一次读写探测均成功。异常先看网络、凭据与连接数，再查依赖自身负载。",
          "severity": "Critical"
        }
      ]
    }
  ]
}
```

- `frequency` 取 `Manual` / `Hourly` / `Daily`；`runbookRefs` 里的标题必须能在 `runbooks.json` 找到；
- **数组顺序就是 `SortOrder`**（从 1 开始）；
- 检查项只有三个字段：`title`（短标题）、`body`（一段自然语言：查什么 / 什么算健康 / 异常先看哪）、`severity`（`Info` / `Warning` / `Critical`）；
- `body` 必填；不绑定工具、参数模板、取值路径、比较符、阈值或窗口；
- 正文按约定不点名工具、Prometheus 指标、SQL、表名、消费组名或 topic 名，只写业务口径。

## 导入语义

- 一个事务内完成，失败整体回滚；
- 按业务键判新增还是更新：`Project.Code`、`Environment.Name`、`McpServer.Name`、`Runbook.Title`、
  `Checklist.Name`、`ChecklistItem.Title`；
- 不删除 seed 之外的数据；seed 内检查单里已不在 seed 中的检查项标为 `IsArchived=true`；
- 检查项按 `Title` 判新增还是更新；seed 里已不在的检查项标为 `IsArchived=true`；
- 检查项换序时先落到临时 `SortOrder` 再落目标值，避免 `(ChecklistId, SortOrder)` 唯一索引在中间态冲突；
- 检查单引用的手册集合按 seed 对齐：少则补、多则删引用。
