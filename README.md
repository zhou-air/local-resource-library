# 本地资源库 · Local Resource Library

[English](README.en.md)

轻量 Windows 桌面资源库：给本地文件和文件夹补充名称、说明、笔记与项目归属，帮助你再次找到它们。

**资源仍保留在原来的位置。** 添加资源只保存绝对路径，不复制、不移动文件，也不保存文件内容。同一个资源可属于多个逻辑项目，所有项目共用同一条资源记录。

## 两套桌面界面

项目包含共享 SQLite 核心的 WPF 基础版与 WinUI 3 试用版。下方 V0.1 功能与默认运行、构建命令针对 WPF 版。

WinUI 3 版提供 Windows 原生控件、文件关联图标、八种查看方式、自然排序和可调整的三栏布局；部分资源管理操作仍需使用 WPF 版。两套界面默认使用同一个数据目录，同一资源库不能同时打开。使用方法、功能范围和构建命令见 [WinUI 3 试用版说明](docs/winui-trial.md)，验证范围见 [WinUI 核验记录](docs/winui-verification.md)。

## V0.1 功能

- 通过文件选择、文件夹选择或拖放添加资源；已登记的相同路径复用现有记录。
- 分别维护别名、说明和笔记；别名作为资源库中的首选显示名称。
- 创建逻辑项目，将一个资源加入多个项目。
- 在别名、真实文件名、路径、说明、笔记和项目名称中执行本地文本搜索。
- 双击使用 Windows 默认程序打开文件，或使用资源管理器打开文件夹；记录打开次数与最近打开时间。
- 打开所在位置、复制路径、移出项目、从资源库移除。
- 单独执行真实文件或文件夹重命名，并保留资源 ID、项目关系和说明。
- 标记丢失的路径，保留原信息，并允许重新指定路径。
- SQLite 本地持久化；可切换中文和英文界面。

## 运行

支持 64 位 Windows 10 / Windows 11 桌面环境。发布版包含所需的 .NET 运行时。

1. 完整解压发布文件夹，保留其中的全部文件。
2. 双击 `LocalResourceLibrary.exe`。
3. 添加文件或文件夹，填写说明，并按需要加入项目。

不需要账号。应用不提供云同步、遥测或后台网络服务；打开资源时调用的外部程序可能有自己的联网行为。

### 常用操作

| 目的 | 操作 |
| --- | --- |
| 为资源取易懂的名称 | 编辑「别名」，不会改动真实文件名 |
| 说明资源内容和价值 | 编辑「说明」 |
| 留下个人或临时信息 | 编辑「笔记」 |
| 在多个项目复用资源 | 在详情中设置多个项目归属 |
| 打开资源 | 双击资源，或使用右键菜单的「打开」 |
| 在资源管理器定位 | 使用「打开所在位置」 |
| 改动真实名称 | 使用独立的「重命名真实文件 / 文件夹」操作，确认后才修改磁盘上的名称 |
| 修复已移动的资源 | 选择丢失的资源，使用路径修复操作重新指定位置 |
| 仅取消某个项目归属 | 使用「从项目移除」 |
| 从应用忘记资源 | 使用「从资源库移除」；原文件仍保留 |
| 切换语言 | 使用窗口顶部的语言选择器；选择会保存 |

项目是逻辑集合，不是磁盘文件夹。删除项目或移除资源记录不会删除磁盘上的文件。修改别名与重命名实际文件是两种独立操作。

修改详情后点击「保存」。切换资源或关闭窗口时，未保存的修改会提示保存、放弃或取消。搜索在当前左侧分类中生效；输入多个以空格分开的词时，每个词都需要出现在该资源的某个可搜索字段中。要搜索整个资源库，请先选择「全部资源」。

资源在外部移动、断开或恢复连接后，可点击「刷新状态」重新检查。添加文件夹只登记文件夹本身，不自动导入其全部内容。在应用内重命名文件夹时，库内已经登记的子路径会一起更新。

