# WinUI 3 外观试用版

日期：2026-10-07

本版用于比较 Windows 原生界面的形状、材质、动画和主要操作。它是独立的 WinUI 3 前端，复用原资源库核心与数据；现有 WPF 前端继续保留。[WinUI 3 是 Windows App SDK 提供的原生桌面 UI 框架](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)。

## 启动与数据

解压完整运行包，先退出已打开的 WPF 资源库，再运行解压目录中的 `LocalResourceLibrary.WinUI.exe`。两个前端使用同一把资源库互斥锁，不能同时打开同一个数据目录。

默认复用 `%LOCALAPPDATA%\LocalResourceLibrary` 中的 `library.db` 与 `settings.json`。在 WinUI 版保存的别名、说明、笔记、项目关系或新增资源，之后会在 WPF 版中显示。添加资源仍然只保存文件和文件夹引用。

需要指定其他资源库时，可在 EXE 所在目录打开 PowerShell，使用 `--data-dir`：

```powershell
& .\LocalResourceLibrary.WinUI.exe --data-dir "D:\MyResourceLibrary"
```

发布包采用自包含的完整目录形式。分发、移动或解压时请保留整个目录，不要只复制 EXE；相关依赖随程序一并提供。[Microsoft 自包含部署说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)介绍了这种部署方式。

## 本轮可试用的操作

- 搜索；全部资源、最近打开、路径失效及项目筛选；选择资源查看详情。
- 编辑别名、说明和笔记，保存或放弃修改；选择多个所属项目；新建项目。
- 刷新资源状态；打开资源、打开所在位置、复制路径。
- 通过文件/文件夹选择器或拖放添加资源。
- 切换中文与英文；切换资源、筛选或关闭时处理未保存修改。

本轮范围尚未包括真实文件重命名、修复路径、编辑或删除项目、从库中移除资源，以及原版完整右键菜单。需要这些操作时，可退出试用版后使用 WPF 版。

## 外观与主题

资源区默认采用「详细信息」视图，显示名称、原文件修改日期、类型和大小。通过工具栏「查看」可切换超大图标、大图标、中等图标、小图标、列表、详细信息、平铺和内容；图标取自 Windows 的文件类型关联与文件夹图标。

「排序」提供名称、修改日期、类型、大小和上次打开，以及递增、递减和文件夹优先。详细信息表头也可点击排序，再次点击反转顺序。名称使用 Windows 自然排序，例如文件 2 排在文件 10 前面。

未选中资源时，右侧详情不占用空间。选中资源后自动展开，点击文件区空白、按 Esc 或切换左侧分类后收起；有未保存修改时仍会提示保存、放弃或取消。详情右上角可关闭面板，工具栏「详细信息」可为当前选中资源重新打开。切换视图、排序或调整宽度保留当前选择与未保存内容。

拖动左右两条分隔线调整三栏宽度；分隔线获得键盘焦点后也可用左右方向键调整。中间栏随左右栏宽度和窗口大小变化。查看方式、排序选项和左右栏宽度保存在数据目录内的 `explorer-settings.json` 中，下次启动恢复；启动时不恢复资源选择，详情保持收起。

界面使用原生 ListView、GridView、输入框、按钮、菜单和 ContentDialog，保留 Windows 控件的焦点、按压及弹出动画。窗口配置 Mica 背景，内容以清晰的分区和克制的圆角呈现。

主题自动跟随 Windows 的深浅色设置。本轮尚未加入应用内主题切换或自定义颜色设置。Mica 的实际效果受 Windows 版本、透明度、高对比度和节电状态影响；系统会在不适用时使用纯色背景。[Microsoft Mica 材质说明](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)。

## 保留的原版本

项目中的原 WPF 源码位于 `src/LocalResourceLibrary.App`，本轮不修改该前端。项目本机保留的发布快照位于 `dist/WPF-preserved-20261007`，包含：

- `LocalResourceLibrary-win-x64` 完整运行目录。
- `LocalResourceLibrary-win-x64.zip` 原运行包。
- `LocalResourceLibrary-v0.1-source.zip` 原源码包。

保留版本指界面源码与发布文件；两个前端默认使用同一个资源库数据目录。

## 开发与验证

新前端源码位于 `src/LocalResourceLibrary.WinUI`。以下命令在 Windows 上、源码项目根目录的 PowerShell 中执行。安装 .NET 10 SDK 后构建独立解决方案；本项目使用的 Windows SDK 构建工具通过 NuGet 还原：

源码建议解压到 `C:\Dev\LocalResourceLibrary-WinUI-trial-source` 等较短目录。本机复验中，过深目录使 XAML 编译的中间程序集路径达到 263 字符并构建失败；同一源码移到短目录后正常构建。现成运行包不需要编译。

```powershell
dotnet build .\LocalResourceLibrary.WinUI.slnx -c Release -p:Platform=x64
```

发布完整运行目录并生成 ZIP：

```powershell
& .\scripts\publish-winui.ps1 -Zip
```

输出为 `dist/LocalResourceLibrary-WinUI-win-x64` 完整目录及同名 ZIP；脚本同时附带使用文档和依赖许可说明。

生成可编辑源码包：

```powershell
& .\scripts\package-winui-source.ps1
```

输出为 `dist/LocalResourceLibrary-WinUI-trial-source.zip`，包含两个前端、核心代码、开发脚本及文档；不包含发布程序或资源库数据。

本版定位为外观与主要交互试用版，功能范围以上述清单为准。具体构建、启动、视觉与交互验证结果，以及验证边界，记录在随运行包分发的 `docs/winui-verification.md`；源码文档目录中的文件名为 `winui-verification.md`。

