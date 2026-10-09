using Microsoft.Data.Sqlite;
using static LocalResourceLibrary.Core.LibraryRepository;

namespace LocalResourceLibrary.Core;

public sealed partial class LibraryService
{
    public ProjectGroup GetProjectGroup(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (gate)
        {
            using var connection = repository.Connect();
            return RequireProjectGroup(connection, null, id);
        }
    }

    public ProjectGroup CreateProjectGroup(string name)
    {
        name = ValidateProjectGroupName(name);
        var group = new ProjectGroup(Guid.NewGuid().ToString("N"), name, 0, DateTimeOffset.UtcNow);
        Write((connection, transaction) =>
        {
            EnsureProjectGroupNameAvailable(connection, transaction, name);
            group = group with { SortOrder = Convert.ToInt64(Scalar(connection, transaction,
                "SELECT COALESCE(MAX(sort_order),-1)+1 FROM ProjectGroup;")) };
            Execute(connection, transaction, """
                INSERT INTO ProjectGroup(id,name,name_key,sort_order,created_at) VALUES($id,$name,$key,$order,$created);
                """, ("$id", group.Id), ("$name", name), ("$key", name.ToUpperInvariant()),
                ("$order", group.SortOrder), ("$created", Time(group.CreatedAt)));
        });
        return group;
    }

    public ProjectGroup PatchProjectGroup(string id, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (name != null) name = ValidateProjectGroupName(name);
        ProjectGroup result = null!;
        Write((connection, transaction) =>
        {
            var current = RequireProjectGroup(connection, transaction, id);
            if (name != null)
            {
                EnsureProjectGroupNameAvailable(connection, transaction, name, id);
                Execute(connection, transaction, "UPDATE ProjectGroup SET name=$name,name_key=$key WHERE id=$id;",
                    ("$id", id), ("$name", name), ("$key", name.ToUpperInvariant()));
            }
            result = current with { Name = name ?? current.Name };
        });
        return result;
    }

    /// <summary>Keep every project and membership; append ordinary projects to the ungrouped area.</summary>
    public void DeleteProjectGroup(string id) => Write((connection, transaction) =>
    {
        RequireProjectGroup(connection, transaction, id);
        var projects = ReadSnapshot(connection, transaction).Projects.Where(project => project.GroupId == id).ToArray();
        var nextOrder = NextProjectOrder(connection, transaction, false, null);
        foreach (var project in projects)
            Execute(connection, transaction, "UPDATE Project SET group_id=NULL,sort_order=$order WHERE id=$id;",
                ("$id", project.Id), ("$order", project.IsPinned ? project.SortOrder : nextOrder++));
        Execute(connection, transaction, "DELETE FROM ProjectGroup WHERE id=$id;", ("$id", id));
    });

    public void MoveProjectGroup(string id, string targetId, bool after) => Write((connection, transaction) =>
    {
        var groups = ReadSnapshot(connection, transaction).ProjectGroups!.ToList();
        var source = groups.FirstOrDefault(group => group.Id == id) ?? throw new KeyNotFoundException("项目分组不存在。");
        _ = groups.FirstOrDefault(group => group.Id == targetId) ?? throw new KeyNotFoundException("项目分组不存在。");
        if (id == targetId) return;
        groups.Remove(source);
        groups.Insert(groups.FindIndex(group => group.Id == targetId) + (after ? 1 : 0), source);
        for (var index = 0; index < groups.Count; index++)
            Execute(connection, transaction, "UPDATE ProjectGroup SET sort_order=$order WHERE id=$id;",
                ("$id", groups[index].Id), ("$order", index));
    });

    private static ProjectGroup RequireProjectGroup(SqliteConnection connection, SqliteTransaction? transaction, string id)
        => ReadProjectGroup(connection, transaction, id) ?? throw new KeyNotFoundException("项目分组不存在。");

    private static long NextProjectOrder(SqliteConnection connection, SqliteTransaction transaction, bool pinned, string? groupId)
        => Convert.ToInt64(Scalar(connection, transaction, """
            SELECT COALESCE(MAX(sort_order),-1)+1 FROM Project
            WHERE is_pinned=$pinned AND ($pinned=1 OR group_id IS $group);
            """, ("$pinned", pinned ? 1 : 0), ("$group", groupId)));

    private static string ValidateProjectGroupName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("项目分组名称不能为空。", nameof(name));
        if (name.Length > 200) throw new ArgumentException("项目分组名称不能超过 200 个字符。", nameof(name));
        return name;
    }

    private static void EnsureProjectGroupNameAvailable(SqliteConnection connection, SqliteTransaction transaction,
        string name, string? exceptId = null)
    {
        var id = Scalar(connection, transaction, "SELECT id FROM ProjectGroup WHERE name_key=$key;", ("$key", name.ToUpperInvariant())) as string;
        if (id != null && id != exceptId) throw new InvalidOperationException("已存在同名项目分组。");
    }
}
