# V0.1 核验记录 / Verification record

日期 / Date: 2026-10-07

此记录区分已完成检查和仍待完成的检查。发布清单中的项目不会因为出现在文档中就视为通过。

This record separates completed checks from checks still awaiting execution. A checklist entry is not evidence of a pass.

| 检查 / Check | 当前结果 / Current result |
| --- | --- |
| `dotnet build LocalResourceLibrary.slnx` | 通过：0 警告、0 错误 / Passed: zero warnings and zero errors |
| 核心回归检查 / Core regression checks | 27 / 27 通过 / passed |
| WPF 界面集成检查 / WPF UI integration checks | 18 / 18 通过，0 绑定警告或错误 / passed, zero binding warnings or errors |
| 界面截图与目视检查 / UI captures and visual inspection | 已检查新版中文、英文、空状态、失效状态、最小窗口、项目弹窗和下拉框截图 / Redesigned Chinese, English, empty, missing, minimum-size, project dialogs and dropdown captures reviewed |
| 独立发布版启动 / Published self-contained startup | 通过：隔离数据目录启动真实发布程序，显示主窗口、创建数据库，正常关闭后退出码 0 / Passed: packaged executable launched with isolated data, displayed its window, created the database, and closed with exit code 0 |
| 完整发布目录 / Release folder | 已生成 / Generated |
| 发布 ZIP / Release ZIP | 已生成；419 个文件可完整读回，包含运行时与原生 SQLite，未包含用户数据库或设置 / Generated; all 419 files read back, runtime and native SQLite included, no user database or settings |
| 源码 ZIP 及内容检查 / Source archive and contents | 已生成；37 个源码与文档文件，解压后完整解决方案构建通过，0 警告、0 错误 / Generated; 37 source and documentation files, extracted solution builds with zero warnings or errors |
| 未安装 .NET 的另一台 Windows 电脑 / Another Windows machine without .NET installed | 尚未验证 / Not verified |

核心检查使用临时数据库和测试文件，覆盖路径去重、多项目归属、元数据编辑、六类搜索字段、持久化重开、缺失路径、真实重命名与修复、子路径更新、逻辑移除及失败回滚。打开资源使用替代平台接口，因此不证明 Windows 默认程序或资源管理器已实际打开。

Core checks use temporary databases and files. Coverage includes path deduplication, multiple project membership, metadata editing, six search fields, persistence reopening, missing paths, physical rename and repair, descendant references, logical removal, and failure rollback. Shell opening uses a substitute platform interface; those checks do not establish that a Windows default application or Explorer was actually opened.

发布程序的启动检查在当前 Windows 桌面会话中进行，使用隔离的 `--data-dir`。UI Automation 实际读取到搜索、添加资源、语言选择、左侧导航及空状态控件；窗口标题为「本地资源库」，并生成 SQLite `library.db`。通过窗口关闭操作正常退出。该结果不等同于另一台未安装 .NET 的电脑已验证，也不涵盖所有界面交互。

The published startup check ran in the current Windows desktop session with an isolated `--data-dir`. UI Automation read the actual search, add-resource, language, navigation, and empty-state controls. The window title was 本地资源库 and SQLite `library.db` was created. The app exited normally through window close. This does not establish portability on a second computer without .NET, or cover every UI interaction.

WPF 检查通过真实控件、绑定和路由事件验证混合文件/文件夹拖放、六类字段搜索、详情保存、多项目归属、语言切换保留编辑草稿、菜单分组、打开事件、路径失效提示及无效设置恢复。检查使用隔离数据，外部程序启动使用替代接口。系统原生文件选择对话框、剪贴板、物理鼠标拖动及默认外部应用未做完整人工端到端检查。截图覆盖 1420×860 常规窗口及 1080×650 最小窗口；详情区域可以滚动。

The WPF runner exercises real controls, bindings and routed events for mixed file/folder drops, six-field search, detail saving, multiple projects, language changes that preserve drafts, context menus, opening events, missing paths and malformed settings recovery. It uses isolated data and substitutes external shell launches. Native pickers, clipboard, physical mouse dragging and default external applications have not received a complete manual end-to-end check. Captures cover normal 1420×860 and minimum 1080×650 windows; details remain scrollable.

## 前端外观优化核验 / UI redesign verification

本次使用 frontend-design skill 改进三栏形状、控件模板、表面层次和操作动画，保留原有核心业务逻辑。颜色集中为动态主题资源，尚未提供深色、浅色或自定义主题设置。

新增 5 项针对自定义控件的检查：真实语言下拉框展开与选中；主窗口新建项目弹窗的空白验证、取消与创建；输入弹窗完整返回名称与多行说明；项目选择弹窗正确显示名称并确认、取消及处理空列表；详情滚动条滑块实际移动内容并回到顶部。所有数据均隔离，弹窗确认与取消通过 WPF 自动化接口执行，下拉框与滑块使用路由输入事件。

检查输出在 `artifacts/frontend-design/`，包括主窗口、最小窗口、失效状态、项目输入弹窗、项目选择弹窗及下拉框截图。没有把路由事件检查当作物理鼠标操作的完整人工验收，也没有声称已完成另一个 Windows 环境、所有系统文件选择器或外部默认应用的检查。

The redesign adds five targeted checks for customized language selection, modal project validation/cancel/create, exact multiline input return values, readable project dropdown selection and modal outcomes, and real detail-content movement through the scrollbar thumb. All data remains isolated. Theme selection is deferred; dynamic color resources provide the extension boundary only. Routed events and WPF automation do not establish complete physical mouse QA or portability on another Windows machine.
