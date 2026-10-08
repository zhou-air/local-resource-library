namespace LocalResourceLibrary.Core;

/// <summary>Null leaves a field unchanged; an empty string explicitly clears it.</summary>
public sealed record ResourceMetadataPatch(string Id, string? Alias = null, string? Description = null, string? Note = null);

public sealed record AddResourceResult(ResourceItem Item, bool Added);

public sealed record ResourceMetadataFailure(string ItemId, string Error);

/// <summary>A failed batch writes nothing. Items contains committed results only.</summary>
public sealed record ResourceMetadataBatchResult(
    bool Committed,
    IReadOnlyList<ResourceItem> Items,
    IReadOnlyList<ResourceMetadataFailure> Failures);
