using LocalResourceLibrary.Core;

namespace LocalResourceLibrary.Mcp;

// Keep native content and favicon bytes out of protocol responses.
public sealed record ResourceDto(
    string Id, string Type, string Target, string? Path, string? Url,
    string RealName, string DisplayName, string Alias, string Description, string Note,
    IReadOnlyList<Project> Projects, string Availability, bool IsMissing, bool? Available,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastOpenedAt,
    long OpenCount, FileIdentity? FileIdentity)
{
    public static ResourceDto From(ResourceItem item)
    {
        bool? available = item.IsUrl ? null : item.Type == "folder"
            ? Directory.Exists(item.Target) : File.Exists(item.Target);
        return new ResourceDto(item.Id, item.Type, item.Target, item.IsUrl ? null : item.Target,
            item.IsUrl ? item.Target : null, item.RealName, item.DisplayName, item.Alias, item.Description,
            item.Note, item.Projects, available is null ? "not_checked" : available.Value ? "available" : "missing_or_inaccessible",
            available == false, available, item.CreatedAt, item.UpdatedAt, item.LastOpenedAt,
            item.OpenCount, item.FileIdentity);
    }
}
