using LocalResourceLibrary.Core;

namespace LocalResourceLibrary.WinUI.Services;

/// <summary>Only fields and memberships explicitly changed by the local editor are written.</summary>
public sealed record ResourceEditDraft(ResourceItem Expected, ResourceMetadataPatch Metadata,
    string[] AddProjectIds, string[] RemoveProjectIds, string? Url)
{
    public static ResourceEditDraft Create(ResourceItem expected, string alias, string description, string note,
        IEnumerable<string> projectIds, string? url = null)
    {
        var previous = expected.Projects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        var selected = projectIds.ToHashSet(StringComparer.Ordinal);
        return new(expected,
            new(expected.Id, alias != expected.Alias ? alias : null,
                description != expected.Description ? description : null,
                note != expected.Note ? note : null),
            selected.Except(previous).ToArray(), previous.Except(selected).ToArray(),
            expected.IsUrl && url != null && url != expected.Target ? url : null);
    }
}
