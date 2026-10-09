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
    [Description("List logical projects with permanent IDs, names, descriptions, colors, group IDs, pin state and ordering, plus group metadata. Optional name lookup returns every case-insensitive substring candidate and ambiguous=true for multiple matches. Resolve candidates before using an ID; names never identify mutation targets.")]
    public CallToolResult ListProjects(
        [Description("Optional project-name substring; every matching candidate is returned.")] string? name = null,
        [Description("Optional exact permanent project ID; unknown IDs return not_found.")] string? project_id = null,
        [Description("Optional exact permanent group ID; unknown IDs return not_found.")] string? group_id = null,
        [Description("Return only projects without a group. Cannot be combined with group_id.")] bool ungrouped = false) => Run(() =>
    {
        var snapshot = library.GetSnapshot(checkPaths: false);
        if (project_id != null) RequireProject(snapshot, project_id);
        if (group_id != null) RequireGroup(snapshot, group_id);
        if (ungrouped && group_id != null) throw new ArgumentException("Use group_id or ungrouped, not both.");
        var projects = snapshot.Projects.Where(project =>
            (project_id == null || project.Id == project_id) &&
            (group_id == null || project.GroupId == group_id) &&
            (!ungrouped || project.GroupId == null) && NameMatches(project.Name, name)).ToArray();
        return new { projects, project_groups = Groups(snapshot), total = projects.Length, ambiguous = name != null && projects.Length > 1 };
    });

    [McpServerTool(Name = "list_project_groups", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List project groups with permanent IDs, names, order and creation time. Optional name lookup returns every case-insensitive substring candidate. Groups are one level; projects can be ungrouped.")]
    public CallToolResult ListProjectGroups(string? name = null, string? group_id = null) => Run(() =>
    {
        var snapshot = library.GetSnapshot(checkPaths: false);
        if (group_id != null) RequireGroup(snapshot, group_id);
        var groups = Groups(snapshot).Where(group => (group_id == null || group.Id == group_id) && NameMatches(group.Name, name)).ToArray();
        return new { groups, total = groups.Length, ambiguous = name != null && groups.Length > 1 };
    });

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
    [Description("Create a logical project with an automatically generated permanent UUID. Names are unique using Core's case-insensitive rule. Optionally select a preset color and an existing group ID. Does not create a physical folder; IDs cannot be supplied or changed.")]
    public CallToolResult CreateProject(string name, string description = "",
        [Description("Preset key: default, blue, teal, purple, amber, cyan, rose or slate.")] string color = "default",
        [Description("Existing permanent group ID; omitted creates an ungrouped project.")] string? group_id = null) =>
        Run(() => new { project = library.CreateProject(name, description, color, group_id) });

    [McpServerTool(Name = "update_project", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Patch supplied project fields using only its permanent ID. Name, color and group changes preserve that ID and resource memberships. Omit fields to preserve; an empty description clears it. Set clear_group=true to move to ungrouped. Unknown/stale IDs fail without name fallback; IDs are read-only.")]
    public CallToolResult UpdateProject(string project_id, string? name = null, string? description = null,
        [Description("Preset key: default, blue, teal, purple, amber, cyan, rose or slate.")] string? color = null,
        [Description("Existing permanent group ID; null preserves the current group.")] string? group_id = null,
        [Description("Move to ungrouped; cannot be combined with group_id.")] bool clear_group = false) => Run(() =>
    {
        RequireId(project_id);
        if (name == null && description == null && color == null && group_id == null && !clear_group)
            throw new ArgumentException("Specify at least one project field to update.");
        return new { project = library.PatchProject(project_id, name, description, color: color, groupId: group_id, clearGroup: clear_group) };
    });

    [McpServerTool(Name = "delete_project", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Delete a logical project by permanent ID and remove only its resource memberships. Resources, original files and other project memberships remain. Deleted IDs are never reused. Unknown/stale IDs fail without name fallback.")]
    public CallToolResult DeleteProject(string project_id) => Run(() =>
    {
        RequireProject(library.GetSnapshot(checkPaths: false), project_id);
        library.DeleteProject(project_id);
        return new { deleted = true, project_id };
    });

    [McpServerTool(Name = "move_project", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Order a project before/after another project using permanent IDs. Unpinned projects move into the target's group; pinned projects sort in the independent pinned area. Pin state never changes; source and target must have the same pin state. Use update_project to move group without changing pin state.")]
    public CallToolResult MoveProject(string project_id, string target_project_id, bool after = false) => Run(() =>
    {
        RequireId(project_id);
        RequireId(target_project_id);
        library.MoveProject(project_id, target_project_id, after);
        return new { project = library.GetProject(project_id) };
    });

    [McpServerTool(Name = "create_project_group", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Create a single-level project group with an automatically generated permanent UUID. Does not create a physical folder; IDs cannot be supplied or changed.")]
    public CallToolResult CreateProjectGroup(string name) => Run(() => new { group = library.CreateProjectGroup(name) });

    [McpServerTool(Name = "update_project_group", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Rename a group by permanent ID, preserving its ID, projects and order. Unknown/stale IDs fail without name fallback; Group IDs are read-only.")]
    public CallToolResult UpdateProjectGroup(string group_id, string name) => Run(() =>
    {
        RequireId(group_id);
        return new { group = library.PatchProjectGroup(group_id, name) };
    });

    [McpServerTool(Name = "delete_project_group", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Delete a group by permanent ID. Its projects move to ungrouped, preserving their IDs, colors, pin state and all resource memberships. Resources and original files remain; unknown/stale IDs fail without name fallback.")]
    public CallToolResult DeleteProjectGroup(string group_id) => Run(() =>
    {
        RequireGroup(library.GetSnapshot(checkPaths: false), group_id);
        library.DeleteProjectGroup(group_id);
        return new { deleted = true, group_id };
    });

    [McpServerTool(Name = "move_project_group", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Order a group before/after another group using permanent IDs. Does not change group IDs, project groups, pin state or resource memberships.")]
    public CallToolResult MoveProjectGroup(string group_id, string target_group_id, bool after = false) => Run(() =>
    {
        RequireId(group_id);
        RequireId(target_group_id);
        library.MoveProjectGroup(group_id, target_group_id, after);
        return new { groups = Groups(library.GetSnapshot(checkPaths: false)) };
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

    [McpServerTool(Name = "set_resources_projects", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Atomically add/remove the named project memberships for 1 to 1000 permanent Item IDs. Preserves every other membership, resource record and physical target. Unknown IDs or failed writes roll back the entire batch; duplicates are ignored.")]
    public CallToolResult SetResourcesProjects(string[] item_ids, string[]? add_project_ids = null, string[]? remove_project_ids = null) => Run(() =>
    {
        ValidateResourceBatch(item_ids);
        if ((add_project_ids?.Length ?? 0) + (remove_project_ids?.Length ?? 0) == 0)
            throw new ArgumentException("Specify at least one project ID to add or remove.");
        return library.ChangeResourceProjectsBatch(item_ids, add_project_ids, remove_project_ids);
    });

    [McpServerTool(Name = "copy_resources_to_project", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Copy 1 to 1000 resource references into a project by permanent Item IDs and Project ID in one transaction. Existing target memberships are skipped; all original memberships and Item/File IDs remain. Never copies or modifies real files or folders.")]
    public CallToolResult CopyResourcesToProject(string[] item_ids, string target_project_id) => Run(() =>
    {
        ValidateResourceBatch(item_ids);
        RequireId(target_project_id);
        return library.TransferResources(item_ids, target_project_id);
    });

    [McpServerTool(Name = "move_resources_to_project", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Atomically move 1 to 1000 resource references from source_project_id to target_project_id using permanent IDs. Only the named source membership is removed; other projects and original files remain. Omit source_project_id for All Resources: adds the target and preserves every existing membership. Moving to the same project changes nothing.")]
    public CallToolResult MoveResourcesToProject(string[] item_ids, string target_project_id, string? source_project_id = null) => Run(() =>
    {
        ValidateResourceBatch(item_ids);
        RequireId(target_project_id);
        if (source_project_id != null) RequireId(source_project_id);
        return library.TransferResources(item_ids, target_project_id, source_project_id, move: true);
    });

    [McpServerTool(Name = "import_resources", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Import 1 to 1000 existing absolute file/folder paths and HTTP/HTTPS URLs as references in one Core transaction, optionally into a project identified by project_id. Reuses existing Items without replacing metadata, identity or memberships. No folder scan, network fetch or filesystem copy/move/delete, even for Explorer cut paths. Invalid targets roll back the whole batch.")]
    public CallToolResult ImportResources(string[] targets, string? project_id = null) => Run(() =>
    {
        ValidateResourceBatch(targets);
        if (project_id != null) RequireId(project_id);
        var result = library.ImportReferences(targets, project_id);
        return new { items = result.Items.Select(ResourceDto.From).ToArray(), result.Added, result.Existing, result.AddedMemberships };
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

    private static void ValidateResourceBatch(string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length is < 1 or > 1000) throw new ArgumentException("A resource batch must contain 1 to 1000 entries.");
        if (values.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Resource batch entries cannot be blank.");
    }

    private static void RequireProject(LibrarySnapshot snapshot, string id)
    {
        RequireId(id);
        if (!snapshot.Projects.Any(project => project.Id == id)) throw new KeyNotFoundException("Project does not exist.");
    }

    private static IReadOnlyList<ProjectGroup> Groups(LibrarySnapshot snapshot) => snapshot.ProjectGroups ?? [];

    private static void RequireGroup(LibrarySnapshot snapshot, string id)
    {
        RequireId(id);
        if (!Groups(snapshot).Any(group => group.Id == id)) throw new KeyNotFoundException("Project group does not exist.");
    }

    private static bool NameMatches(string name, string? lookup) => lookup == null ||
        name.Contains(lookup.Trim(), StringComparison.OrdinalIgnoreCase);

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
