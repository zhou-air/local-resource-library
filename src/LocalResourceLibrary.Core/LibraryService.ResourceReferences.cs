using Microsoft.Data.Sqlite;
using static LocalResourceLibrary.Core.LibraryRepository;

namespace LocalResourceLibrary.Core;

public sealed partial class LibraryService
{
    /// <summary>Copy or move stable Item IDs. A null source (All Resources) only adds target memberships.</summary>
    public ResourceReferenceResult TransferResources(IEnumerable<string> itemIds, string targetProjectId,
        string? sourceProjectId = null, bool move = false)
    {
        var ids = ReferenceItemIds(itemIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectId);
        var added = 0;
        var removed = 0;
        Write((connection, transaction) =>
        {
            RequireProject(connection, transaction, targetProjectId);
            if (move && sourceProjectId != null) RequireProject(connection, transaction, sourceProjectId);
            foreach (var id in ids) RequireItem(connection, transaction, id);
            if (move && sourceProjectId == targetProjectId) return;
            foreach (var id in ids)
            {
                var changed = Execute(connection, transaction,
                    "INSERT OR IGNORE INTO ProjectItem(project_id,item_id) VALUES($project,$item);",
                    ("$project", targetProjectId), ("$item", id));
                added += changed;
                if (move && sourceProjectId != null)
                {
                    var count = Execute(connection, transaction,
                        "DELETE FROM ProjectItem WHERE project_id=$project AND item_id=$item;",
                        ("$project", sourceProjectId), ("$item", id));
                    removed += count;
                    changed += count;
                }
                if (changed > 0) Touch(connection, transaction, id);
            }
        }, move && sourceProjectId != null ? "MoveResources" : "CopyResources");
        return new ResourceReferenceResult(ids, added, removed);
    }

    /// <summary>Add/remove only requested memberships for all resources in one transaction.</summary>
    public ResourceReferenceResult ChangeResourceProjectsBatch(IEnumerable<string> itemIds,
        IEnumerable<string>? addProjectIds = null, IEnumerable<string>? removeProjectIds = null)
    {
        var ids = ReferenceItemIds(itemIds);
        var (add, remove) = MembershipDelta(addProjectIds, removeProjectIds);
        var added = 0;
        var removed = 0;
        Write((connection, transaction) =>
        {
            foreach (var projectId in add.Concat(remove)) RequireProject(connection, transaction, projectId);
            foreach (var id in ids) RequireItem(connection, transaction, id);
            foreach (var id in ids)
            {
                var changed = 0;
                foreach (var projectId in add)
                {
                    var count = Execute(connection, transaction,
                        "INSERT OR IGNORE INTO ProjectItem(project_id,item_id) VALUES($project,$item);",
                        ("$project", projectId), ("$item", id));
                    added += count;
                    changed += count;
                }
                foreach (var projectId in remove)
                {
                    var count = Execute(connection, transaction,
                        "DELETE FROM ProjectItem WHERE project_id=$project AND item_id=$item;",
                        ("$project", projectId), ("$item", id));
                    removed += count;
                    changed += count;
                }
                if (changed > 0) Touch(connection, transaction, id);
            }
        }, "ChangeResourceProjects");
        return new ResourceReferenceResult(ids, added, removed);
    }

    /// <summary>Atomically import absolute paths and HTTP(S) URLs; reuse existing records and metadata.</summary>
    public ResourceImportResult ImportReferences(IEnumerable<string> targets, string? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var rawTargets = targets.ToArray();
        if (rawTargets.Length == 0) throw new ArgumentException("没有可导入的资源引用。", nameof(targets));
        var items = new List<ResourceItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var existing = 0;
        var memberships = 0;
        Write((connection, transaction) =>
        {
            if (projectId != null) RequireProject(connection, transaction, projectId);
            foreach (var raw in rawTargets)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(raw);
                var value = raw.Trim().Trim('"');
                var isUrl = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (!isUrl && !Path.IsPathFullyQualified(value))
                    throw new ArgumentException($"请输入绝对文件路径或有效网址：{raw}", nameof(targets));
                var target = isUrl ? ResourceUrls.Normalize(value) : ResourcePaths.Normalize(value);
                var key = isUrl ? ResourceUrls.Key(target) : ResourcePaths.Key(target);
                if (!seen.Add(key)) continue;
                var type = isUrl ? ResourceUrls.Type : null;
                var result = RegisterResourceInTransaction(connection, transaction, type, target, key,
                    null, null, null, projectId == null ? [] : [projectId], null, out var changed);
                items.Add(result.Item);
                if (result.Added) added++; else existing++;
                memberships += changed;
            }
        }, "ImportResources");
        return new ResourceImportResult(items, added, existing, memberships);
    }

    private static string[] ReferenceItemIds(IEnumerable<string> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("资源 ID 不能为空。", nameof(itemIds));
        return ids;
    }

    private AddResourceResult RegisterResourceInTransaction(SqliteConnection connection, SqliteTransaction transaction,
        string? requestedType, string target, string key, string? alias, string? description, string? note,
        string[] projectIds, byte[]? icon, out int changedMemberships)
    {
        foreach (var projectId in projectIds) RequireProject(connection, transaction, projectId);
        var itemId = Scalar(connection, transaction, "SELECT id FROM Item WHERE path_key=$key;", ("$key", key)) as string;
        var added = itemId == null;
        if (added)
        {
            var type = requestedType ?? (platform.DirectoryExists(target) ? "folder" : platform.FileExists(target) ? "file" : null);
            if (type == null || !Exists(target, type))
                throw new FileNotFoundException("路径不存在、暂时无法访问，或资源类型与路径不同。", target);
            itemId = Guid.NewGuid().ToString("N");
            var now = Time(DateTimeOffset.UtcNow);
            var identity = type == "file" ? CaptureIdentity(target) : null;
            Execute(connection, transaction, """
                INSERT INTO Item(id,type,target,path_key,alias,description,note,created_at,updated_at,volume_id,file_id,favicon)
                VALUES($id,$type,$target,$key,$alias,$description,$note,$now,$now,$volume,$file,$icon);
                """, ("$id", itemId), ("$type", type), ("$target", target), ("$key", key),
                ("$alias", alias ?? ""), ("$description", description ?? ""), ("$note", note ?? ""),
                ("$now", now), ("$volume", identity?.VolumeId), ("$file", identity?.FileId), ("$icon", icon));
        }
        else
        {
            var current = RequireItem(connection, transaction, itemId!);
            if (requestedType != null && current.Type != requestedType)
                throw new InvalidOperationException("相同路径已收藏为其他资源类型，请使用现有记录。");
            if (Exists(current.Target, current.Type)) BackfillIdentity(connection, transaction, current);
        }
        changedMemberships = 0;
        foreach (var projectId in projectIds)
            changedMemberships += Execute(connection, transaction,
                "INSERT OR IGNORE INTO ProjectItem(project_id,item_id) VALUES($project,$item);",
                ("$project", projectId), ("$item", itemId));
        if (!added && changedMemberships > 0) Touch(connection, transaction, itemId!);
        var item = RequireItem(connection, transaction, itemId!);
        return new AddResourceResult(item with { IsMissing = !Exists(item.Target, item.Type) }, added);
    }
}
