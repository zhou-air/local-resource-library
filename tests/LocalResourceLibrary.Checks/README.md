# Core regression checks

Run on Windows with the .NET 10 SDK from the repository root:

```powershell
dotnet run --project tests/LocalResourceLibrary.Checks/LocalResourceLibrary.Checks.csproj -c Release
```

The console runner returns exit code `0` only when all 27 scenarios pass. Every scenario uses its own temporary SQLite database and temporary files. Cleanup is restricted to that scenario's generated temporary directory.

Coverage includes reference-only imports; canonical Windows paths; many-to-many projects; separate alias, description and note; all six search fields; database reopening; missing paths; safe rename and repair; descendant references; logical removal; shell dispatch history; and rollback after injected SQLite and filesystem failures.

The checks perform real filesystem changes only to their generated test files. Windows shell launch is replaced with a test platform, so the suite does not open default applications or Explorer. It does not verify WPF rendering, dialogs, drag-and-drop, or the behavior of the user's default application.
