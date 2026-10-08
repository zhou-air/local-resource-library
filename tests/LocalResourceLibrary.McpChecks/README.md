# MCP functional checks

Run from the repository root:

```powershell
dotnet run --project tests/LocalResourceLibrary.McpChecks -c Release
```

The project reference builds the MCP server. The checks use the official MCP C# SDK client to start real `dotnet LocalResourceLibrary.Mcp.dll` child processes over stdio. Every database and file fixture lives in a generated temporary directory; no personal library is opened and WinUI is never launched.

Coverage includes protocol-only stdout and clean shutdown, tool discovery and annotations, structured/text result agreement, file/folder/URL collection and exact deduplication, all searchable metadata fields, filters and pagination, permanent Item IDs, project patching and additive/removal memberships, invalid-input rollback, partial metadata updates, and persistence after a new server starts.

Batch checks cover read-only preview, disabled-by-default commit, explicit server opt-in, cross-process token rejection, single-use tokens, atomic submission, per-item conflict failures and complete rollback. A controlled clock tests token expiry and bounded preview storage without waiting ten minutes. Multiple server processes and a separate Core instance write to the same isolated library concurrently. Native Windows file identity recovery is exercised after an external rename; folder paths are not scanned or recovered automatically.

These checks validate MCP startup by a protocol client and the shared Core/database behavior. They do not validate a particular installed Codex configuration, client approval dialogs, live WinUI interaction, or visual appearance.

To test an already built or separately published framework-dependent server:

```powershell
dotnet run --project tests/LocalResourceLibrary.McpChecks -c Release -- --server "C:\path\LocalResourceLibrary.Mcp.dll"
```
