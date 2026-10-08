# Architecture

## Resource identity

```text
Physical resource → Item ↔ ProjectItem ↔ Project
```

The file system owns the actual resource. `Item` owns its logical context. `ProjectItem` stores memberships, so adding an item to another project does not duplicate its metadata or physical contents.

`Item` carries a permanent Item ID, a type, an absolute target path, alias, description, note, timestamps, and open count. Physical files also carry a Windows volume GUID and File ID when available. Item ID is independent of the path; the file identity is used only to identify and recover the physical file. Alias is independent of the actual file name. `Project` carries a name and description. SQLite persists records and membership constraints. The current application registers files and folders; URL collection and other resource types are not implemented.

## Project layout

| Project | Responsibility |
| --- | --- |
| `src/LocalResourceLibrary.WinUI` | The only desktop frontend: WinUI 3 window, localized strings, dialogs, pickers, drag and drop, resource browsing, startup, and local UI settings |
| `src/LocalResourceLibrary.Core` | Models, SQLite persistence, local search, item/project relationships, resource operations, and a Windows platform adapter |
| `tests/LocalResourceLibrary.Checks` | Executable core checks using temporary files and databases |
| `tests/LocalResourceLibrary.ExplorerChecks` | Executable checks of browsing models, sorting, draft preservation, settings, and metadata; not a full UI test |
| `scripts/publish-winui.ps1` | Self-contained Windows x64 folder publishing, dependency notices, and optional ZIP creation |
| `scripts/package-winui-source.ps1` | Source archive excluding generated output and local user data, without requiring Git |
| `LocalResourceLibrary.WinUI.slnx` | Solution containing the WinUI frontend, Core, and both check projects |

The UI invokes Core operations rather than embedding database statements in event handlers. `ISearchProvider` separates text search; `IResourcePlatform` separates Windows file and shell operations so checks can substitute a platform adapter. Neither extension changes item identity or membership.

The WinUI project owns its localization, settings, and error-formatting code. The Explorer checks link selected WinUI model/service sources and use lightweight UI stubs to exercise that logic without launching a window.

## Operations exposed by the UI

- **Add:** register an absolute path, capture physical-file identity when available, reuse an already registered item, and optionally add membership.
- **Edit details:** update alias, description, note, and selected memberships. Alias changes metadata only.
- **Create project:** create a logical collection with a name and description.
- **Delete project:** remove that project and its memberships, preserving all resource records and physical files.
- **Delete resource records:** delete one or more items and their memberships in a single SQLite transaction, preserving physical files.
- **Multiple selection:** native extended selection in both ListView and GridView; common right-click commands, select all, and batch path copying. Single-resource editing is hidden for multiple selection.
- **Remove current memberships:** remove selected items from the current project in one transaction, keeping their records and other memberships.
- **Search and browse:** filter library metadata, select a view mode, and sort by resource/file metadata.
- **Open:** check the saved path, recover a missing physical-file path by its stored identity when possible, then hand off to Windows and record a successful handoff. The app cannot establish whether a user then reads or edits the resource in the external program.
- **Detect missing:** check availability at the saved path and retain missing records. Status refresh does not recover relocated files.

Path-based deduplication does not identify every physical-file identity. Hard links, symbolic links, aliases for network locations, and externally changed paths can have different path representations.

## On-demand file recovery

When adding a physical file, the Windows adapter captures its volume GUID and File ID. Existing accessible files without an identity are backfilled during path checks or open. Unsupported file systems or unavailable identity APIs leave identity unset and preserve normal path-based behavior; already missing legacy files without an identity remain Missing.

Open always checks the stored path first. If it is missing, the adapter uses native `OpenFileById` on the saved volume and verifies the recovered file's identity. A successful recovery updates the target path and its path key before shell opening. Item ID, alias, description, note, project memberships, creation time, and the stored file identity remain unchanged. Open count and last-opened time retain their normal successful-open behavior. Failed recovery marks the item Missing while retaining its context.

Recovery is limited to physical files on the stored volume and runs only on user open. It does not scan directories or introduce FileSystemWatcher, background monitoring, startup services, USN Journal tracking, automatic folder-record rename tracking, or cross-volume tracking.

## Core operations not exposed by the current UI

Core retains the following interfaces and checks, but the current WinUI window does not provide commands for them:

- **Physical rename:** rename the resource while preserving item identity, metadata, and memberships. Folder renaming rebases registered descendant paths. File-system and database writes are separate; on metadata-write failure, the operation attempts to restore the original disk name.
- **Repair path:** update the saved reference to an existing user-selected resource without replacing its context.
- **Edit project description:** update the optional project description. The UI exposes name-only project renaming through the navigation context menu, preserving the existing description and memberships.

These are core capabilities rather than user-facing features. Project membership can be removed by clearing its checkbox and saving, or with the batch context-menu command.

## Persistence and UI settings

Default data lives below `%LOCALAPPDATA%\LocalResourceLibrary`. `--data-dir PATH` redirects the application to a separate local library. The SQLite file is `library.db`.

SQLite schema version 2 adds nullable file-identity fields. Version 1 databases migrate automatically, retaining existing Item IDs, metadata, and memberships; file identity is backfilled lazily when an existing file is accessible.

Language follows the Windows display language at startup: Chinese for Chinese cultures, English otherwise. Legacy `settings.json` language choices are ignored. View mode, sorting, and pane widths are saved in `explorer-settings.json`. Metadata remains as entered; changing language does not translate names, notes, descriptions, aliases, or paths. Startup does not restore a resource selection, so details start collapsed.

A mutex prevents two instances in the same Windows session from opening one data directory. This is not a multi-user shared-database service. Make backups after fully closing the app and copy the whole data directory.

The application registers a native notification-area icon at startup. A hidden top-level Win32 window receives icon callbacks and the `TaskbarCreated` broadcast to re-register the icon after Explorer restarts. Closing the WinUI window cancels destruction and hides it, retaining drafts and operations. Tray exit uses the existing unsaved-edits flow before closing the window, removing the icon and releasing the library lock. A second launch posts a restore message to the existing tray window for that data directory.

## Outside the current scope

No content indexing, document parsing, content previews, AI, embeddings, tags, URL capture, accounts, cloud sync, browser extension, global shortcut window, automatic classification, or background network service. Future additions must respect the registered library's scope.
