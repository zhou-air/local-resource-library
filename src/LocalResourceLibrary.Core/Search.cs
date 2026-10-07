namespace LocalResourceLibrary.Core;

/// <summary>Search only the supplied library metadata. A future provider can use a different strategy.</summary>
public interface ISearchProvider
{
    IEnumerable<ResourceItem> Search(IEnumerable<ResourceItem> items, string query);
}

public sealed class LocalTextSearchProvider : ISearchProvider
{
    public IEnumerable<ResourceItem> Search(IEnumerable<ResourceItem> items, string query)
    {
        ArgumentNullException.ThrowIfNull(items);
        var terms = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return terms.Length == 0 ? items : items.Where(item =>
        {
            var fields = new[] { item.Alias, item.RealName, item.Target, item.Description, item.Note }
                .Concat(item.Projects.Select(project => project.Name)).ToArray();
            return terms.All(term => fields.Any(field => field.Contains(term, StringComparison.OrdinalIgnoreCase)));
        });
    }
}
