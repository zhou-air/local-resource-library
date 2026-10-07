# WinUI 3 试用版依赖许可说明

日期：2026-10-07

本文件补充原 `THIRD-PARTY-NOTICES.md`，说明新 WinUI 前端的原生依赖。原文中的 WPF 运行配置名仍适用于保留的 WPF 版本；新版本以 `LocalResourceLibrary.WinUI.runtimeconfig.json` 为准。

本次使用 Microsoft.WindowsAppSDK 2.5.1，包含 Microsoft.WindowsAppSDK.Runtime 2.5.1 和 Microsoft.WindowsAppSDK.WinUI 2.3.9。这些包的原始 `license.txt`、`NOTICE.txt` 随运行包保存在 `docs/licenses/packages/<包名>/<版本>/` 中。Windows App SDK 的 Microsoft 许可文本与共享 Core 的 SQLite 依赖许可分别保留。

发布脚本从本次还原的 `project.assets.json` 收集实际依赖包的顶层许可与通知文件，包括原生 WinUI、Windows App SDK、WebView2 和其他依赖。Windows SDK 构建工具的 `sdk_license.txt` 也随包保留。SDK 构建工具不属于程序运行所需的另行安装项。

新前端自包含运行包附带 Microsoft.NETCore.App 10.0.10；其原始许可和第三方通知从对应版本的运行时包复制到 `docs/licenses/runtime/Microsoft.NETCore.App/`。原 `docs/licenses/` 中的 Microsoft.Data.Sqlite、SQLitePCLRaw 许可仍随包保留。

以上文件保留第三方包提供的许可和通知；本项目原创源码采用 GPL-3.0-only，见根目录 [LICENSE](../LICENSE)。重新发布时请使用 `scripts/publish-winui.ps1`，以收集该次构建所实际使用的包与运行时通知。
