# Local Resource Library

[简体中文](README.md)

A Windows local resource library built with WinUI 3. Add aliases, descriptions, notes, and project memberships to files and folders, with local search, eight view modes, natural sorting, and Chinese/English UI.

**Resources stay where they are.** Adding a resource stores its absolute path without copying, moving, or storing its contents. One resource can belong to several logical projects, all referencing the same item. WinUI 3 is the project's only desktop frontend.

## Available features

- Add files and folders with pickers or drag and drop. An already registered path reuses its existing item.
- Keep alias, description, and note separate. The alias is the preferred display name and does not change the actual file name.
- Create logical projects and select or clear memberships in the details panel. An item can belong to multiple projects.
- Search aliases, real file names, paths, descriptions, notes, and project names using local text search.
- Filter by all resources, recently opened resources, missing paths, or projects.
- Double-click to open resources; open the containing location or copy a path. Track open count and last-opened time.
- Show Windows file association icons in extra large, large, medium, small, list, details, tiles, and content views.
- Sort by name, date modified, type, size, or last opened, with ascending/descending order and folders first. Names use Windows natural sorting.
- Resize navigation and details panes. Save view, sorting, pane widths, and language locally.
- Store metadata in SQLite and handle unsaved edits with Save, Discard, and Cancel choices.

## Run

Use a 64-bit Windows 10 or Windows 11 desktop environment. Published releases include the required .NET and Windows App SDK runtime dependencies.

1. Extract the complete release folder and keep all its files together.
2. Open `LocalResourceLibrary.WinUI.exe`.
3. Add files or folders, describe them, and assign them to projects.

No account is required. The app provides no cloud sync, telemetry, or background network service. External applications used to open resources may have their own network behavior.

### Common actions

| Goal | Action |
| --- | --- |
| Give an item a readable name | Edit its alias; the actual file name stays the same |
| Explain what a resource contains and why it matters | Edit its description |
| Save temporary or personal information | Edit its note |
| Add or remove project membership | Select or clear memberships in details, then save |
| Open a resource | Double-click it or choose Open in details |
| Locate it in Explorer | Choose Open location |
| Get its saved path | Choose Copy path |
| Check resource availability | Choose Refresh status |
| Change view or sorting | Use View or Sort; details headers also support sorting |
| Switch language | Use the language selector at the top; the choice is saved |

Click Save after editing details. Selecting another resource or category, or closing the window, prompts you to save, discard, or cancel when changes remain. Search applies within the current sidebar category. Each whitespace-separated term must match at least one searchable field of the resource; choose All resources to search the whole library.

Adding a folder registers the folder itself, without importing its contents. Use Refresh status after externally moving a resource or disconnecting/reconnecting a location. Missing resources retain their records and descriptions.

Selecting a resource opens details. Clicking blank resource-area space, pressing `Esc`, or changing sidebar category clears selection and collapses details. The Details toolbar button opens or closes details for the current selection. Drag pane dividers to resize them; focused dividers also accept the left and right arrow keys.

Keyboard shortcuts: `Ctrl+F` focuses search, `Ctrl+S` saves details, and `F5` refreshes status. In the resource list, `Enter` opens the selected resource and `F2` edits its alias.

## Data and backup

The default database is:

```text
%LOCALAPPDATA%\LocalResourceLibrary\library.db
```

The database contains metadata, projects, and memberships, not the resources themselves. Backing up the library is not a backup of the original files. Language is stored in `settings.json`; view, sorting, and pane widths are stored in `explorer-settings.json` in the same directory. Only one app instance can open a data directory at a time.

**Fully exit the app before copying the entire `LocalResourceLibrary` data directory for backup.** Exit before restoring it, too. Moving the library to another computer requires making the resources available at their saved paths; the current UI does not offer path repair.

Use a separate data directory:

```powershell
.\LocalResourceLibrary.WinUI.exe --data-dir "C:\Temp\LocalResourceLibrary-Test"
```

Removing the program folder does not automatically remove the library stored in local application data.

## Build from source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Initial restore, build, or publish may require network access for NuGet dependencies and runtime packs. Windows SDK build tools are restored through NuGet. `global.json` selects a stable .NET 10 SDK and allows newer feature bands within that major version.

Keep the source in a short path such as `C:\Dev`; deep directories can exceed WinUI XAML intermediate-path limits.

```powershell
dotnet build .\LocalResourceLibrary.WinUI.slnx -c Release -p:Platform=x64
dotnet run --project tests/LocalResourceLibrary.Checks
dotnet run --project tests/LocalResourceLibrary.ExplorerChecks
```

These executable checks cover core behavior and the resource-browsing model. They do not establish complete UI or visual coverage.

Publish a self-contained Windows x64 release:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-winui.ps1 -Zip
```

The output is `dist/LocalResourceLibrary-WinUI-win-x64` and an optional archive with the same name plus `.zip`. Distribute the complete folder or archive. Existing output is retained under `dist` as `.previous-winui-…` after a successful replacement.

Package the source without requiring Git:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-winui-source.ps1
```

The output is `dist/LocalResourceLibrary-WinUI-trial-source.zip`, with a containing top-level folder, WinUI frontend, Core, checks, configuration, documentation, and development scripts. The script excludes build caches, databases, user settings, and logs. Archive entries have a stable order and timestamps. An existing source archive is retained as `.previous-winui-source-…`.

See the [usage guide (Chinese)](docs/winui-trial.md), [architecture](docs/architecture.md), [verification record (Chinese)](docs/winui-verification.md), [release checklist](docs/release-checklist.md), and [third-party notices](THIRD-PARTY-NOTICES.md).

## Scope and limitations

The current WinUI frontend does not offer physical-file rename, path repair, project editing/deletion, library-record removal, or a complete context menu. The related data and file-operation interfaces remain in Core but are not exposed by the UI. If another program moves or renames a resource, the saved path becomes invalid; the app does not scan the computer to guess its new location.

Search uses library metadata only, without content indexing, AI, or embeddings. URL collection, content previews, tags, global hotkey search, cloud sync, accounts, browser extensions, automatic classification, and network services are not implemented. The app is intended for one local user and offers no shared-database or cross-device collaboration.

The theme follows Windows light/dark settings, with no in-app theme switch. Mica availability depends on the operating system and settings. Application instructions and errors are available in Chinese and English; low-level Windows errors may use the operating system's language.

## License

This project's original source code is licensed under the **GNU General Public License v3.0 only (GPL-3.0-only)**. See [LICENSE](LICENSE) for the full text. Copyright (C) 2026 zhou-air.

Third-party components retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md) and the [WinUI dependency notices (Chinese)](docs/winui-third-party-notices.md).
