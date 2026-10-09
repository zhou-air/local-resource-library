# MCP 开发功能核验

## 2026-10-09：项目分组、颜色与永久 ID

执行 `dotnet run --project tests/LocalResourceLibrary.McpChecks -c Release`，结果为 **224 个 MCP 断言通过，退出码 0**。该命令构建 MCP 及检查程序，并使用官方 SDK 启动真实 stdio 子进程。全部数据库和资源样例都在本次生成的临时目录；未写入个人数据库，未启动 WinUI，未进行截图或视觉验收。

本次覆盖 18 个工具的发现、输入结构和读写标记；原有无参数 `list_projects`、项目创建/修改和资源工具仍能调用。新增检查验证颜色和所属分组元数据、项目与分组创建/重命名/删除/排序、未分组查询、完整名称候选与歧义标记、名称作为 ID 时拒绝操作、旧 ID 在项目改名后继续可用、重启后 ID/颜色/分组持久化、预设颜色校验、无效分组的原子拒绝、置顶区跨分组排序、未置顶项目移至目标分组、删组保留项目及资源关联、删项目保留资源及其他项目关联，以及删除后重建同名对象生成新 UUID。创建与修改工具的 schema 不提供 ID 设置或替换参数。

原有搜索字段/分页、多项目归属、三类资源去重、元数据局部修改、批量预览与提交、并发写入、原文件内容保留及 NTFS 文件身份恢复检查也全部通过。此结果验证 MCP 协议与隔离 Core/SQLite 行为；不代表此次安装后的 Codex 客户端刷新、真实 WinUI 交互、深浅主题外观或其他机器验收。

## 2026-10-08：历史核验记录

以下是更新前版本的历史结果，不作为本次新增功能的原生窗口或视觉验收。全部写入测试使用隔离临时资源库，不写个人数据库，不检查原文件哈希，不进行截图或视觉验收。

| 检查 | 结果 | 实际覆盖 |
| --- | --- | --- |
| 完整解决方案 Release x64 构建 | 通过，0 警告、0 错误 | WinUI、Core、MCP 和全部五组检查项目 |
| 原有 Core Checks | 52/52 | 原业务回归，包含资源、项目、物理路径/身份恢复与 URL |
| Explorer Checks | 24/24 | 浏览模型、编辑草稿、刷新和增量保存计划；新增外部更新与草稿保留检查 |
| URL Checks | 23/23 | 确定性网页抓取、图标、超时和编辑草稿回归 |
| Core Concurrency Checks | 10/10 | 多连接启动/迁移、三类型去重、局部补丁、关联增减、冲突检测、真实第二条写失败后全回滚、UI 保存冲突 |
| MCP Checks | 151 个断言通过 | 官方 SDK 客户端自动启动实际 stdio 子进程，全部工具、三类型、搜索字段/分页、去重、项目管理、确认边界、令牌过期/重放、批量冲突、两个 MCP 进程与 Core 并发、NTFS 改名恢复和重启持久化 |
| 独立 self-contained MCP 发布 | 通过 | 输出 `artifacts/LocalResourceLibrary-Mcp-win-x64`，包含运行依赖、许可通知和可直接追加的 Codex 配置；发布 DLL 也通过同一组 151 个协议断言 |
| 本机 Codex 自动启动 | 通过，退出码 0 | 本机 `codex-cli 0.162.0-alpha.2` 使用命令参数注入独立 MCP 配置、隔离临时库，自动启动发布 EXE，实际调用一次 `list_projects` 返回空数组 |
| Codex 严格配置加载 | 通过，退出码 0 | `--strict-config` 接受批量提交 `approval_mode="prompt"` 的配置，MCP 正常初始化并再次实际调用 `list_projects`；本次仍未执行批量提交或审批界面 |
| 原生 WinUI + MCP 同时运行 | 通过 | 本次 Release WinUI 与发布后的 MCP EXE 共用隔离库；80 次 MCP 元数据更新期间通过 UIAutomation 实际执行 24 次 WinUI 刷新，读回三类资源新别名；3 资源、6 项目关联，无重复；`integrity_check=ok`、`foreign_key_check=[]` |

Core 回归和并发检查没有启动 WinUI；Explorer Checks 只验证模型，不代表实际窗口交互。MCP 的官方 SDK 启动与 Codex 真实调用分别核验，避免把普通协议客户端误称为 Codex 验收。

Codex 核验仅使用本次临时配置，未修改用户全局 `config.toml`。原始事件保存在本机 `artifacts/mcp-codex-probe.jsonl`，其中有 `mcp_tool_call` 的成功返回；无需启动 WinUI。未测试 Codex 批量提交的真人审批对话框；生成示例按当前官方文档配置该工具为 `approval_mode="prompt"`。MCP 默认拒绝批量提交，开启需 `--allow-batch-commit`；预览令牌只是精确变更绑定，不能代替用户批准。

原生检查启动的 WinUI PID 为 35516，MCP PID 为 29140。WinUI 可访问性控件实际读回 `Coexist File`、`Coexist Folder`、`Coexist Website`；写入后窗口进程仍正常存活。测试完成后 MCP 关闭 stdin 正常退出，仅停止本次隔离 WinUI 进程（标题栏 × 会隐藏到托盘）。未终止任何既有应用实例。完整证据保存在 `artifacts/mcp-winui-coexist-963c90baac594e24b87735e8a3cf4814/native-coexist-result.json`。

此次原生检查覆盖启动、并发写入、实际刷新和名称显示的功能读回，没有截图或外观检查。所有结论以这里列出的实际覆盖为界，不声称通过视觉验收、多机器共享数据库、ReFS 或跨卷身份恢复。

测试结束后的临时目录清理被自动审批拒绝，工具只报告 `blocked by policy`；改用明确文件清单逐项清理同样被拒绝。因此本次原生共存测试和 Codex 探针的临时样例目录保留，检查进程均已退出，不影响实际资源库或 MCP 使用。测试证据文件继续保存在仓库 `artifacts` 中。
