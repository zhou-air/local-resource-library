# Local Resource Library

[简体中文](README.md)

A lightweight Windows desktop library that gives local files and folders readable names, descriptions, notes, and project membership.

**Resources stay where they are.** Adding a resource stores its absolute path. It does not copy or move the resource or store its contents. One resource can belong to several logical projects, all referencing the same item.

## Two desktop interfaces

The project includes a WPF version and a WinUI 3 trial frontend sharing the same SQLite core. The V0.1 feature list and default run/build commands below describe the WPF version.

The WinUI 3 frontend adds native Windows controls, file association icons, eight view modes, natural sorting, and adjustable panes. Some resource-management operations still require WPF. Both frontends use the same data directory by default and cannot open the same library simultaneously. See the [WinUI 3 trial guide (Chinese)](docs/winui-trial.md) for its feature scope and build commands, and the [WinUI verification record (Chinese)](docs/winui-verification.md) for checked coverage.

## V0.1 features

- Add files and folders with pickers or drag and drop. An already registered path reuses its existing item.
- Keep alias, description, and note separate. The alias is the preferred display name.
- Create logical projects and assign an item to multiple projects.
- Search aliases, real file names, paths, descriptions, notes, and project names using local text search.
- Double-click to open files with their Windows default application, or folders with Explorer. Track open count and last-opened time.
- Open the containing location, copy a path, remove project membership, or remove a library record.
- Rename a physical file or folder through a separate operation while retaining the item ID, metadata, and project relationships.
- Mark missing paths without deleting their records, and repair paths later.
- Store metadata locally in SQLite and switch between Chinese and English UI.

## Run

Use a 64-bit Windows 10 or Windows 11 desktop environment. Published releases include the required .NET runtime.

1. Extract the complete release folder and keep all its files together.
2. Open `LocalResourceLibrary.exe`.
3. Add files or folders, describe them, and assign them to projects.

No account is required. The app provides no cloud sync, telemetry, or background network service. External applications used to open resources may have their own network behavior.

### Common actions

| Goal | Action |
| --- | --- |
| Give an item a readable name | Edit its alias; the real file name stays the same |
| Explain what a resource contains and why it matters | Edit its description |
| Save temporary or personal information | Edit its note |
| Reuse a resource across projects | Select multiple memberships in the details panel |
| Open a resource | Double-click it or choose Open in its context menu |
| Locate it in Explorer | Choose Open location |
| Change its actual name | Use Rename physical file / folder and confirm |
| Restore a moved resource reference | Select the missing item and repair its path |
| Remove one membership | Choose Remove from project |
| Forget a resource in this app | Choose Remove from library; the physical resource remains |
| Switch language | Use the language selector at the top of the window; the choice is saved |

A project is a logical collection, not a disk folder. Deleting a project or a library record never deletes the physical resource. Editing an alias and renaming a physical file are separate operations.

Click Save after editing details. Selecting another item or closing the window prompts you to save, discard, or cancel if changes remain. Search applies within the current sidebar view. Multiple whitespace-separated terms must each match at least one searchable field of the resource; choose All resources to search the whole library.

Use Refresh status after externally moving a resource or disconnecting/reconnecting a location. Adding a folder registers the folder itself; it does not import all its contents. Renaming a folder in the app also updates already registered child paths.

Keyboard shortcuts: `Ctrl+F` focuses search, `Ctrl+S` saves details, and `F5` refreshes status. When focus is in the resource list, `Enter` opens the selected resource and `F2` edits its alias.

## Data and backup

The default database is:

```text
%LOCALAPPDATA%\LocalResourceLibrary\library.db
```

The database contains metadata, projects, and memberships. It does not contain the resources themselves; backing up the library is not a backup of the original files.

The interface language is saved in `settings.json` in the same directory. Only one app instance can open the same data directory at a time.

**Fully exit the app before copying the entire `LocalResourceLibrary` data directory for backup.** Exit the app before restoring it, too. Moving the library to another computer also requires making the original resources available there and repairing changed paths.

Use a separate data directory for a trial run or verification:

```powershell
.\LocalResourceLibrary.exe --data-dir "C:\Temp\LocalResourceLibrary-Test"
```

Removing the program folder does not automatically remove the library stored in the local application data directory.

## Build from source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Initial restore, build, or publish may need network access to download NuGet dependencies and runtime packs.

```powershell
dotnet build LocalResourceLibrary.slnx
dotnet run --project tests/LocalResourceLibrary.Checks
dotnet run --project tests/LocalResourceLibrary.UiChecks
dotnet run --project src/LocalResourceLibrary.App
```

UI integration checks require an available Windows desktop session. `global.json` selects a stable .NET 10 SDK and allows newer feature bands within that major version.

Publish a self-contained Windows x64 release:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Zip
```

The output folder is `dist/LocalResourceLibrary-win-x64`; the optional archive is `dist/LocalResourceLibrary-win-x64.zip`. Distribute the complete folder or archive. The script publishes into a fresh staging directory first. When a successful build replaces an existing output, the previous folder is retained under `dist` as `.previous-…`; remove it manually when no longer needed.

Package the source without requiring Git:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-source.ps1
```

The output is `dist/LocalResourceLibrary-v0.1-source.zip`, with a containing top-level folder. The script selects source code, project configuration, documentation, and scripts from an explicit allowlist, excluding build caches, databases, user settings, and logs. Archive entries have a stable order and timestamps. An existing source archive is retained as `.previous-source-…`.

See [architecture](docs/architecture.md), [UI design](docs/ui-design.md), the [verification record](docs/verification.md), the [release verification checklist](docs/release-checklist.md), and [third-party notices](THIRD-PARTY-NOTICES.md).

## Scope and limitations

V0.1 registers local files and folders using their saved paths. If another application moves or renames a resource, repair its path. The app does not scan the computer to guess its new location.

Search uses only library metadata, with no file-content indexing, AI, or embeddings. URL collection, previews, tags, global hotkey search, cloud sync, accounts, browser extensions, automatic classification, and network services are not implemented. The generic item model and separate search interface leave room for later work.

This version is intended for one local user. It does not provide shared-database or cross-device collaboration. Physical-file rename changes the disk resource; resolve file locks, existing destination names, or permission problems before retrying.

Application instructions and errors are available in Chinese and English. Low-level errors returned by Windows may use the operating system's language.

## License

This project's original source code is licensed under the **GNU General Public License v3.0 only (GPL-3.0-only)**. See [LICENSE](LICENSE) for the full text. Copyright (C) 2026 zhou-air.

Third-party components retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md) and the [WinUI dependency notices (Chinese)](docs/winui-third-party-notices.md).
