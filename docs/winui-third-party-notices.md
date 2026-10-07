# WinUI 3 依赖许可说明

日期：2026-10-07

本文件补充根目录 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)，说明 WinUI 界面的依赖。运行时版本以发布目录中的 `LocalResourceLibrary.WinUI.runtimeconfig.json` 为准。

项目引用 Microsoft.WindowsAppSDK 2.5.1 和 Microsoft.Windows.SDK.BuildTools 10.0.26100.9169。现有还原结果包括 Microsoft.WindowsAppSDK.Runtime 2.5.1 与 Microsoft.WindowsAppSDK.WinUI 2.3.9。依赖包提供的 `license.txt`、`NOTICE.txt` 等通知随运行包保存在 `docs/licenses/packages/<包名>/<版本>/` 中。

发布脚本从构建时的 `project.assets.json` 收集实际依赖包的顶层许可和通知，包括原生 WinUI、Windows App SDK、WebView2 和其他依赖。Windows SDK 构建工具的 `sdk_license.txt` 也随包保留；构建工具不属于运行程序时需要另行安装的组件。

自包含运行包的 .NET 许可与第三方通知，从运行配置指定版本的还原运行时包复制到 `docs/licenses/runtime/`。根目录中随源码提供的 Microsoft.Data.Sqlite 和 SQLitePCLRaw 许可也保留在运行包中。

以上文件保留第三方包提供的许可与通知。本项目原创源码采用 GPL-3.0-only，见根目录 [LICENSE](../LICENSE)。重新发布时使用 `scripts/publish-winui.ps1`，以收集该次构建实际使用的包和运行时通知。
