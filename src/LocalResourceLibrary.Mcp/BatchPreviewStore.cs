using System.Security.Cryptography;
using System.Text.Json.Serialization;
using LocalResourceLibrary.Core;

namespace LocalResourceLibrary.Mcp;

public sealed record ResourceUpdate(
    [property: JsonPropertyName("item_id")] string ItemId,
    string? Alias = null, string? Description = null, string? Note = null)
{
    public ResourceMetadataPatch ToPatch() => new(ItemId, Alias, Description, Note);
}

public sealed record PendingBatch(string Token, DateTimeOffset ExpiresAt,
    IReadOnlyList<ResourceMetadataPatch> Patches, IReadOnlyList<ResourceItem> ExpectedItems);

/// <summary>Process-local, single-use previews. Tokens authorize only the exact, unchanged preview.</summary>
public sealed class BatchPreviewStore(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, PendingBatch> pending = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public PendingBatch Create(IReadOnlyList<ResourceMetadataPatch> patches, IReadOnlyList<ResourceItem> expectedItems)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            foreach (var token in pending.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                pending.Remove(token);
            if (pending.Count >= 32) throw new InvalidOperationException("Too many pending previews. Commit a preview or wait for expiry.");
            var tokenValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var preview = new PendingBatch(tokenValue, now.AddMinutes(10), patches.ToArray(), expectedItems.ToArray());
            pending.Add(tokenValue, preview);
            return preview;
        }
    }

    public PendingBatch Take(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (gate)
        {
            if (!pending.Remove(token, out var batch) || batch.ExpiresAt <= time.GetUtcNow())
                throw new InvalidOperationException("Preview token is invalid, expired, already used, or belongs to another server process. Preview again.");
            return batch;
        }
    }
}
