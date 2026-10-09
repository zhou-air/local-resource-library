# Local Resource Library

[简体中文](README.md)

A Windows local resource library built with WinUI 3. Add aliases, descriptions, notes, and project memberships to files, folders, and URLs, with local search, eight view modes, natural sorting, and Chinese/English UI.

**Files stay where they are.** Adding a file or folder stores its absolute path and, when supported on Windows, its volume identity and File ID, without copying, moving, or storing its contents. URLs use the same Item model and SQLite library, with optional cached website icons. Each item has a permanent Item ID independent of its path. One resource can belong to several logical projects, all referencing the same item. WinUI 3 is the project's only desktop frontend.

Resource marquee selection, batch drag/drop, Copy/Cut/Paste and 100-step session Undo/Redo share the existing Core service. See [resource interactions](docs/resource-interactions.md).

## AI agents and MCP

The independent `LocalResourceLibrary.Mcp` stdio server lets Codex and other MCP clients search and manage saved files, folders and URLs through the existing Core and SQLite library. WinUI is optional. It supports partial metadata patches, logical projects, multiple memberships, and atomic batch commits after a preview and client confirmation.

Run `powershell -ExecutionPolicy Bypass -File .\scripts\publish-mcp.ps1`, then append the generated `codex-mcp.toml` to your Codex configuration. See [MCP integration](docs/mcp.md) for tool parameters, permissions and testing. In WinUI, use Refresh resources and status / F5 to reload agent edits. Local drafts update only edited fields; conflicts on edited fields reject saving and retain the draft.

## Available features

- Add files and folders with pickers or drag and drop. An already registered path reuses its existing item.
- Choose Add resources → Add URL to enter or paste an HTTP/HTTPS URL. Fetch the title, description, and favicon automatically; failed fetching still allows saving. URLs can belong to multiple projects.
- Keep alias, description, and note separate. The alias is the preferred display name and does not change the actual file name.
- Create logical projects and manage memberships by dragging resources or using Copy, Cut and Paste. An item can belong to multiple projects.
- Rename a project from its context menu, preserving its description, memberships, and physical resources.
- Organize projects into flat groups with persisted expansion, independent group ordering, and drag-and-drop project movement. Deleting a group moves its projects to Ungrouped and preserves resources. Pinned projects retain a separate section.
- Set a project's preset color or group from its context menu or properties. Only the outline folder icon uses the color; names, counts, and selected backgrounds retain the existing theme.
- Project and group IDs are permanent and read-only. Copy a Project ID from Advanced information in project properties. MCP name lookup returns candidates; project and membership operations use IDs.
- Keep a notification-area icon from startup. The window close button hides the window; click the icon to restore it, or right-click and choose Exit library to quit.
- Delete projects or selected resource records using context menus, the toolbar, or `Delete`, preserving original files. Project context menus can also remove just the current membership.
- Use `Ctrl` for multiple selection, `Shift` for ranges, and `Ctrl+A` to select all. Delete records or copy paths and URLs in batches.
- Search aliases, real file names or website hosts, paths or URLs, descriptions, notes, and project names using local text search.
- Filter by all resources, recently opened resources, missing paths, or projects.
- Double-click to open resources; open the containing location or copy a path. Track open count and last-opened time.
- Double-click URLs to open the Windows default browser and record opening history. Use Edit URL or Copy URL from the context menu; edit the complete URL and shared metadata in details. Deleting a URL removes its library record only.
- When opening a file whose saved path is missing, use its stored volume identity and File ID to recover the same file on that volume, update its path, and preserve its alias, description, note, and project memberships.
- Show Windows file association icons for physical resources and cached favicons for URLs, with a globe fallback. All resource types share extra large, large, medium, small, list, details, tiles, and content views.
- Sort by name, date modified, type, size, or last opened, with ascending/descending order and folders first. Names use Windows natural sorting.
- Resize navigation and details panes. Save view, sorting, pane widths locally; the UI language follows Windows.
- Store metadata in SQLite and handle unsaved edits with Save, Discard, and Cancel choices.

## Run

Use a 64-bit Windows 10 or Windows 11 desktop environment. Published releases include the required .NET and Windows App SDK runtime dependencies.

1. Extract the complete release folder and keep all its files together.
2. Open `LocalResourceLibrary.WinUI.exe`.
3. Add files, folders, or URLs, describe them, and assign them to projects.

No account is required. Startup and browsing make no background network requests; adding or editing a URL fetches optional website information. The app provides no cloud sync, telemetry, or background network service. External applications used to open resources may have their own network behavior.

### Common actions

| Goal | Action |
| --- | --- |
| Give an item a readable name | Edit its alias; the actual file name stays the same |
| Explain what a resource contains and why it matters | Edit its description |
| Save temporary or personal information | Edit its note |
| Add or remove project membership | Drag into a project, or copy/cut and paste there; use Remove from project to detach |
| Open a resource | Double-click it or choose Open in details |
| Locate it in Explorer | Choose Open location |
| Get saved paths | Choose Copy path; the context menu copies all selected paths |
| Save or edit a URL | Choose Add resources → Add URL; edit details or use the Edit URL context menu |
| Get saved URLs | Choose Copy URL; mixed selection can copy paths and URLs together |
| Delete resource records | Use the context menu, Delete resources toolbar button, or `Delete`; originals remain |
| Remove current memberships | In a project, select resources and choose Remove from project |
| Delete a project | Use its context menu or select it and use Delete project in the sidebar |
| Select multiple resources | `Ctrl`-click, `Shift`-click, blank-area marquee, or `Ctrl+A` |
| Check resource availability | Choose Refresh resources and status |
| Change view or sorting | Use View or Sort; details headers also support sorting |
| Switch language | Follow the Windows display language automatically; restart the app after changing it |

