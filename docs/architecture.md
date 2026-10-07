# Architecture

## Resource identity

```text
Physical resource → Item ↔ ProjectItem ↔ Project
```

The file system owns the actual resource. `Item` owns its logical context. `ProjectItem` stores memberships, so adding an item to another project does not duplicate its metadata or physical contents.

`Item` carries a stable ID, a type, an absolute target path, alias, description, note, timestamps, and open count. Alias is independent of the actual file name. `Project` carries a name and description. SQLite persists records and membership constraints. The current application registers files and folders; URL collection and other resource types are not implemented.

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

- **Add:** register an absolute path, reuse an already registered item, and optionally add membership.
- **Edit details:** update alias, description, note, and selected memberships. Alias changes metadata only.
- **Create project:** create a logical collection with a name and description.
- **Search and browse:** filter library metadata, select a view mode, and sort by resource/file metadata.
- **Open:** hand off to Windows and record a successful handoff. The app cannot establish whether a user then reads or edits the resource in the external program.
- **Detect missing:** check availability at the saved path and retain missing records; do not scan the computer to discover a new location.

Path-based deduplication does not identify every physical-file identity. Hard links, symbolic links, aliases for network locations, and externally changed paths can have different path representations.

## Core operations not exposed by the current UI

Core retains the following interfaces and checks, but the current WinUI window does not provide commands for them:

- **Physical rename:** rename the resource while preserving item identity, metadata, and memberships. Folder renaming rebases registered descendant paths. File-system and database writes are separate; on metadata-write failure, the operation attempts to restore the original disk name.
- **Repair path:** update the saved reference to an existing user-selected resource without replacing its context.
- **Edit or delete project:** update project metadata or remove project records and memberships.
- **Remove item:** remove library metadata and memberships without deleting the physical resource.

These are core capabilities rather than user-facing features. Project membership can be removed through the current UI by clearing its checkbox and saving.

## Persistence and UI settings

Default data lives below `%LOCALAPPDATA%\LocalResourceLibrary`. `--data-dir PATH` redirects the application to a separate local library. The SQLite file is `library.db`.

Language is saved in `settings.json`. View mode, sorting, and pane widths are saved in `explorer-settings.json`. Metadata remains as entered; changing language does not translate names, notes, descriptions, aliases, or paths. Startup does not restore a resource selection, so details start collapsed.

A mutex prevents two instances in the same Windows session from opening one data directory. This is not a multi-user shared-database service. Make backups after fully closing the app and copy the whole data directory.

## Outside the current scope

No content indexing, document parsing, content previews, AI, embeddings, tags, URL capture, accounts, cloud sync, browser extension, global shortcut window, automatic classification, or background network service. Future additions must respect the registered library's scope.
