# Third-party components / 第三方组件

These notices apply to dependencies. Local Resource Library's original source code is licensed under GPL-3.0-only; see [LICENSE](LICENSE). Third-party components retain their own licenses.

以下说明针对第三方依赖；本应用原创源码采用 GPL-3.0-only，见 [LICENSE](LICENSE)。第三方组件保留各自的许可证。

| Component | Version used by V0.1 | Notice |
| --- | --- | --- |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.12 | MIT; Microsoft package metadata, .NET Foundation and Contributors upstream notice. [Included license](docs/licenses/Microsoft.Data.Sqlite-MIT.txt), [upstream license](https://github.com/dotnet/efcore/blob/main/LICENSE.txt) |
| SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.core, SQLitePCLRaw.lib.e_sqlite3, SQLitePCLRaw.provider.e_sqlite3 | 2.1.12 | Apache-2.0; Copyright 2014–2024 SourceGear, LLC. [Included license](docs/licenses/SQLitePCLRaw-Apache-2.0.txt), [versioned upstream license](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT) |
| SQLite native engine, provided through SQLitePCLRaw | As supplied by the package above | SQLite's upstream source is dedicated to the public domain. [Upstream notice](https://www.sqlite.org/copyright.html) |
| .NET and Windows Desktop runtime | Exact versions recorded in the published `LocalResourceLibrary.runtimeconfig.json` | A self-contained release includes these runtimes. The publish script copies their license and third-party notices from the exact restored runtime packs into `docs/licenses/runtime/` in the release. |

The NuGet dependency versions above are taken from the project reference and its resolved package metadata. Review this file when dependencies change. Bundled runtime notices may describe components used internally by .NET; keep those files with the distribution.

上表的 NuGet 版本来自项目及已解析的依赖元数据。升级依赖后需同步检查本文件。发布脚本会保留实际打包运行时的许可文件；分发时请保留完整发布目录。
