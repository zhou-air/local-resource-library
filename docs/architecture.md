# Architecture

## Resource identity

```text
File / Folder / URL → Item ↔ ProjectItem ↔ Project
```

The file system or website owns the actual resource. `Item` owns its logical context. `ProjectItem` stores memberships, so adding an item to another project does not duplicate its metadata or physical contents.

`Item` carries a permanent Item ID, a type (`file`, `folder`, or `url`), an absolute target path or HTTP/HTTPS URL, alias, description, note, timestamps, and open count. Physical files also carry a Windows volume GUID and File ID when available; URLs can carry a cached favicon BLOB. Item ID is independent of the target; the file identity is used only to identify and recover the physical file. Alias is independent of the actual file name or URL. `Project` carries a name and description. The existing SQLite database persists all three types and their shared memberships.

## Project layout

| Project | Responsibility |
| --- | --- |
| `src/LocalResourceLibrary.WinUI` | The only desktop frontend: WinUI 3 window, localized strings, dialogs, pickers, drag and drop, resource browsing, startup, and local UI settings |
| `src/LocalResourceLibrary.Core` | Models, SQLite persistence, local search, item/project relationships, resource operations, and a Windows platform adapter |
| `src/LocalResourceLibrary.Mcp` | Independent .NET 10 official-SDK stdio server; delegates all writes to Core, without WinUI, network listeners, content parsing, or background scanning |
| `tests/LocalResourceLibrary.Checks` | Executable core checks using temporary files and databases |
| `tests/LocalResourceLibrary.ExplorerChecks` | Executable checks of browsing models, sorting, draft preservation, settings, and metadata; not a full UI test |
| `tests/LocalResourceLibrary.UrlChecks` | Deterministic URL-fetch transport checks, partial results, timeout/cancellation, icon bounds, and manual-edit/stale-result protection |
| `scripts/publish-winui.ps1` | Self-contained Windows x64 folder publishing, dependency notices, and optional ZIP creation |
| `scripts/package-winui-source.ps1` | Source archive excluding generated output and local user data, without requiring Git |
| `tests/LocalResourceLibrary.CoreConcurrencyChecks` | Isolated concurrent startup/migration, partial patches, membership deltas, batch rollback and UI edit conflicts |
| `tests/LocalResourceLibrary.McpChecks` | Real stdio client/server integration including three types, protocol discovery, permissions, batches, concurrent clients and file identity recovery |
| `scripts/publish-mcp.ps1` | Standalone self-contained MCP output under artifacts, with actual-path Codex configuration and dependency notices |
| `LocalResourceLibrary.WinUI.slnx` | Solution containing WinUI, Core, MCP and all five check projects |

The UI invokes Core operations rather than embedding database statements in event handlers. `ISearchProvider` separates text search; `IResourcePlatform` separates Windows file and shell operations so checks can substitute a platform adapter. Neither extension changes item identity or membership.

The WinUI project owns its localization, settings, and error-formatting code. The Explorer checks link selected WinUI model/service sources and use lightweight UI stubs to exercise that logic without launching a window.

## Operations exposed by the UI

- **Add:** register an absolute path or URL, capture physical-file identity when available, reuse an already registered item, and optionally add memberships. Adding an existing URL only adds selected memberships and never replaces its metadata or favicon.
- **Edit details:** update alias, description, note, and selected memberships; URLs also expose the complete editable target. The URL context menu opens the same native edit dialog. Alias changes metadata only.
- **Create project:** create a logical collection with a name and description.
- **Delete project:** remove that project and its memberships, preserving all resource records and physical files.
- **Delete resource records:** delete one or more items and their memberships in a single SQLite transaction, preserving physical files.
- **Multiple selection:** native extended selection in both ListView and GridView; type-specific right-click commands, select all, and batch path/URL copying. Single-resource editing is hidden for multiple selection.
- **Remove current memberships:** remove selected items from the current project in one transaction, keeping their records and other memberships.
- **Search and browse:** filter library metadata, select a view mode, and sort by resource/file metadata.
- **Open:** check physical paths and recover missing physical files by identity when possible; URLs go to the Windows default browser. Record a successful Windows handoff in the shared open history. The app cannot establish whether the external program then loads the resource successfully.
- **Detect missing:** check physical availability at the saved path and retain missing records. Status refresh does not recover relocated files or test website availability.

Path-based deduplication does not identify every physical-file identity. Hard links, symbolic links, aliases for network locations, and externally changed paths can have different path representations.

