# Architecture

## Resource identity

```text
Physical resource → Item ↔ ProjectItem ↔ Project
```

The physical file system owns the actual resource. `Item` owns its logical context. `ProjectItem` stores memberships, so adding an item to another project does not duplicate its metadata or physical contents.

`Item` carries a stable ID, a type, an absolute target path, alias, description, note, timestamps, and open count. Alias is independent of the real file name. `Project` carries a name and description. SQLite persists records and membership constraints.

V0.1 activates file and folder resource types. The type field and resource-operation boundary are intended to accommodate additional resource kinds later; URL collection and other new types are not implemented.

## Project layout

| Project | Responsibility |
| --- | --- |
| `src/LocalResourceLibrary.App` | WPF window, Chinese/English strings, dialogs, drag and drop, application startup, and saved language settings |
| `src/LocalResourceLibrary.Core` | Models, SQLite persistence, local search, item/project relationships, resource operations, and a Windows platform adapter |
| `tests/LocalResourceLibrary.Checks` | Executable checks of persistence and meaningful resource-library behavior against temporary data |
| `tests/LocalResourceLibrary.UiChecks` | Windows desktop integration checks; distinct from exhaustive manual UI and visual review |
| `scripts/publish.ps1` | Self-contained Windows x64 folder publishing and optional ZIP creation |
| `scripts/package-source.ps1` | Source archive from an explicit allowlist; excludes generated output and user data without requiring Git |

The UI invokes core operations rather than embedding database statements in event handlers. `ISearchProvider` separates the text-search implementation; `IResourcePlatform` separates Windows file and shell operations so checks can substitute a platform adapter. Neither extension changes item identity or project membership.

## Important operation boundaries

- **Add:** register the absolute path; reuse the item already registered for that path; optionally add membership.
- **Edit alias:** update metadata only.
- **Rename physical file or folder:** rename the real resource and update the stored target while preserving the item ID and its relationships. Folder changes rebase registered descendant paths. File-system and database writes are separate systems; errors must be reported rather than presented as success, with an attempt to restore the original disk name if the metadata update fails.
- **Repair path:** update the reference to a user-selected existing resource while retaining metadata and memberships.
- **Remove membership:** remove only the item/project relationship.
- **Remove item or project:** remove library records and related memberships; do not delete physical resources.
- **Open:** hand off to Windows and record the successful handoff. The app cannot establish whether a user subsequently reads or edits the resource in the external program.
- **Detect missing:** determine availability from the saved path, retain the record, and allow repair. Do not discover files by scanning the computer.

Path-based deduplication is not a claim that every physical file identity can be detected. Hard links, symbolic links, aliases for network locations, and externally changed paths may have different path representations.

## Persistence and language

Default data lives below `%LOCALAPPDATA%\LocalResourceLibrary`. `--data-dir PATH` redirects the application to a separate directory for isolated verification or a different local library. The SQLite file is `library.db`.

The UI language choice is saved locally in `settings.json`. Metadata remains as entered; changing the interface language does not translate project names, descriptions, notes, aliases, or file paths. A mutex prevents two app instances in the same Windows session from opening the same data directory; this is not a multi-user shared-database service.

Database backups should be made after fully closing the app. Copy the whole data directory rather than assuming a live SQLite database can be copied safely as a single file.

## Deliberately outside V0.1

No content indexing, document parsing, previews, AI, embeddings, tags, URL capture, account system, cloud sync, browser extension, global shortcut window, automatic classification, or background network service. Future additions must work from the user's registered library; this version does not establish permission to scan the whole computer.
