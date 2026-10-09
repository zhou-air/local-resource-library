# Core regression checks

Run on Windows with the .NET 10 SDK from the repository root:

```powershell
dotnet run --project tests/LocalResourceLibrary.Checks/LocalResourceLibrary.Checks.csproj -c Release
```

The console runner returns exit code `0` only when all 58 scenarios pass. Every scenario uses its own temporary SQLite database and temporary files. Cleanup is restricted to that scenario's generated temporary directory.

Project organization checks include real schema 1/3/4 migration to schema 5, migration rollback, stable UUIDs and ID immutability, group lifecycle and deletion without resource loss, theme palette bounds, independent pinned ordering, cross-group moves and atomic failure handling.

Coverage includes reference-only imports; canonical Windows paths; many-to-many projects; separate alias, description and note; all six search fields; database reopening; missing paths; safe rename and repair; descendant references; logical removal; atomic batch record/membership deletion, preservation of original files and unrelated memberships, and rollback after injected deletion failures; shell dispatch history; and rollback after injected SQLite and filesystem failures.

File rename recovery coverage includes a real local Windows file renamed outside the application and recovered after a database restart, with its Item ID, physical identity, content, and project context preserved. This native integration check requires a local Windows volume that supports file-ID lookup. Deterministic provider checks cover existing-path precedence; unresolved or inaccessible identities; mismatched file IDs or volume IDs; missing or folder candidates; path conflicts; explicit repair recapturing identity; and recovery write or shell-launch failures. A hand-created version-one database verifies schema migration, identity backfill only for accessible files, unchanged metadata timestamps, and the Missing behavior of legacy files that have no saved identity. Snapshots never perform rename recovery, and externally renamed folder items remain Missing until explicit repair.

The checks perform real filesystem changes only to their generated test files. Windows shell launch is replaced with a test platform, so the suite does not open default applications or Explorer. It does not verify WinUI rendering, dialogs, drag-and-drop, or the behavior of the user's default application.
