namespace LocalResourceLibrary.Core;

public record Project(string Id, string Name, string Description);

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
    IReadOnlyList<Project> Projects)
{
    public string RealName => Path.GetFileName(Path.TrimEndingDirectorySeparator(Target)) is { Length: > 0 } name
        ? name : Target;
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? RealName : Alias;
}

public record LibrarySnapshot(IReadOnlyList<ResourceItem> Items, IReadOnlyList<Project> Projects);
public record AddResourcesResult(int Added, int Existing, IReadOnlyList<string> Errors);