Click Save after editing details. Selecting another resource or category, or exiting through the notification-area icon, prompts you to save, discard, or cancel when changes remain. Closing the window only hides it and keeps your draft. The icon may be inside the taskbar overflow area; Windows controls its placement. Clicking the icon or launching the app again restores the existing window. Search applies within the current sidebar category. Each whitespace-separated term must match at least one searchable field of the resource; choose All resources to search the whole library.

Adding a folder registers the folder itself, without importing its contents. Use Refresh resources and status after externally moving a resource or disconnecting/reconnecting a location to check the saved path; refresh does not locate a new path. Missing resources retain their records and descriptions.

URL deduplication trims outer whitespace, then compares the exact text. Domain casing, ports, HTTP/HTTPS, paths, queries, and `#fragments` remain distinct. Adding an existing URL only adds selected memberships, preserving its saved alias, description, note, and favicon.

Metadata fetching is asynchronous, with an 8-second total budget covering the page, redirects, and favicon. Partial results and fetching failures still allow saving. Automatic fill preserves manually edited aliases and descriptions, including intentionally empty values; editing an existing URL also preserves its saved fields. Results for an earlier URL are discarded after the target changes. Refresh resources and status does not test website availability. Cached icons use a BLOB in the existing SQLite database, limited to 512 KiB PNG, JPEG, GIF, or ICO images; decoding failure uses the globe fallback.

After an external file rename, opening the item triggers recovery. The app checks the saved path first, then uses its stored file identity if that path is missing. If the same file is found on the stored volume, the new path is saved and opened. Item ID, alias, description, note, and project memberships stay unchanged. A file that cannot be recovered is marked Missing. Existing files with valid paths receive file identities during path checks or open; already missing items without a stored identity remain Missing.

Selecting one resource opens details. Multiple selection hides individual editing and displays the selected count. Clicking blank resource-area space, pressing `Esc`, or changing sidebar category clears selection and collapses details. The Details toolbar button opens or closes details for the current selection. Drag pane dividers to resize them; focused dividers also accept the left and right arrow keys.

Keyboard shortcuts: `Ctrl+F` focuses search, `Ctrl+S` saves details, and `F5` reloads resources and checks saved paths. In the resource area, `Ctrl+A` selects all current results and `Delete` deletes selected records. `Enter` opens a single selected resource and `F2` edits its alias.

## Data and backup

The default database is:

```text
%LOCALAPPDATA%\LocalResourceLibrary\library.db
```

The database contains metadata, file identities, URLs, cached website icons, projects, groups and memberships, without file contents or page bodies. Schema version 1–4 databases upgrade automatically to version 5, retaining project IDs, existing records, memberships, pinning and order. Legacy projects use the default icon color and Ungrouped. Backing up the library is not a backup of the original files. The UI follows the Windows display language (Chinese for Chinese systems, English otherwise); legacy language choices in `settings.json` are ignored. View, sorting, pane widths and group expansion are stored locally. Only one WinUI window instance can open a data directory at a time; independent MCP processes can run alongside it using SQLite transactions. Upgrade WinUI and MCP together; older binaries reject the newer database schema.

**Fully exit WinUI and all MCP clients/server processes before copying the entire `LocalResourceLibrary` data directory for backup.** Exit those processes before restoring it, too. Moving the library to another computer requires making the resources available at their saved paths; the current UI does not offer path repair.

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
dotnet run --project tests/LocalResourceLibrary.UrlChecks
dotnet run --project tests/LocalResourceLibrary.CoreConcurrencyChecks
dotnet run --project tests/LocalResourceLibrary.McpChecks
```

These five executable check projects cover Core behavior, browsing models, URL fetching/edit drafts, database concurrency and real stdio MCP calls. They do not establish complete UI, live website availability, or visual coverage.

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

The current WinUI frontend does not offer physical-file rename or manual path repair; these interfaces remain in Core. Project properties, groups, colors, rename, deletion, library-record deletion, multiple selection, and common context-menu actions are available. Project names and group names each remain unique under their existing case-insensitive rules, but names are only display and lookup values, never permanent identities. Groups cannot be nested. Deleting resource records removes their memberships in all projects. Deleting a project removes only that project and its memberships. Both preserve original files.

WinUI recovery runs when a user opens a physical file; MCP get_resource can also explicitly use the same recovery logic and requires a usable Windows volume identity and File ID. If identity capture is unavailable, normal path-based opening still works; a missing path remains Missing. The app does not scan the file system or use FileSystemWatcher, background monitoring, startup services, or the USN Journal. It does not automatically track folder-item renames or cross-volume moves.

Search uses library metadata only, without content indexing, AI, or embeddings. Content previews, tags, global hotkey search, cloud sync, accounts, browser extensions, automatic classification, and hosted network services are not implemented. URL collection does not import or modify browser bookmarks. The app is intended for one local user and supports multiple local AI clients sharing its library, without multi-machine shared databases or cross-device collaboration.

The theme follows Windows light/dark settings, with no in-app theme switch. Mica availability depends on the operating system and settings. Application instructions and errors are available in Chinese and English; low-level Windows errors may use the operating system's language.

## License

This project's original source code is licensed under the **GNU General Public License v3.0 only (GPL-3.0-only)**. See [LICENSE](LICENSE) for the full text. Copyright (C) 2026 zhou-air.

Third-party components retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md) and the [WinUI dependency notices (Chinese)](docs/winui-third-party-notices.md).
