# Release verification checklist

This is a checklist, not a claim that every item has passed. Record actual results and the environment for each release. Historical results are in [WinUI verification](winui-verification.md).

This is a personal-use project. Agents do not perform visual review or screenshot acceptance after completing tasks unless the user explicitly requests it. Relevant build and functional checks remain required; see [AGENTS.md](../AGENTS.md).

## Build and local checks

- [ ] On Windows with .NET 10 SDK, `dotnet build LocalResourceLibrary.WinUI.slnx -c Release -p:Platform=x64` succeeds.
- [ ] `dotnet run --project tests/LocalResourceLibrary.Checks` succeeds; record its actual coverage.
- [ ] `dotnet run --project tests/LocalResourceLibrary.ExplorerChecks` succeeds; model checks do not establish full UI coverage.
- [ ] `scripts/publish-winui.ps1 -Zip` produces a complete self-contained Windows x64 folder and ZIP.
- [ ] `scripts/package-winui-source.ps1` produces a WinUI-only source ZIP with a top-level folder and no generated caches, databases, local settings, or logs.
- [ ] Build the extracted source ZIP from a short independent path.
- [ ] Launch `LocalResourceLibrary.WinUI.exe` with an isolated `--data-dir`, then exit through its tray menu and reopen it to check persistence.
- [ ] Check a Windows x64 machine without separately installed .NET or Windows App SDK runtimes before claiming clean-machine portability.

## Resource workflow

- [ ] Add files and folders with pickers and drag and drop; original resources remain unchanged.
- [ ] Add the same path again; one item remains.
- [ ] Edit alias, description, and note independently; alias does not change the actual file name.
- [ ] Create two projects, assign one item to both, then clear one membership and save. Its other membership remains.
- [ ] Search alias, actual name, path, description, note, and project name; check category-limited multi-term search.
- [ ] Open a file with its default app and a folder in Explorer; check successful-handoff open count and last-opened value.
- [ ] Use Open location and Copy path; check Recently opened.
- [ ] Move a disposable resource outside the app and refresh status. Its missing record retains metadata.
- [ ] Check Save, Discard, and Cancel when changing selection/category or exiting from the tray with unsaved edits; window × only hides and keeps drafts.
- [ ] Delete a project; resources and other memberships remain. Delete single and multiple resource records; originals remain.
- [ ] Remove selected memberships from the current project; records and other memberships remain.
- [ ] Check deletion cancellation and unsaved-edit Save/Discard/Cancel; failed batches leave no partial deletions.
- [ ] Check project context renaming, description/membership preservation, blank and duplicate-name rejection, and restart persistence.
- [ ] Check default tray icon, window hiding and restoring, second-launch activation, tray exit and icon removal.
- [ ] Verify that the documented limitations match the UI: physical rename, path repair, and project description editing remain unavailable.

## Browsing, language, and presentation

- [ ] Check all eight view modes and five sorting fields, both directions, folders first, and natural name sorting.
- [ ] Check details-header sorting; changing view or sorting retains selection and unsaved drafts.
- [ ] Check selection/details behavior, blank-space clicks, `Esc`, category changes, and the Details toolbar button.
- [ ] Resize both pane dividers with mouse and arrow keys; restart to confirm view, sorting, and width settings.
- [ ] Start with Chinese and English Windows display languages and verify automatic selection; legacy settings.json must not override it. Metadata remains as entered.
- [ ] Check `Ctrl+F`, `Ctrl+S`, `F5`, and resource-area `F2` / `Enter` / `Delete` / `Ctrl+A`.
- [ ] Check Ctrl/Shift selection in both list and grid views, selected counts, selection across sorting/view changes, and right-click targets.
- [ ] If a visual review is required for the release, record the Windows theme, scale, window sizes, and states actually inspected.
- [ ] Do not describe model checks or automated interaction as exhaustive visual testing.

## Before public distribution

- [x] Original source license: GPL-3.0-only, with the complete root [LICENSE](../LICENSE).
- [ ] Compare notices with the actual packaged dependencies and preserve included license files.
- [ ] Confirm source/release archives contain no local databases, private paths, credentials, editor state, or unrelated artifacts.
- [ ] Document failed or unverified checks alongside the release.
