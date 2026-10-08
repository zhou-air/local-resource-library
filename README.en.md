# Local Resource Library

[简体中文](README.md)

A Windows local resource library built with WinUI 3. Add aliases, descriptions, notes, and project memberships to files and folders, with local search, eight view modes, natural sorting, and Chinese/English UI.

**Resources stay where they are.** Adding a resource stores its absolute path and, when supported on Windows, its volume identity and File ID, without copying, moving, or storing its contents. Each item has a permanent Item ID independent of its path. One resource can belong to several logical projects, all referencing the same item. WinUI 3 is the project's only desktop frontend.

## Available features

- Add files and folders with pickers or drag and drop. An already registered path reuses its existing item.
- Keep alias, description, and note separate. The alias is the preferred display name and does not change the actual file name.
- Create logical projects and select or clear memberships in the details panel. An item can belong to multiple projects.
- Rename a project from its context menu, preserving its description, memberships, and physical resources.
- Keep a notification-area icon from startup. The window close button hides the window; click the icon to restore it, or right-click and choose Exit library to quit.
- Delete projects or selected resource records using context menus, the toolbar, or `Delete`, preserving original files. Project context menus can also remove just the current membership.
- Use `Ctrl` for multiple selection, `Shift` for ranges, and `Ctrl+A` to select all. Delete records or copy paths in batches.
- Search aliases, real file names, paths, descriptions, notes, and project names using local text search.
- Filter by all resources, recently opened resources, missing paths, or projects.
- Double-click to open resources; open the containing location or copy a path. Track open count and last-opened time.
- When opening a file whose saved path is missing, use its stored volume identity and File ID to recover the same file on that volume, update its path, and preserve its alias, description, note, and project memberships.
- Show Windows file association icons in extra large, large, medium, small, list, details, tiles, and content views.
- Sort by name, date modified, type, size, or last opened, with ascending/descending order and folders first. Names use Windows natural sorting.
- Resize navigation and details panes. Save view, sorting, pane widths locally; the UI language follows Windows.
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
| Get saved paths | Choose Copy path; the context menu copies all selected paths |
| Delete resource records | Use the context menu, Delete resources toolbar button, or `Delete`; originals remain |
| Remove current memberships | In a project, select resources and choose Remove from project |
| Delete a project | Use its context menu or select it and use Delete project in the sidebar |
| Select multiple resources | `Ctrl`-click, `Shift`-click, Select all, or `Ctrl+A` |
| Check resource availability | Choose Refresh status |
| Change view or sorting | Use View or Sort; details headers also support sorting |
| Switch language | Follow the Windows display language automatically; restart the app after changing it |

Click Save after editing details. Selecting another resource or category, or exiting through the notification-area icon, prompts you to save, discard, or cancel when changes remain. Closing the window only hides it and keeps your draft. The icon may be inside the taskbar overflow area; Windows controls its placement. Clicking the icon or launching the app again restores the existing window. Search applies within the current sidebar category. Each whitespace-separated term must match at least one searchable field of the resource; choose All resources to search the whole library.

Adding a folder registers the folder itself, without importing its contents. Use Refresh status after externally moving a resource or disconnecting/reconnecting a location to check the saved path; refresh does not locate a new path. Missing resources retain their records and descriptions.

After an external file rename, opening the item triggers recovery. The app checks the saved path first, then uses its stored file identity if that path is missing. If the same file is found on the stored volume, the new path is saved and opened. Item ID, alias, description, note, and project memberships stay unchanged. A file that cannot be recovered is marked Missing. Existing files with valid paths receive file identities during path checks or open; already missing items without a stored identity remain Missing.

Selecting one resource opens details. Multiple selection hides individual editing and displays the selected count. Clicking blank resource-area space, pressing `Esc`, or changing sidebar category clears selection and collapses details. The Details toolbar button opens or closes details for the current selection. Drag pane dividers to resize them; focused dividers also accept the left and right arrow keys.

Keyboard shortcuts: `Ctrl+F` focuses search, `Ctrl+S` saves details, and `F5` refreshes status. In the resource area, `Ctrl+A` selects all current results and `Delete` deletes selected records. `Enter` opens a single selected resource and `F2` edits its alias.

## Data and backup

The default database is:

```text
%LOCALAPPDATA%\LocalResourceLibrary\library.db
```

The database contains metadata, file identities, projects, and memberships, not the resources themselves. Existing schema version 1 databases are upgraded automatically to version 2. Backing up the library is not a backup of the original files. The UI follows the Windows display language (Chinese for Chinese systems, English otherwise); legacy language choices in `settings.json` are ignored. view, sorting, and pane widths are stored in `explorer-settings.json` in the same directory. Only one app instance can open a data directory at a time.

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

The output is `dist/LocalResourceLibrary-WinUI-win-x64` and an optional archive with the same name plus `.zip`. Distribute the complete folder or archive. After successful publishing, replaced outputs are removed by default. Add `-KeepPrevious` to retain them.

Package the source without requiring Git:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-winui-source.ps1
```

The output is `dist/LocalResourceLibrary-WinUI-trial-source.zip`, with a containing top-level folder, WinUI frontend, Core, checks, configuration, documentation, and development scripts. The script excludes build caches, databases, user settings, and logs. Archive entries have a stable order and timestamps. After successful packaging, the replaced archive is removed by default. Add `-KeepPrevious` to retain it.

This is a personal-use project. Agents do not need visual or screenshot review after completing tasks unless the user explicitly requests it. Relevant builds and functional checks still apply; see [project instructions (Chinese)](AGENTS.md).

See the [usage guide (Chinese)](docs/winui-trial.md), [architecture](docs/architecture.md), [verification record (Chinese)](docs/winui-verification.md), [release checklist](docs/release-checklist.md), and [third-party notices](THIRD-PARTY-NOTICES.md).

## Scope and limitations

The current WinUI frontend does not offer physical-file rename, manual path repair, or project description editing; these interfaces remain in Core. Project rename, project deletion, library-record deletion, multiple selection, and common context-menu actions are available. Deleting resource records removes their memberships in all projects. Deleting a project removes only that project and its memberships. Both preserve original files.

Automatic recovery runs only when the user opens a physical file and requires a usable Windows volume identity and File ID. If identity capture is unavailable, normal path-based opening still works; a missing path remains Missing. The app does not scan the file system or use FileSystemWatcher, background monitoring, startup services, or the USN Journal. It does not automatically track folder-item renames or cross-volume moves.

Search uses library metadata only, without content indexing, AI, or embeddings. URL collection, content previews, tags, global hotkey search, cloud sync, accounts, browser extensions, automatic classification, and network services are not implemented. The app is intended for one local user and offers no shared-database or cross-device collaboration.

The theme follows Windows light/dark settings, with no in-app theme switch. Mica availability depends on the operating system and settings. Application instructions and errors are available in Chinese and English; low-level Windows errors may use the operating system's language.

## License

This project's original source code is licensed under the **GNU General Public License v3.0 only (GPL-3.0-only)**. See [LICENSE](LICENSE) for the full text. Copyright (C) 2026 zhou-air.

Third-party components retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md) and the [WinUI dependency notices (Chinese)](docs/winui-third-party-notices.md).
