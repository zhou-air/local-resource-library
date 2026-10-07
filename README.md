# 本地资源库 · Local Resource Library

[English](README.en.md)

基于 WinUI 3 的 Windows 本地资源库：为文件和文件夹添加别名、说明、笔记与项目归属，支持本地搜索、八种查看方式、自然排序和中英文界面。

**资源仍保留在原来的位置。** 添加资源只保存绝对路径，不复制、不移动文件，也不保存文件内容。同一个资源可属于多个逻辑项目，所有项目共用同一条资源记录。项目现仅保留 WinUI 3 桌面界面。

## 已提供的功能

- 通过文件选择、文件夹选择或拖放添加资源；已登记的相同路径复用现有记录。
- 分别维护别名、说明和笔记；别名作为资源库中的首选显示名称，不改动真实文件名。
- 创建逻辑项目，在详情中勾选或取消项目归属；一个资源可以属于多个项目。
- 在别名、真实文件名、路径、说明、笔记和项目名称中执行本地文本搜索。
- 按全部资源、最近打开、路径失效和项目筛选。
- 双击打开资源；打开所在位置、复制路径；记录打开次数与最近打开时间。
- 显示 Windows 文件关联图标，提供超大图标、大图标、中等图标、小图标、列表、详细信息、平铺和内容八种视图。
- 按名称、修改日期、类型、大小或上次打开排序，可选择递增、递减和文件夹优先；名称使用 Windows 自然排序。
- 调整导航栏和详情栏宽度；查看方式、排序、栏宽与语言选择在本机保存。
- SQLite 本地持久化；中文与英文界面；未保存修改的保存、放弃和取消提示。

## 运行

支持 64 位 Windows 10 / Windows 11 桌面环境。发布包包含所需的 .NET 与 Windows App SDK 运行依赖。

1. 完整解压发布文件夹，保留其中的全部文件。
2. 双击 `LocalResourceLibrary.WinUI.exe`。
3. 添加文件或文件夹，填写说明，并按需要加入项目。

不需要账号。应用不提供云同步、遥测或后台网络服务；打开资源时调用的外部程序可能有自己的联网行为。

### 常用操作

| 目的 | 操作 |
| --- | --- |
| 为资源取易懂的名称 | 编辑「别名」，不会改动真实文件名 |
| 说明资源内容和价值 | 编辑「说明」 |
| 留下个人或临时信息 | 编辑「笔记」 |
| 设置或取消项目归属 | 在详情中勾选或取消项目，然后保存 |
| 打开资源 | 双击资源，或使用详情中的「打开」 |
| 在资源管理器定位 | 使用「打开所在位置」 |
| 获取资源路径 | 使用「复制路径」 |
| 检查外部文件的可用性 | 点击「刷新状态」 |
| 切换查看方式和排序 | 使用工具栏「查看」和「排序」；详细信息表头也可点击排序 |
| 切换语言 | 使用窗口顶部的语言选择器；选择会保存 |

修改详情后点击「保存」。切换资源、分类或关闭窗口时，未保存的修改会提示保存、放弃或取消。搜索在当前左侧分类中生效；输入多个以空格分开的词时，每个词都需要出现在该资源的某个可搜索字段中。要搜索整个资源库，请先选择「全部资源」。

添加文件夹只登记文件夹本身，不自动导入其全部内容。资源在外部移动、断开或恢复连接后，可点击「刷新状态」重新检查；失效路径的记录和说明会保留。

选中资源后详情栏展开；点击资源区空白、按 `Esc` 或切换左侧分类可清除选择并收起。工具栏「详细信息」可以为当前选择打开或关闭详情。拖动分隔线调整栏宽；分隔线获得键盘焦点后可用左右方向键调整。

快捷键：`Ctrl+F` 聚焦搜索，`Ctrl+S` 保存详情，`F5` 刷新状态。在资源列表中，`Enter` 打开选中资源，`F2` 编辑别名。

## 数据与备份

默认数据库位置：

