# Third-party components / 第三方组件

These notices apply to dependencies. Local Resource Library's original source code is licensed under GPL-3.0-only; see [LICENSE](LICENSE). Third-party components retain their own licenses.

以下说明针对第三方依赖；本应用原创源码采用 GPL-3.0-only，见 [LICENSE](LICENSE)。第三方组件保留各自的许可证。

| Component | Version | Notice |
| --- | --- | --- |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.12 | MIT; Microsoft package metadata, .NET Foundation and Contributors upstream notice. [Included license](docs/licenses/Microsoft.Data.Sqlite-MIT.txt), [upstream license](https://github.com/dotnet/efcore/blob/main/LICENSE.txt) |
| SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.core, SQLitePCLRaw.lib.e_sqlite3, SQLitePCLRaw.provider.e_sqlite3 | 2.1.12 | Apache-2.0; Copyright 2014–2024 SourceGear, LLC. [Included license](docs/licenses/SQLitePCLRaw-Apache-2.0.txt), [versioned upstream license](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT) |
| SQLite native engine, provided through SQLitePCLRaw | As supplied by the package above | SQLite's upstream source is dedicated to the public domain. [Upstream notice](https://www.sqlite.org/copyright.html) |
| Microsoft.WindowsAppSDK | 2.5.1 project reference | Windows App SDK and WinUI dependency licenses and notices are copied from the exact restored packages into release `docs/licenses/packages/`. See [WinUI dependency notices](docs/winui-third-party-notices.md). |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.9169 project reference | The restored build-tools package's SDK license is preserved by the publish script. It is a build dependency, not a separately required runtime installation. |
| .NET runtime | Exact versions recorded in the published `LocalResourceLibrary.WinUI.runtimeconfig.json` | The self-contained release includes the runtime. The publish script copies licenses and third-party notices from the exact restored runtime packs into release `docs/licenses/runtime/`. |

NuGet versions above are taken from project references and resolved package metadata. Review these notices when dependencies change. Keep license and notice files with the complete distribution.

WinUI publishing collects the restored dependency packages' top-level licenses and notices, including native Windows App SDK/WinUI and other transitive dependencies. See [license sources](docs/licenses/SOURCES.md) for the checked-in SQLite notice sources.

上表版本来自项目引用及已解析的依赖元数据。升级依赖后需同步检查。发布脚本会收集实际包和运行时的许可通知；分发时请保留完整运行目录。
