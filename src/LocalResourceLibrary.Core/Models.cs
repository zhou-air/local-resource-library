namespace LocalResourceLibrary.Core;

public record Project(string Id, string Name, string Description, bool IsPinned = false, long SortOrder = 0);

public record ResourceItem(
    string Id,
    string Type,
    string Target,
    string Alias,
    string Description,
    string Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastOpenedAt,
    long OpenCount,
    bool IsMissing,
    IReadOnlyList<Project> Projects,
    FileIdentity? FileIdentity = null,
    byte[]? Favicon = null)
{
    public bool IsUrl => Type == ResourceUrls.Type;
    public string RealName => IsUrl ? ResourceUrls.DisplayName(Target) : Path.GetFileName(Path.TrimEndingDirectorySeparator(Target)) is { Length: > 0 } name
        ? name : Target;
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? RealName : Alias;
}

public record LibrarySnapshot(IReadOnlyList<ResourceItem> Items, IReadOnlyList<Project> Projects);
public record AddResourcesResult(int Added, int Existing, IReadOnlyList<string> Errors);
public record AddUrlResult(ResourceItem Item, bool Added);
