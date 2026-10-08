# MCP 接入 / MCP integration

`LocalResourceLibrary.Mcp` 是独立的 .NET 10 C# MCP Server，使用官方 `ModelContextProtocol` 2.2.0 SDK 和 stdio。客户端按需启动它，不需要 WinUI、不开放网络端口、不注册开机启动或后台服务。每个客户端运行自己的进程，多个进程和 WinUI 共用 Core 与同一个 SQLite 库。

## 直接运行和 Codex 配置

在仓库根目录发布包含运行依赖的 Windows x64 文件夹：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-mcp.ps1
```

输出在 `artifacts/LocalResourceLibrary-Mcp-win-x64`，不替换已有 `dist` 中的 WinUI 运行包。保留完整文件夹；可以用 `-OutputDirectory 'C:\Tools\LocalResourceLibrary-Mcp'` 指定位置。生成的 `codex-mcp.toml` 已填写当前发布目录的绝对启动路径，直接将其内容追加到 `%USERPROFILE%\.codex\config.toml`。无需先双击 MCP 程序或 WinUI。重启客户端连接后即可发现工具。本步骤不修改现有全局 Codex 配置。

通用示例（将启动路径换成实际发布位置）：

```toml
[mcp_servers.local_resource_library]
command = 'C:\Tools\LocalResourceLibrary-Mcp\LocalResourceLibrary.Mcp.exe'
args = ["--allow-batch-commit"]
startup_timeout_sec = 30
tool_timeout_sec = 60

