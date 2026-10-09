# Resource interaction functional checks

Run from the repository root:

```powershell
dotnet run --project tests/LocalResourceLibrary.ResourceInteractionChecks -c Release
```

Every check uses a fresh generated temporary directory and SQLite database. The checks cover the shared Core services used by WinUI and MCP: atomic batch copy/move, multi-project preservation, mixed file/folder/URL imports, deduplication, validation and injected SQLite rollback, stable Item/File/Project IDs, session Undo/Redo, batch deletion restoration, project/group lifecycle, property edits, 100-step history, redo clearing, external-write conflicts, and preservation of unrelated external fields. Membership revision tests detect externally removed/re-added associations even when their final state matches the old snapshot, prevent new user actions from reviving stale history, and verify repeated changes by the same UI session remain undoable and redoable.

The actual WinUI clipboard parser is linked into this test project to validate absolute/quoted/multiple paths, URLs, invalid mixed text, stable reference payloads, incompatible payload versions and cross-library rejection. Original file contents and positions remain unchanged throughout the reference operations. Tests do not exercise the Windows clipboard, native mouse gestures, screenshots or visual acceptance.
