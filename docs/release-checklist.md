# Release verification checklist

This document describes what to verify. It is not a claim that every item has been checked. Record the actual result and environment before a public release.

## Build and local checks

- [ ] On Windows with .NET 10 SDK, `dotnet build LocalResourceLibrary.slnx` succeeds.
- [ ] `dotnet run --project tests/LocalResourceLibrary.Checks` succeeds. Preserve its reported check coverage; do not call these checks a full UI test.
- [ ] `dotnet run --project tests/LocalResourceLibrary.UiChecks` succeeds in a Windows desktop session. Record which interactions it actually covers.
- [ ] `scripts/publish.ps1 -Zip` produces a complete self-contained Windows x64 folder and ZIP.
- [ ] `scripts/package-source.ps1` produces a source ZIP with a top-level folder and excludes build caches, local databases, settings, and logs.
- [ ] Launch the published executable with an isolated `--data-dir`, then close and reopen it to check persistence.
- [ ] Launch the release on a Windows x64 machine without a separately installed .NET runtime before claiming clean-machine portability.

## Main resource workflow

- [ ] Add a test file through the picker and a folder through its picker. Their original locations and contents remain unchanged.
- [ ] Drag and drop files and a folder into the app.
- [ ] Add the same path again; one item remains.
- [ ] Edit alias, description, and note independently. Editing alias leaves the real file name unchanged.
- [ ] Create two projects and assign one item to both. Changes to that item appear consistently from each project.
- [ ] Search independently for alias, real name, target path, description, note, and a project name.
- [ ] Open a file with its default application and a folder with Explorer. Check the successful-handoff open count and last-opened value.
- [ ] Use Open location and Copy path.
- [ ] Check Recent after opening resources.

## File-system and membership boundaries

- [ ] Rename a disposable physical test file in the app. Confirm the disk name and stored target change while its ID, metadata, and memberships stay intact.
- [ ] Try an existing destination name or a locked test resource; a useful error appears and the library remains usable.
- [ ] Move a disposable test resource outside the app, refresh availability, and check that its record is marked missing without losing metadata.
- [ ] Repair the missing path and confirm the same item's context remains available.
- [ ] Remove an item from one project; its other membership remains.
- [ ] Remove a library record and delete a test project; the physical resources remain on disk.
- [ ] Check unsaved-detail edits when changing selection or performing an action so accidental data loss is visible and avoidable.

## Language and presentation

- [ ] Switch between Chinese and English and restart the app. The choice persists and metadata is unchanged.
- [ ] Inspect the main window, detail editing, context menu, confirmations, and error states in both languages.
- [ ] Inspect at a normal desktop scale and a larger Windows display scale; check clipping, scrolling, keyboard focus, and readable button labels.
- [ ] Check `Ctrl+F`, `Ctrl+S`, `F5`, and resource-list `F2` / `Enter` shortcuts.
- [ ] Do not describe automated UI interaction or screenshots as exhaustive visual testing.

## Before public distribution

- [x] Original source license: GPL-3.0-only; full text included in the root LICENSE file.
- [ ] Review third-party notices against the actual packaged dependency versions and preserve bundled license files.
- [ ] Check that the source/release archive contains no local databases, private paths, credentials, editor state, or unrelated artifacts.
- [ ] Document any failed or unverified check with the release.