[mcp_servers.local_resource_library.tools.commit_resource_updates]
approval_mode = "prompt"
```

默认数据目录与 WinUI 相同：`%LOCALAPPDATA%\LocalResourceLibrary`，数据库为 `library.db`。如果 WinUI 使用了自定义目录，MCP 也必须指定同一个目录：

```toml
args = ['--data-dir', 'E:\MyLibrary', '--allow-batch-commit']
```

仅禁用批量提交时，去掉 `--allow-batch-commit`；单条写入仍可使用。只读客户端可以设置 `enabled_tools = ["search_resources", "list_projects", "list_project_resources"]`。`get_resource` 默认会通过文件身份修复索引路径，诚实标记为可写工具；调用 `resolve_path=false` 时不会修复。

开发时先构建，再让客户端直接启动 DLL（不要把会向 stdout 打印构建日志的 `dotnet run` 配成协议入口）：

```powershell
dotnet build src/LocalResourceLibrary.Mcp -c Release
dotnet src/LocalResourceLibrary.Mcp/bin/Release/net10.0/LocalResourceLibrary.Mcp.dll --data-dir C:\Temp\Library-Mcp
```

其他 MCP 客户端使用同一可执行文件、参数和 stdio 配置。批量提交必须在客户端设置人工确认；没有确认能力的客户端应省略 `--allow-batch-commit`，或者禁止 `commit_resource_updates`。启动许可加预览令牌提供明确的操作边界，令牌本身不能证明真人同意；不声称阻止有库文件访问权限的任意本地程序。

配置字段依据 [官方 Codex MCP 文档](https://developers.openai.com/codex/mcp)。SDK 的 stdio 接入依据 [官方 C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)。

## 工具

输入和输出使用 snake_case 字段。工具同时返回 `structuredContent` 和同内容的 JSON 文本，便于不同客户端读取。所有查询按需重新读取数据库，不使用长期缓存。

| 工具 | 参数与行为 |
| --- | --- |
| `search_resources` | `query` 默认空；可选 `type` 为 `file`、`folder`、`url`；可选 `project_id`；`limit=100`、`offset=0`。复用 Core 本地文本搜索，覆盖真实名称、别名、说明、笔记、路径/URL 和项目名称，多词全部匹配 |
| `get_resource` | 必需 `item_id`；`resolve_path=true`。返回永久 ID、类型、真实目标、元数据、项目和可用状态；必要时按 Volume ID/File ID 恢复文件索引路径，不打开文件，不增加打开次数 |
| `list_projects` | 无参数；返回全部项目 ID、名称、说明、置顶和顺序 |
| `list_project_resources` | 必需 `project_id`；`limit=100`、`offset=0`；分页获取该项目的所有三类资源 |
| `add_resource` | 必需 `type`、`target`；可选 `alias`、`description`、`note`、`project_ids`。文件/文件夹须是存在的绝对路径，URL 只允许 HTTP/HTTPS。已有目标复用记录，只增加所选关联，不覆盖原元数据 |
| `update_resource` | 必需 `item_id`；可选 `alias`、`description`、`note`；至少指定一个字段。未指定或 null 保留，空字符串清空；不改路径、网址或归属 |
| `create_project` | 必需 `name`；可选 `description`。复用 Core 名称校验与不区分大小写的去重规则；同名时报告冲突，客户端可查询并使用已有项目 |
| `update_project` | 必需 `project_id`；可选 `name`、`description`，只更新指定字段；资源关联、置顶、顺序保留 |
| `set_resource_projects` | 必需 `item_id`；可选 `add_project_ids`、`remove_project_ids`，至少一条变更；只增减指定关联，保留其他项目。交叠的增减 ID 拒绝，重复 ID 自动去重 |
| `preview_resource_updates` | 必需 `updates` 数组，每项含 `item_id` 及所需元数据字段，1–100 个不同资源；返回精确前后值、`confirmation_token`、`expires_at`、`commit_enabled`，不写数据库 |
| `commit_resource_updates` | 必需 `confirmation_token`；仅提交相同进程中预览的内容，需要启动许可和客户端用户确认；所有条目一事务成功或全回滚 |

搜索和项目资源列表返回 `items`、`total`、`limit`、`offset`、`has_more`。`limit` 范围 1–1000；继续请求 `offset + limit`，直到 `has_more=false`。返回结果按 Core 创建时间顺序排列；大量资源分页期间如果有外部新增/删除，客户端应重新查询核对。

资源的 `path` / `url` 只有对应类型有值，`target` 总有值。文件/文件夹的 `available` 为布尔值，`availability` 为 `available` 或 `missing_or_inaccessible`；不区分丢失与访问权限不足。网页 `available=null`、`availability=not_checked`、`is_missing=false`，只表示保存的收藏，**不表示网站在线**。搜索不恢复失效路径；对要阅读的文件调用 `get_resource` 可按已保存身份定位同一卷内的新位置。

文件/文件夹按既有路径规范化去重；网页保留现有精确 URL 文本去重规则（去除首尾空白），HTTP/HTTPS、大小写、端口、参数、片段差异不会被合并。保留既有 Item ID 和文件身份机制；路径不是永久资源 ID。不同路径的硬链接等仍遵循既有项目规则，不增加全盘身份扫描。

## Agent 工作流程

- 查资料：`search_resources({"query":"PDMS Catalogue"})` → 必要时 `get_resource` → Agent 使用自己的文件或网页读取能力分析。
- 收藏到项目：`list_projects` → 查询或 `create_project` → `add_resource` 并传 `project_ids`。
- 加入另一个项目：`set_resource_projects` 只传 `add_project_ids`，保留原归属。
- 批量补简介：查询项目的所有分页 → 筛选空简介 → Agent 自行读取资料 → `preview_resource_updates` → 将全部前后变化展示给用户 → 用户确认后调用 `commit_resource_updates`。

预览令牌是随机、进程内、十分钟有效且仅能使用一次的凭据。服务器重启或换客户端进程后需要重新预览。最多同时保留 32 个预览。提交失败同样消耗令牌。提交时 Core 在同一事务内复核所有预览记录：元数据、目标、身份、时间、打开记录及项目归属若已变化，会报告对应 Item ID 并整批不写。项目名称、说明等上下文变化也可使预览失效，请重新预览并确认。

业务失败通过 MCP `isError=true` 与结构化错误返回；批量失败包含每项 `item_id` / `error`，`committed=false` 且 `items=[]`。数据库繁忙会报告错误，不应盲目重放不确定结果；先查询确认当前状态。单条写入以事务串行执行，同一字段同时更新时后提交值生效；需要更严格的复核可使用批量预览（也允许只含一项）。

## 并发、WinUI 与安全范围

SQLite 使用 WAL、外键、唯一约束和 10 秒锁等待。结构升级的版本检查与迁移位于同一写事务，多客户端启动不会重复迁移。MCP 不获取 WinUI 的窗口单实例锁，各进程共享库时写入由 SQLite 串行处理；不支持多机器共享网络数据库。

WinUI 中点击「刷新资源和状态」或按 F5 可加载 MCP 变更；有未保存草稿时继续提示保存/放弃/取消。WinUI 保存仅提交实际编辑字段和项目关联增减，保留外部对未编辑字段和其他关联的更新；同字段冲突时拒绝保存并保留草稿，用户重新刷新处理。没有后台轮询或硬盘扫描。

数据库备份和恢复时必须完全退出 WinUI **以及所有 MCP 客户端/服务器进程**，再复制整个数据目录，含 SQLite 辅助文件。MCP 可独立运行，因此只退出 WinUI 已不足以保证库空闲。

MCP 只管理索引与元数据，不提供删除工具、文件内容读写、真实文件改名/移动、任意命令执行、上传、全盘扫描、网页抓取或 PDF/Word/CAD 解析。无网络监听；程序所有日志写 stderr，stdout 专供 MCP 协议。文件身份恢复只校正数据库路径，不修改真实文件。

标签、语义搜索、AI 分类、自动生成简介、MCP Resources 和复杂关系留给后续扩展。首版未添加这些能力。

## 功能检查

```powershell
dotnet build .\LocalResourceLibrary.WinUI.slnx -c Release -p:Platform=x64
dotnet run --project tests/LocalResourceLibrary.Checks -c Release
dotnet run --project tests/LocalResourceLibrary.ExplorerChecks -c Release
dotnet run --project tests/LocalResourceLibrary.UrlChecks -c Release
dotnet run --project tests/LocalResourceLibrary.CoreConcurrencyChecks -c Release
dotnet run --project tests/LocalResourceLibrary.McpChecks -c Release
```

检查使用隔离临时数据库和资源，不改个人库。MCP 检查通过真实 stdio 子进程覆盖协议和业务；并发检查使用多个 Core 连接及 MCP 进程，Explorer 检查验证 UI 模型刷新及草稿增量保存。这五组检查自身不等同于真实 WinUI 窗口操作或视觉验收；本次还单独完成了 Codex 自动启动和原生 WinUI/MCP 同时运行功能检查。完整执行结果见 [MCP 功能核验](mcp-verification.md)。
