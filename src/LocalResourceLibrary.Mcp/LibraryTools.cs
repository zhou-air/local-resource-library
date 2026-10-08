using System.ComponentModel;
using System.Text.Json;
using LocalResourceLibrary.Core;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LocalResourceLibrary.Mcp;

[McpServerToolType]
public sealed class LibraryTools(LibraryService library, ServerOptions options, BatchPreviewStore previews)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly ISearchProvider search = new LocalTextSearchProvider();

    [McpServerTool(Name = "search_resources", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Search saved file, folder and URL metadata: names, aliases, descriptions, notes, targets and project names. Multiple words all must match. Paginated; never scan directories or read content.")]
    public CallToolResult SearchResources(
        [Description("Keywords, or empty for all resources.")] string query = "",
        [Description("Optional type: file, folder or url.")] string? type = null,
        [Description("Optional project ID; get IDs from list_projects.")] string? project_id = null,
        [Description("Page size, 1 to 1000.")] int limit = 100,
        [Description("Zero-based offset; continue until has_more is false.")] int offset = 0) => Run(() =>
    {
        ValidatePage(limit, offset);
        ValidateType(type, optional: true);
        var snapshot = library.GetSnapshot(checkPaths: false);
        if (project_id != null) RequireProject(snapshot, project_id);
        var items = snapshot.Items.Where(item => (type == null || item.Type == type) &&
            (project_id == null || item.Projects.Any(project => project.Id == project_id)));
        return Page(search.Search(items, query).ToArray(), limit, offset);
    });

    [McpServerTool(Name = "get_resource", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Get a saved resource by permanent Item ID, its real path/URL, metadata, projects and availability. By default a missing file is resolved through Core's saved Volume ID/File ID; may update index path but never opens or changes the real file. URL reachability is not checked.")]
    public CallToolResult GetResource(string item_id,
        [Description("Recover a moved file through its saved identity; false leaves the stored target unchanged.")] bool resolve_path = true) => Run(() =>
    {
        RequireId(item_id);
        return new { item = ResourceDto.From(library.GetResource(item_id, resolve_path)) };
    });

    [McpServerTool(Name = "list_projects", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List all logical projects with IDs, names, descriptions, pin state and ordering.")]
    public CallToolResult ListProjects() => Run(() => new { projects = library.GetSnapshot(checkPaths: false).Projects });

    [McpServerTool(Name = "list_project_resources", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List every saved resource in a logical project using pages. Continue with offset until has_more is false.")]
    public CallToolResult ListProjectResources(string project_id, int limit = 100, int offset = 0) => Run(() =>
    {
        ValidatePage(limit, offset);
        var snapshot = library.GetSnapshot(checkPaths: false);
        RequireProject(snapshot, project_id);
        return Page(snapshot.Items.Where(item => item.Projects.Any(project => project.Id == project_id)).ToArray(), limit, offset);
    });

    [McpServerTool(Name = "add_resource", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Bookmark a file, folder or HTTP/HTTPS URL. Local target must be an existing absolute path. Core deduplication reuses existing records; only adds requested memberships and preserves existing metadata. Does not copy files, scan folders, fetch websites or parse content.")]
    public CallToolResult AddResource(
        [Description("file, folder or url.")] string type,
        [Description("Absolute local path or complete HTTP/HTTPS URL.")] string target,
        string? alias = null, string? description = null, string? note = null,
        [Description("Project IDs to add; omitted means no initial projects.")] string[]? project_ids = null) => Run(() =>
    {
        ValidateType(type);
        var result = library.AddResource(type, target, alias, description, note, project_ids);
        return new { item = ResourceDto.From(result.Item), added = result.Added };
    });

    [McpServerTool(Name = "update_resource", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Patch only the supplied alias, description and note of one resource. Omitted or null fields are preserved; empty strings explicitly clear fields. Preserves target, file identity and project memberships. Use batch preview/commit for bulk changes.")]
    public CallToolResult UpdateResource(string item_id, string? alias = null, string? description = null, string? note = null) => Run(() =>
    {
        ValidatePatch(new ResourceMetadataPatch(item_id, alias, description, note));
        return new { item = ResourceDto.From(library.PatchResourceMetadata(new(item_id, alias, description, note))) };
    });

    [McpServerTool(Name = "create_project", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Create a logical project. Project names are unique using Core's case-insensitive rule; this does not create a physical folder.")]
    public CallToolResult CreateProject(string name, string description = "") => Run(() => new { project = library.CreateProject(name, description) });

    [McpServerTool(Name = "update_project", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Patch supplied project name and/or description, preserving resources, associations, pin state and order. Omit fields to preserve; an empty description clears it.")]
    public CallToolResult UpdateProject(string project_id, string? name = null, string? description = null) => Run(() =>
    {
        RequireId(project_id);
        if (name == null && description == null) throw new ArgumentException("Specify name or description to update.");
        return new { project = library.PatchProject(project_id, name, description) };
    });

    [McpServerTool(Name = "set_resource_projects", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Atomically add and/or remove only the named project memberships for a resource. Every other membership is preserved. A resource can belong to multiple projects without duplication. IDs cannot appear in both lists.")]
    public CallToolResult SetResourceProjects(string item_id, string[]? add_project_ids = null, string[]? remove_project_ids = null) => Run(() =>
    {
        RequireId(item_id);
        if ((add_project_ids?.Length ?? 0) + (remove_project_ids?.Length ?? 0) == 0)
            throw new ArgumentException("Specify at least one project ID to add or remove.");
        return new { item = ResourceDto.From(library.ChangeResourceProjects(item_id, add_project_ids, remove_project_ids)) };
    });

    [McpServerTool(Name = "preview_resource_updates", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Preview 1 to 100 metadata patches without writing the database. Returns exact before/after fields and a process-local token valid for ten minutes and one commit. Show this preview to the user and obtain approval before commit. Invalid items are reported together and no token is issued.")]
    public CallToolResult PreviewResourceUpdates(ResourceUpdate[] updates)
    {
        return RunResult(() =>
        {
            ArgumentNullException.ThrowIfNull(updates);
            if (updates.Length is < 1 or > 100) throw new ArgumentException("A batch must contain 1 to 100 updates.");
            var snapshot = library.GetSnapshot(checkPaths: false);
            var byId = snapshot.Items.ToDictionary(item => item.Id);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var failures = new List<ResourceMetadataFailure>();
            var patches = new List<ResourceMetadataPatch>();
            var expected = new List<ResourceItem>();
            foreach (var update in updates)
            {
                try
                {
                    if (update == null) throw new ArgumentException("An update cannot be null.");
                    var patch = update.ToPatch();
                    ValidatePatch(patch);
                    if (!seen.Add(patch.Id)) throw new ArgumentException("Duplicate Item ID in batch.");
                    if (!byId.TryGetValue(patch.Id, out var item)) throw new KeyNotFoundException("Resource does not exist.");
                    patches.Add(patch);
                    expected.Add(item);
                }
                catch (Exception exception) when (IsExpected(exception))
                {
                    failures.Add(new ResourceMetadataFailure(update?.ItemId ?? "", exception.Message));
                }
            }
            if (failures.Count > 0) return Result(new { committed = false, failures }, isError: true);
            var batch = previews.Create(patches, expected);
            var changes = patches.Zip(expected, (patch, item) => new
            {
                item_id = item.Id,
                before = new { alias = item.Alias, description = item.Description, note = item.Note },
                after = new { alias = patch.Alias ?? item.Alias, description = patch.Description ?? item.Description, note = patch.Note ?? item.Note }
            }).ToArray();
            return Result(new { confirmation_token = batch.Token, expires_at = batch.ExpiresAt, commit_enabled = options.AllowBatchCommit, changes });
        });
    }

    [McpServerTool(Name = "commit_resource_updates", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Commit an approved metadata preview in one Core transaction. Requires --allow-batch-commit at startup and user approval through the MCP client's tool permissions. Pass only the preview token; any intervening resource change rejects and rolls back the entire batch with item-specific failures. Tokens are consumed even on conflict.")]
    public CallToolResult CommitResourceUpdates(string confirmation_token) => RunResult(() =>
    {
        if (!options.AllowBatchCommit)
            return Result(new { error = new { code = "permission_denied", message = "Batch commit is disabled. Start with --allow-batch-commit and configure client confirmation before submitting an approved preview." } }, isError: true);
        var batch = previews.Take(confirmation_token);
        var result = library.ApplyResourceMetadataBatch(batch.Patches, batch.ExpectedItems);
        return Result(new { committed = result.Committed, items = result.Items.Select(ResourceDto.From).ToArray(), failures = result.Failures }, isError: !result.Committed);
    });

    private static object Page(IReadOnlyList<ResourceItem> items, int limit, int offset) => new
    {
        items = items.Skip(offset).Take(limit).Select(ResourceDto.From).ToArray(),
        total = items.Count, limit, offset, has_more = (long)offset + limit < items.Count
    };

    private static void ValidatePage(int limit, int offset)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "limit must be 1 to 1000.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "offset must not be negative.");
    }

    private static void ValidateType(string? type, bool optional = false)
    {
        if (optional && type == null) return;
        if (type is not ("file" or "folder" or "url")) throw new ArgumentException("type must be file, folder or url.");
    }

    private static void RequireId(string id) => ArgumentException.ThrowIfNullOrWhiteSpace(id);

    private static void RequireProject(LibrarySnapshot snapshot, string id)
    {
        RequireId(id);
        if (!snapshot.Projects.Any(project => project.Id == id)) throw new KeyNotFoundException("Project does not exist.");
    }

    private static void ValidatePatch(ResourceMetadataPatch patch)
    {
        RequireId(patch.Id);
        if (patch.Alias == null && patch.Description == null && patch.Note == null)
            throw new ArgumentException("Specify at least one metadata field; use an empty string to clear it.");
    }

    private static bool IsExpected(Exception exception) => exception is ArgumentException or KeyNotFoundException or
        InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or SqliteException;

    private static CallToolResult Run(Func<object> action) => RunResult(() => Result(action()));

    private static CallToolResult RunResult(Func<CallToolResult> action)
    {
        try { return action(); }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Result(new { error = new
            {
                code = exception switch
                {
                    ArgumentException => "invalid_argument",
                    KeyNotFoundException => "not_found",
                    SqliteException sqlite when sqlite.SqliteErrorCode is 5 or 6 => "database_busy",
                    InvalidOperationException => "conflict",
                    UnauthorizedAccessException => "access_denied",
                    _ => "operation_failed"
                },
                message = exception.Message
            } }, isError: true);
        }
    }

    private static CallToolResult Result(object value, bool isError = false)
    {
        var json = JsonSerializer.SerializeToElement(value, JsonOptions);
        return new CallToolResult
        {
            IsError = isError,
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }]
        };
    }
}