```text
%LOCALAPPDATA%\LocalResourceLibrary\library.db
```

数据库保存资源元数据、项目与关联关系，不保存资源本体。备份数据库也不等于备份原文件。语言保存在同一目录的 `settings.json` 中，视图、排序和栏宽保存在 `explorer-settings.json` 中。同一个数据目录同时只允许打开一个应用实例。

**备份时先完全退出应用，再复制整个 `LocalResourceLibrary` 数据目录。** 恢复时也先退出应用，再将备份放回原位置。迁移到另一台电脑时，需要让资源在保存的路径上可用；当前界面尚未提供路径修复。

使用独立数据目录：

```powershell
.\LocalResourceLibrary.WinUI.exe --data-dir "C:\Temp\LocalResourceLibrary-Test"
```

删除程序文件夹不会自动删除保存在本地应用数据目录中的资源库。

## 从源码构建

需要 Windows 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。首次还原、构建或发布可能需要联网下载 NuGet 依赖及运行时包；Windows SDK 构建工具由 NuGet 还原。`global.json` 选择 .NET 10 正式版 SDK，并允许使用同一主版本的更新功能带。

建议将源码放在 `C:\Dev` 等较短路径。过深目录可能导致 WinUI XAML 编译的中间路径超长。

```powershell
dotnet build .\LocalResourceLibrary.WinUI.slnx -c Release -p:Platform=x64
dotnet run --project tests/LocalResourceLibrary.Checks
dotnet run --project tests/LocalResourceLibrary.ExplorerChecks
```

这两组检查分别覆盖核心行为和资源浏览模型，不等于完整界面或视觉验收。

发布包含运行依赖的 Windows x64 版本：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-winui.ps1 -Zip
```

发布目录为 `dist/LocalResourceLibrary-WinUI-win-x64`，压缩包为同名 `.zip`。必须分发完整文件夹或完整压缩包。成功发布后，已有输出保留在 `dist` 下的 `.previous-winui-…` 目录或压缩包中。

打包源码，无需安装 Git：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-winui-source.ps1
```

输出为 `dist/LocalResourceLibrary-WinUI-trial-source.zip`，包含独立顶层目录、WinUI 界面、Core、检查程序、配置、文档和开发脚本。脚本排除构建缓存、数据库、用户设置及日志；按固定顺序和时间戳生成条目。已有源码包会保留为 `.previous-winui-source-…` 文件。

更多信息：[使用说明](docs/winui-trial.md) · [架构](docs/architecture.md) · [核验记录](docs/winui-verification.md) · [发布核验清单](docs/release-checklist.md) · [第三方依赖](THIRD-PARTY-NOTICES.md)

## 范围与限制

当前 WinUI 界面尚未提供真实文件重命名、路径修复、项目编辑或删除、从资源库移除资源以及完整右键菜单。相关数据和文件操作接口仍保留在 Core 中，但没有接入界面；请勿将核心接口能力当成现有界面功能。资源在其他程序中移动或改名后，旧路径会失效；应用不会扫描整台电脑来猜测新位置。

搜索只处理库内元数据，不读取文件内容，不使用 AI 或语义向量。当前不包含网址收藏、内容预览、标签、全局快捷搜索、云同步、账号、浏览器扩展、自动分类或网络服务。应用面向单用户本地使用，不提供共享数据库或多设备协作方案。

主题跟随 Windows 深浅色设置，尚无应用内主题切换。Mica 背景效果取决于系统版本和设置。应用提供中英文操作提示与错误说明；Windows 返回的底层错误详情可能使用操作系统自身的语言。

## 许可证

本项目原创源码采用 **GNU General Public License v3.0 only（GPL-3.0-only，仅第 3 版）**，完整文本见 [LICENSE](LICENSE)。Copyright (C) 2026 zhou-air。

第三方组件仍按各自许可证使用，见 [第三方依赖说明](THIRD-PARTY-NOTICES.md) 和 [WinUI 依赖说明](docs/winui-third-party-notices.md)。
