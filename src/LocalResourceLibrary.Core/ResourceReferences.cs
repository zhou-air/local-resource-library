namespace LocalResourceLibrary.Core;

/// <summary>Counts describe actual relationship changes, including duplicate-safe no-ops.</summary>
public sealed record ResourceReferenceResult(
    IReadOnlyList<string> ItemIds, int AddedMemberships, int RemovedMemberships);

/// <summary>A reference import is committed atomically and never changes the referenced files.</summary>
public sealed record ResourceImportResult(
    IReadOnlyList<ResourceItem> Items, int Added, int Existing, int AddedMemberships);

public sealed record LibraryUndoState(bool CanUndo, bool CanRedo, string? UndoDescription, string? RedoDescription);

public sealed record LibraryHistoryResult(bool Applied, string? Description, bool Conflict = false);
