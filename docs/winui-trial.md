# WinUI 3 使用说明

日期：2026-10-07

本项目现仅保留 WinUI 3 桌面界面，使用 SQLite 核心登记本地文件和文件夹。添加资源只保存路径引用，不复制、移动或保存文件内容。[WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/) 是 Windows App SDK 提供的原生桌面 UI 框架。

## 启动与数据

完整解压运行包后，运行 `LocalResourceLibrary.WinUI.exe`。默认数据目录为 `%LOCALAPPDATA%\LocalResourceLibrary`，保存 `library.db`、语言设置 `settings.json` 和浏览布局设置 `explorer-settings.json`。同一数据目录不能被两个应用实例同时打开。

在 EXE 所在目录的 PowerShell 中，可指定其他资源库：

```powershell
& .\LocalResourceLibrary.WinUI.exe --data-dir "D:\MyResourceLibrary"
```

运行包采用自包含的完整目录形式，包含 .NET 和 Windows App SDK 运行依赖。请保留整个目录，不要只复制 EXE。发布方式见 [Microsoft 自包含部署说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)。

备份或恢复前请完全退出应用，再复制整个数据目录。资源本体需另行备份。删除程序文件夹不会自动删除资源库。

## 资源操作

- 通过文件或文件夹选择器、拖放添加资源；同一路径复用现有记录。
- 搜索别名、真实文件名、路径、说明、笔记及项目名称；按全部资源、最近打开、路径失效或项目筛选。
- 编辑别名、说明和笔记，保存或放弃修改。别名不会改变磁盘上的名称。
- 新建逻辑项目，为资源勾选多个所属项目，或取消勾选后保存以移出项目。
- 刷新资源状态；打开资源、打开所在位置、复制路径。
- 切换中文与英文；切换资源、分类或关闭时处理未保存修改。

项目是逻辑集合，不是磁盘文件夹。添加文件夹不会自动导入其全部内容。多词搜索要求每个词都匹配某个可搜索字段；要搜索整个库，请先选「全部资源」。

当前界面尚未提供真实文件重命名、路径修复、项目编辑/删除、从库移除资源以及完整右键菜单。Core 中保留相关接口，尚未接入界面。外部移动或改名会使旧路径失效，应用不会自动寻找新位置。

## 查看、排序与布局

资源区默认采用「详细信息」，显示名称、修改日期、类型和大小。工具栏「查看」还可切换超大图标、大图标、中等图标、小图标、列表、平铺和内容；图标取自 Windows 文件类型关联及文件夹图标。

「排序」支持名称、修改日期、类型、大小、上次打开，以及递增、递减和文件夹优先。详细信息表头也可点击排序，再次点击反转顺序。名称使用 Windows 自然排序，例如文件 2 排在文件 10 前面。

未选中资源时，右侧详情不占用空间。选中后自动展开；点击资源区空白、按 `Esc` 或切换分类后清除选择并收起。有未保存修改时会提示保存、放弃或取消。详情右上角可关闭面板，工具栏「详细信息」可为当前资源重新打开。切换视图、排序或调整宽度保留当前选择与未保存内容。

拖动两条分隔线调整三栏宽度；分隔线获得键盘焦点后也可用左右方向键调整。查看方式、排序和左右栏宽保存在 `explorer-settings.json` 中。下次启动恢复这些设置，但不恢复资源选择，详情保持收起。

快捷键：`Ctrl+F` 搜索，`Ctrl+S` 保存详情，`F5` 刷新状态；资源列表中的 `Enter` 打开资源，`F2` 编辑别名。

## 主题

界面使用原生 ListView、GridView、输入框、按钮、菜单和 ContentDialog，保留 Windows 控件的焦点、按压及弹出动画。窗口配置 Mica 背景。

主题跟随 Windows 深浅色设置，尚无应用内主题切换或自定义颜色。Mica 效果受 Windows 版本、透明度、高对比度和节电状态影响；不适用时由系统提供纯色背景。见 [Microsoft Mica 材质说明](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)。

## 开发与打包

源码位于 `src/LocalResourceLibrary.WinUI`，核心位于 `src/LocalResourceLibrary.Core`。在 Windows 安装 .NET 10 SDK 后，从源码根目录构建；Windows SDK 构建工具通过 NuGet 还原。

请将源码放在 `C:\Dev\LocalResourceLibrary-WinUI-trial-source` 等较短目录。历史本机检查中，过深目录使 XAML 中间路径达到 263 字符并构建失败；缩短目录后构建通过。现成运行包不需要编译。

```powershell
dotnet build .\LocalResourceLibrary.WinUI.slnx -c Release -p:Platform=x64
dotnet run --project tests/LocalResourceLibrary.Checks
dotnet run --project tests/LocalResourceLibrary.ExplorerChecks
```

发布完整运行目录和 ZIP：

```powershell
& .\scripts\publish-winui.ps1 -Zip
```

输出为 `dist/LocalResourceLibrary-WinUI-win-x64` 及同名 ZIP，附带使用文档和实际依赖的许可通知。已有输出保留为 `.previous-winui-…`。

生成源码包：

```powershell
& .\scripts\package-winui-source.ps1
```

输出为 `dist/LocalResourceLibrary-WinUI-trial-source.zip`，包含 WinUI、Core、两组检查、开发脚本和文档；不包含另一套桌面前端、发布程序或资源库数据。文件名保留原源码包名称，不代表提供其他前端。

实际核验结果与未覆盖范围见 [核验记录](winui-verification.md)。项目原创源码采用 GPL-3.0-only，见 [LICENSE](../LICENSE)；依赖说明见 [WinUI 依赖许可](winui-third-party-notices.md)。