URL deduplication trims outer whitespace and uses exact text comparison. Domain casing, explicit ports, HTTP/HTTPS, paths, query strings, and fragments remain distinct. URL records never invoke physical-path checks, recovery, or native Shell icon extraction. Their modification sort uses the Item's saved update time, and size is blank.

## Website metadata

User URL add/edit operations fetch optional title, description, and favicon asynchronously with an 8-second total budget shared by page, redirects, and icon requests. Startup, list browsing, and status refresh make no website requests. Failure or partial results never prevents saving a valid URL.

The edit draft tracks manually changed aliases and descriptions, including empty values; existing fields are protected when editing a saved URL. URL revisions reject stale request results. Favicon images are stored in the same SQLite Item BLOB, bounded to 512 KiB PNG/JPEG/GIF/ICO; decoding on the WinUI thread is asynchronous, with a globe fallback on failure. Changed favicon revisions also reject stale image decoding. All resource types reuse the same eight views, search, sorting, selection, and project relationships.

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

SQLite schema version 4 retains Item's `url` type and nullable favicon BLOB and adds Project's `is_pinned` and `sort_order` fields, preserving physical-resource rows, identity fields, metadata, and memberships. Version 1, 2 and 3 databases migrate automatically; existing projects start unpinned in their prior alphabetical order. New projects append to the normal group; pinning or unpinning appends to the destination group. Reordering is transactional and restricted to the same pinned state. Version 2's nullable file-identity fields retain their behavior; identities are backfilled lazily when an existing physical file is accessible. No separate URL database is created.

Language follows the Windows display language at startup: Chinese for Chinese cultures, English otherwise. Legacy `settings.json` language choices are ignored. View mode, sorting, and pane widths are saved in `explorer-settings.json`. Metadata remains as entered; changing language does not translate names, notes, descriptions, aliases, or paths. Startup does not restore a resource selection, so details start collapsed.

A mutex limits WinUI to one window instance per data directory in a Windows session. MCP processes do not acquire this window lock and may run independently or alongside WinUI. This is a single-user, local library, not a multi-machine shared database. Make backups after fully closing WinUI and every MCP client/server process, copying the whole data directory.

## MCP and concurrent metadata edits

The official C# SDK owns JSON-RPC framing, initialization, tool discovery and stdio lifecycle. Every client launches its own lightweight process; logs go to stderr. Queries reuse `LocalTextSearchProvider` and read fresh Core snapshots. The adapter exposes only resource metadata and targets, leaving content reading to the agent. URL availability is explicitly untested. `get_resource` can recover a file's saved identity without opening it or incrementing open history, so its tool annotation honestly allows metadata writes.

Core provides atomic `AddResource`, `PatchResourceMetadata`, `PatchProject`, `ChangeResourceProjects` and `ApplyResourceMetadataBatch` operations. Existing path/URL addition routes share the same registration implementation, permanent Item IDs, deduplication and identity capture. Omitting a patch field preserves it; an empty value clears it. Project delta operations only add/remove named associations. SQL remains inside Core. SQLite uses WAL, foreign keys, unique constraints and a ten-second busy timeout; migration reads its version after beginning the same immediate transaction used for upgrades.

Batch previews do not write the database. A random process-local token binds 1–100 immutable patches and expected resource snapshots, expires after ten minutes and is consumed once. Committing requires explicit startup opt-in and client-side user confirmation; a token itself is not evidence of human approval. Core compares the expected context inside the write transaction and commits all patches or returns item-specific failures without writing any. No deletion tool is exposed. See [MCP integration](mcp.md).

WinUI reloads shared metadata with Refresh resources and status / F5, retaining existing unsaved-edit choices. Its immutable draft baseline feeds `ApplyResourceEdit`, which checks edited fields for conflicts and applies only changed fields and membership deltas in one transaction. Agent changes to unrelated fields/associations survive UI saves. Same-field conflicts reject saving and retain the local draft. Project renaming patches only the name and preserves external description changes. No polling or file-system watcher is added.

The application registers a native notification-area icon at startup. A hidden top-level Win32 window receives icon callbacks and the `TaskbarCreated` broadcast to re-register the icon after Explorer restarts. Closing the WinUI window cancels destruction and hides it, retaining drafts and operations. Tray exit uses the existing unsaved-edits flow before closing the window, removing the icon and releasing the library lock. A second launch posts a restore message to the existing tray window for that data directory.

## Outside the current scope

No content indexing, document parsing, content previews, AI, embeddings, tags, accounts, cloud sync, browser extension, browser-bookmark import, global shortcut window, automatic classification, or background network service. Future additions must respect the registered library's scope. Functional checks do not constitute native UI or visual acceptance.