快捷键：`Ctrl+F` 聚焦搜索，`Ctrl+S` 保存详情，`F5` 刷新状态。在资源列表中，`Enter` 打开选中资源，`F2` 编辑别名。

## 数据与备份

默认数据库位置：

```text
%LOCALAPPDATA%\LocalResourceLibrary\library.db
```

数据库保存资源元数据、项目与关联关系，不保存资源本体。备份数据库也不等于备份原文件。

界面语言保存在同一目录的 `settings.json` 中。同一个数据目录同时只允许打开一个应用实例。

**备份时先完全退出应用，再复制整个 `LocalResourceLibrary` 数据目录。** 恢复时也先退出应用，再将备份放回原位置。若要将资源库迁移到另一台电脑，还需要让目标文件在新电脑上可用，并修复已改变的路径。

为试用或检查使用独立数据目录：

```powershell
.\LocalResourceLibrary.exe --data-dir "C:\Temp\LocalResourceLibrary-Test"
```

卸载或删除程序文件夹不会自动删除保存在本地应用数据目录中的资源库。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 和 Windows。首次还原、构建或发布可能需要联网下载 NuGet 依赖及运行时包。

```powershell
dotnet build LocalResourceLibrary.slnx
dotnet run --project tests/LocalResourceLibrary.Checks
dotnet run --project tests/LocalResourceLibrary.UiChecks
dotnet run --project src/LocalResourceLibrary.App
```

界面集成检查需要可用的 Windows 桌面会话。`global.json` 选择 .NET 10 正式版 SDK，并允许使用同一主版本的更新功能带。

发布包含运行时的 Windows x64 版本：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Zip
```

发布目录为 `dist/LocalResourceLibrary-win-x64`，压缩包为 `dist/LocalResourceLibrary-win-x64.zip`。必须分发完整文件夹或完整压缩包。脚本先发布到新的临时目录，成功后替换输出目录；已有输出会保留为 `dist` 下的 `.previous-…` 目录，确认不再需要后可自行删除。

打包源码，无需安装 Git：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-source.ps1
```

输出为 `dist/LocalResourceLibrary-v0.1-source.zip`，包含独立顶层目录。脚本只收集指定的源码、项目配置、文档和脚本，排除构建缓存、数据库、用户设置及日志；按固定顺序和时间戳生成条目。已有源码压缩包会保留为 `.previous-source-…` 文件。

更多信息：[架构](docs/architecture.md) · [界面设计](docs/ui-design.md) · [核验记录](docs/verification.md) · [发布核验清单](docs/release-checklist.md) · [第三方依赖](THIRD-PARTY-NOTICES.md)

## 范围与限制

V0.1 仅登记本地文件和文件夹，按当前保存的路径寻找资源。资源在其他程序中移动或改名后，需要修复路径；不会扫描整台电脑来猜测新位置。

搜索只处理库内元数据，不读取文件内容，不使用 AI 或语义向量。当前不包含网址收藏、预览、标签、全局快捷搜索、云同步、账号、浏览器扩展、自动分类或网络服务。Item 模型与独立搜索接口为后续扩展保留边界，这些功能尚未实现。

当前版本面向单用户本地使用，不提供共享数据库或多设备协作方案。真实文件重命名会修改磁盘内容；遇到占用、同名目标或权限不足时应先处理原因后重试。

应用提供中英文操作提示与错误说明；Windows 返回的底层错误详情可能使用操作系统自身的语言。

## 许可证

本项目原创源码采用 **GNU General Public License v3.0 only（GPL-3.0-only，仅第 3 版）**，完整文本见 [LICENSE](LICENSE)。Copyright (C) 2026 zhou-air。

第三方组件仍按各自许可证使用，见 [第三方依赖说明](THIRD-PARTY-NOTICES.md) 和 [WinUI 依赖说明](docs/winui-third-party-notices.md)。
