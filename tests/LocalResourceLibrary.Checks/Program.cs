using LocalResourceLibrary.Core;
using Microsoft.Data.Sqlite;

namespace LocalResourceLibrary.Checks;

internal static class Program
{
    private static int Main()
    {
        (string Name, Action Run)[] checks =
        [
            ("Adding files and folders stores references only", AddReferences),
            ("Canonical paths share one item across projects", CanonicalDeduplication),
            ("Alias, description, and note preserve the physical resource", EditContext),
            ("Local search covers every requested field", SearchFields),
            ("SQLite reopens with metadata and relationships intact", Persistence),
            ("Physical rename preserves item identity and context", RenamePreservesContext),
            ("Physical rename rejects unsafe names and overwrites", RenameRejections),
            ("Case-only physical rename preserves the item", CaseOnlyRename),
            ("Missing resources keep their context and old target", MissingContext),
            ("Repair restores the reference and preserves identity", RepairPreservesContext),
            ("Repair rejects wrong types, missing paths, and duplicates", RepairRejections),
            ("Library and project removal never delete physical resources", LogicalRemoval),
            ("Folder rename updates all registered descendants", RenameFolderDescendants),
            ("Folder repair updates descendant references", RepairFolderDescendants),
            ("Folder destination conflicts leave all state untouched", FolderConflictIsAtomic),
            ("Invalid project updates do not partly write metadata", InvalidProjectIsAtomic),
            ("Open history advances only when the shell succeeds", OpenHistory),
            ("Failed physical moves leave references unchanged", FailedMoveIsAtomic),
            ("Partial add reports invalid inputs and keeps valid references", PartialAdd),
            ("Project edits propagate to search and membership", ProjectEdits),
            ("Windows path aliases and Unicode casing deduplicate", WindowsPathAliases),
            ("Re-adding a missing reference can attach another project", MissingMembership),
            ("Case-only folder rename updates descendant casing", CaseOnlyFolderRename),
            ("Folder repair handles overlapping old and new path keys", RepairOverlappingKeys),
            ("SQLite rename failure rolls back the physical move", DatabaseRenameRollback),
            ("SQLite metadata failure rolls back fields and memberships", DatabaseMetadataRollback),
            ("Failed case-only rename restores the original directory entry", CaseOnlyRenameRollback)
        ];
        var failures = new List<string>();
        foreach (var (name, run) in checks)
        {
            try
            {
                run();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures.Add(name);
                Console.WriteLine($"FAIL  {name}\n      {exception}");
            }
        }
        Console.WriteLine($"\n{checks.Length - failures.Count}/{checks.Length} checks passed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void AddReferences()
    {
        using var fixture = new Fixture();
        var path = fixture.File("reference.txt", "original content");
        var folder = fixture.Folder("repository");
        var result = fixture.Library.AddPaths([path, folder]);
        Equal(2, result.Added, "Two resources must be registered.");
        Equal(0, result.Errors.Count, "Valid paths must not produce errors.");
        Equal(2, fixture.Library.GetSnapshot().Items.Count, "Both types must be present.");
        Equal("file", fixture.ItemAt(path).Type, "Files must retain their resource type.");
        Equal("folder", fixture.ItemAt(folder).Type, "Folders must retain their resource type.");
        Equal("original content", System.IO.File.ReadAllText(path), "Adding must not rewrite the file.");
        Equal(1, Directory.GetFiles(fixture.SourceRoot, "*", SearchOption.AllDirectories).Length,
            "Adding references must not copy files.");
        Require(fixture.Library.GetSnapshot().Items.All(item => Path.IsPathFullyQualified(item.Target)),
            "Targets must be absolute paths.");
    }

    private static void CanonicalDeduplication()
    {
        using var fixture = new Fixture();
        var path = fixture.File("Chinese-中文", "manual.txt", "content");
        var first = fixture.Library.CreateProject("PDMS Learning");
        var second = fixture.Library.CreateProject("ModelCreator");
        fixture.Library.AddPaths([path], first.Id);
        var result = fixture.Library.AddPaths([path.ToUpperInvariant()], second.Id);
        Equal(0, result.Added, "Casing differences must not create another item.");
        Equal(1, result.Existing, "The existing resource must be reused.");
        fixture.Library.AddPaths([Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path))], second.Id);
        var item = fixture.ItemAt(path);
        Equal(1, fixture.Library.GetSnapshot().Items.Count, "The file must exist once in the library.");
        SetEqual([first.Id, second.Id], item.Projects.Select(project => project.Id), "Membership must be many-to-many.");

        var folder = fixture.Folder("FolderCase");
        fixture.Library.AddPaths([folder]);
        fixture.Library.AddPaths([folder.ToUpperInvariant() + Path.DirectorySeparatorChar]);
        Equal(2, fixture.Library.GetSnapshot().Items.Count, "Trailing separators must not duplicate folders.");
    }

    private static void EditContext()
    {
        using var fixture = new Fixture();
        var path = fixture.File("QICHUANG-SITE-2026.txt", "original content");
        var item = fixture.Add(path);
        var project = fixture.Library.CreateProject("ModelCreator");
        fixture.Library.UpdateItem(item.Id, "PDMS TXT Export Example", "Format compatibility sample", "Review hierarchy later", [project.Id]);
        var updated = fixture.Item(item.Id);
        Equal("PDMS TXT Export Example", updated.DisplayName, "Alias must be the preferred display name.");
        Equal("QICHUANG-SITE-2026.txt", updated.RealName, "RealName must remain the physical name.");
        Equal(path, updated.Target, "Alias edits must not change the stored target.");
        Require(System.IO.File.Exists(path), "Alias edits must not rename the physical file.");
        Require(!System.IO.File.Exists(Path.Combine(fixture.SourceRoot, updated.Alias)), "Alias must not create a physical file.");
        Equal("Format compatibility sample", updated.Description, "Description must remain distinct.");
        Equal("Review hierarchy later", updated.Note, "Note must remain distinct.");
        fixture.Library.UpdateItem(item.Id, "", updated.Description, updated.Note, [project.Id]);
        Equal(updated.RealName, fixture.Item(item.Id).DisplayName, "An empty alias must fall back to the real name.");
    }

    private static void SearchFields()
    {
        using var fixture = new Fixture();
        var path = fixture.File("unique-path-segment", "FilenameNeedle.txt", "content");
        var item = fixture.Add(path);
        var unrelated = fixture.Add(fixture.File("unrelated.txt", "content"));
        var project = fixture.Library.CreateProject("ProjectNeedle");
        fixture.Library.UpdateItem(item.Id, "AliasNeedle", "DescriptionNeedle 中文兼容", "NoteNeedle", [project.Id]);
        var provider = new LocalTextSearchProvider();
        var items = fixture.Library.GetSnapshot().Items;
        foreach (var query in new[] { "AliasNeedle", "FilenameNeedle", "unique-path-segment", "DescriptionNeedle", "NoteNeedle", "ProjectNeedle", "中文兼容", "aliasneedle" })
        {
            var results = provider.Search(items, query).ToArray();
            Equal(1, results.Length, $"Query '{query}' must match exactly the relevant item.");
            Equal(item.Id, results[0].Id, $"Query '{query}' must match the expected item.");
            Require(results.All(result => result.Id != unrelated.Id), "Unrelated items must not match.");
        }
        Equal(2, provider.Search(items, "").Count(), "Empty search must show the library.");
        Equal(0, provider.Search(items, "no-such-search-value").Count(), "Unknown text must not match.");
    }

    private static void Persistence()
    {
        using var fixture = new Fixture();
        var item = fixture.Add(fixture.File("persist.txt", "content"));
        var project = fixture.Library.CreateProject("Persistent project", "Project description");
        fixture.Library.UpdateItem(item.Id, "Persistent alias", "Persistent description", "Persistent note", [project.Id]);
        fixture.Library.Open(item.Id);
        var before = fixture.Item(item.Id);
        var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform).GetSnapshot();
        var after = reopened.Items.Single();
        SameContext(before, after);
        Equal(before.Target, after.Target, "Targets must persist.");
        Equal(before.CreatedAt, after.CreatedAt, "Creation timestamp must persist.");
        Equal(before.UpdatedAt, after.UpdatedAt, "Update timestamp must persist.");
        Equal(before.LastOpenedAt, after.LastOpenedAt, "Last-opened timestamp must persist.");
        Equal(1, after.OpenCount, "Open history must persist.");
        Equal("Project description", reopened.Projects.Single().Description, "Project descriptions must persist.");
        using var databaseFile = new FileStream(fixture.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[16];
        databaseFile.ReadExactly(header);
        Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(header), "Persistence must use a real SQLite database.");
    }

    private static void RenamePreservesContext()
    {
        using var fixture = new Fixture();
        var oldPath = fixture.File("old-name.txt", "physical content");
        var before = fixture.ContextItem(oldPath);
        fixture.Library.Open(before.Id);
        before = fixture.Item(before.Id);
        fixture.Library.RenamePhysical(before.Id, "新名称.txt");
        var after = fixture.Item(before.Id);
        var destination = Path.Combine(fixture.SourceRoot, "新名称.txt");
        Equal(destination, after.Target, "Rename must update the target.");
        Require(!System.IO.File.Exists(oldPath) && System.IO.File.Exists(destination), "The physical file must actually be renamed.");
        Equal("physical content", System.IO.File.ReadAllText(destination), "Rename must preserve file content.");
        SameContext(before, after);
        Equal(before.OpenCount, after.OpenCount, "Rename must preserve open count.");
        Equal(before.LastOpenedAt, after.LastOpenedAt, "Rename must preserve last-opened time.");
        Equal(before.CreatedAt, after.CreatedAt, "Rename must preserve creation time.");
    }

    private static void RenameRejections()
    {
        using var fixture = new Fixture();
        var oldPath = fixture.File("source.txt", "source");
        var occupiedPath = fixture.File("occupied.txt", "must survive");
        var before = fixture.ContextItem(oldPath);
        foreach (var name in new[] { "", "..", "../escape.txt", "a\\b.txt", "bad:name.txt", "CON.txt", "NUL", "LPT1.log", "trailing.", "trailing ", "occupied.txt" })
        {
            Throws(() => fixture.Library.RenamePhysical(before.Id, name), $"Unsafe or occupied name '{name}' must be rejected.");
            var after = fixture.Item(before.Id);
            Equal(oldPath, after.Target, "Rejected rename must preserve the target.");
            SameContext(before, after);
            Require(System.IO.File.Exists(oldPath), "Rejected rename must preserve the source file.");
            Equal("must survive", System.IO.File.ReadAllText(occupiedPath), "Rename must not overwrite an existing file.");
        }
    }

    private static void CaseOnlyRename()
    {
        using var fixture = new Fixture();
        var before = fixture.ContextItem(fixture.File("camelCase.txt", "content"));
        fixture.Library.RenamePhysical(before.Id, "CAMELCASE.txt");
        var after = fixture.Item(before.Id);
        Equal("CAMELCASE.txt", Path.GetFileName(after.Target), "Case-only rename must update display casing.");
        SameContext(before, after);
        Equal("CAMELCASE.txt", Path.GetFileName(Directory.GetFiles(fixture.SourceRoot).Single()), "The actual directory entry casing must change.");
    }

    private static void MissingContext()
    {
        using var fixture = new Fixture();
        var path = fixture.File("missing.txt", "content");
        var before = fixture.ContextItem(path);
        System.IO.File.Delete(path);
        var missing = fixture.Item(before.Id);
        Require(missing.IsMissing, "A deleted target must be marked missing.");
        Equal(path, missing.Target, "Missing detection must retain the old path.");
        SameContext(before, missing);
        Throws(() => fixture.Library.Open(before.Id), "Opening a missing resource must fail visibly.");
        Equal(0, fixture.Item(before.Id).OpenCount, "Failed missing-path opens must not advance history.");
        Require(fixture.Library.GetSnapshot().Items.Any(item => item.Id == before.Id), "Missing items must never be removed automatically.");
    }

    private static void RepairPreservesContext()
    {
        using var fixture = new Fixture();
        var oldPath = fixture.File("old-target.txt", "content");
        var before = fixture.ContextItem(oldPath);
        var newPath = Path.Combine(fixture.SourceRoot, "relocated.txt");
        System.IO.File.Move(oldPath, newPath);
        Require(fixture.Item(before.Id).IsMissing, "The old target must be missing before repair.");
        fixture.Library.RepairPath(before.Id, newPath);
        var repaired = fixture.Item(before.Id);
        Equal(newPath, repaired.Target, "Repair must point at the selected existing resource.");
        Require(!repaired.IsMissing, "Successful repair must clear missing state.");
        SameContext(before, repaired);
        Equal("content", System.IO.File.ReadAllText(newPath), "Repair must not modify resource content.");
        Require(!System.IO.File.Exists(oldPath), "Repair must not recreate the old target.");
    }

    private static void RepairRejections()
    {
        using var fixture = new Fixture();
        var before = fixture.ContextItem(fixture.File("first.txt", "first"));
        var secondPath = fixture.File("second.txt", "second");
        fixture.Add(secondPath);
        var folder = fixture.Folder("folder");
        foreach (var target in new[] { folder, secondPath, Path.Combine(fixture.SourceRoot, "not-present.txt") })
        {
            Throws(() => fixture.Library.RepairPath(before.Id, target), "Wrong-type, duplicate, or missing repair targets must be rejected.");
            var after = fixture.Item(before.Id);
            Equal(before.Target, after.Target, "Rejected repair must preserve the original target.");
            SameContext(before, after);
        }
        var folderItem = fixture.Add(folder);
        Throws(() => fixture.Library.RepairPath(folderItem.Id, secondPath), "A folder cannot be repaired to a file.");
    }

    private static void LogicalRemoval()
    {
        using var fixture = new Fixture();
        var path = fixture.File("keep-me.txt", "content");
        var before = fixture.ContextItem(path);
        var projects = before.Projects.ToArray();
        fixture.Library.RemoveFromProject(before.Id, projects[0].Id);
        Equal(1, fixture.Item(before.Id).Projects.Count, "Removing one membership must keep the other.");
        Require(System.IO.File.Exists(path), "Project removal must not delete the file.");
        fixture.Library.AddToProject(before.Id, projects[0].Id);
        fixture.Library.DeleteProject(projects[0].Id);
        Equal(1, fixture.Item(before.Id).Projects.Count, "Project deletion must only remove that project's joins.");
        Require(System.IO.File.Exists(path), "Deleting a project must not delete physical files.");
        fixture.Library.RemoveFromLibrary(before.Id);
        Equal(0, fixture.Library.GetSnapshot().Items.Count, "Removing from the library must remove the item.");
        Require(System.IO.File.Exists(path), "Removing from the library must not delete the physical file.");
        Equal(1, fixture.Library.GetSnapshot().Projects.Count, "Removing an item must preserve projects.");
        var addedAgain = fixture.Add(path);
        Equal(0, addedAgain.Projects.Count, "Re-adding a removed item must not resurrect deleted memberships.");
    }

    private static void RenameFolderDescendants()
    {
        using var fixture = new Fixture();
        var oldFolder = fixture.Folder("parent");
        var childPath = fixture.File("parent", "nested", "child.txt", "content");
        var siblingPath = fixture.File("parent-sibling", "unrelated.txt", "sibling");
        var folder = fixture.ContextItem(oldFolder);
        var nestedFolder = fixture.Add(Path.GetDirectoryName(childPath)!);
        var child = fixture.ContextItem(childPath);
        var sibling = fixture.Add(siblingPath);
        fixture.Library.RenamePhysical(folder.Id, "renamed-parent");
        var newFolder = Path.Combine(fixture.SourceRoot, "renamed-parent");
        Equal(newFolder, fixture.Item(folder.Id).Target, "The parent folder target must change.");
        Equal(Path.Combine(newFolder, "nested"), fixture.Item(nestedFolder.Id).Target, "Registered subfolder references must follow their parent.");
        var movedChild = fixture.Item(child.Id);
        Equal(Path.Combine(newFolder, "nested", "child.txt"), movedChild.Target, "Registered file references must follow their parent.");
        SameContext(child, movedChild);
        SameContext(folder, fixture.Item(folder.Id));
        Require(!movedChild.IsMissing && System.IO.File.Exists(movedChild.Target), "Descendant references must remain usable.");
        Equal(siblingPath, fixture.Item(sibling.Id).Target, "A common text prefix is not a descendant path.");
    }

    private static void RepairFolderDescendants()
    {
        using var fixture = new Fixture();
        var oldFolder = fixture.Folder("old-folder");
        var childPath = fixture.File("old-folder", "nested", "child.txt", "content");
        var folder = fixture.ContextItem(oldFolder);
        var child = fixture.ContextItem(childPath);
        var newFolder = Path.Combine(fixture.SourceRoot, "moved-folder");
        Directory.Move(oldFolder, newFolder);
        fixture.Library.RepairPath(folder.Id, newFolder);
        var repairedChild = fixture.Item(child.Id);
        Equal(Path.Combine(newFolder, "nested", "child.txt"), repairedChild.Target, "Repairing the parent must update registered descendants.");
        Require(!repairedChild.IsMissing, "Repaired descendants must resolve.");
        SameContext(child, repairedChild);
        SameContext(folder, fixture.Item(folder.Id));
        Equal(0, fixture.Platform.MoveCalls, "Repair must never physically move a resource.");
    }

    private static void FolderConflictIsAtomic()
    {
        using var fixture = new Fixture();
        var oldFolder = fixture.Folder("source-folder");
        var childPath = fixture.File("source-folder", "same.txt", "source");
        var conflictPath = fixture.File("destination-folder", "same.txt", "old destination");
        var folder = fixture.ContextItem(oldFolder);
        var child = fixture.ContextItem(childPath);
        var conflict = fixture.ContextItem(conflictPath);
        Directory.Delete(Path.GetDirectoryName(conflictPath)!, recursive: true);
        Throws(() => fixture.Library.RenamePhysical(folder.Id, "destination-folder"), "Renaming onto a registered descendant target must reject a uniqueness conflict.");
        Equal(oldFolder, fixture.Item(folder.Id).Target, "A conflict must leave the parent reference unchanged.");
        Equal(childPath, fixture.Item(child.Id).Target, "A conflict must leave descendants unchanged.");
        Equal(conflictPath, fixture.Item(conflict.Id).Target, "A conflict must preserve the old missing reference.");
        Require(Directory.Exists(oldFolder), "A conflict must preserve the physical parent.");
        Require(!Directory.Exists(Path.GetDirectoryName(conflictPath)!), "A conflict must not leave the source moved.");
        SameContext(folder, fixture.Item(folder.Id));
        SameContext(child, fixture.Item(child.Id));
    }

    private static void InvalidProjectIsAtomic()
    {
        using var fixture = new Fixture();
        var before = fixture.ContextItem(fixture.File("context.txt", "content"));
        Throws(() => fixture.Library.UpdateItem(before.Id, "uncommitted alias", "uncommitted description", "uncommitted note", [before.Projects[0].Id, "missing-project"]),
            "Unknown project membership must reject the update.");
        var after = fixture.Item(before.Id);
        SameContext(before, after);
        Equal(before.UpdatedAt, after.UpdatedAt, "Failed updates must not advance the timestamp.");
        var newPath = fixture.File("not-added.txt", "content");
        Throws(() => fixture.Library.AddPaths([newPath], "missing-project"), "Unknown project must reject add before writing an item.");
        Equal(1, fixture.Library.GetSnapshot().Items.Count, "A rejected project add must not leave an unassigned item.");
        Throws(() => fixture.Library.AddToProject(before.Id, "missing-project"), "Invalid membership must not be persisted.");
        SameContext(before, fixture.Item(before.Id));
    }

    private static void OpenHistory()
    {
        using var fixture = new Fixture();
        var item = fixture.Add(fixture.File("open.txt", "content"));
        fixture.Platform.FailOpen = true;
        Throws(() => fixture.Library.Open(item.Id), "Shell launch errors must be reported.");
        Equal(0, fixture.Item(item.Id).OpenCount, "Failed launch must not increase open count.");
        Require(fixture.Item(item.Id).LastOpenedAt is null, "Failed launch must not set last-opened time.");
        fixture.Platform.FailOpen = false;
        fixture.Library.Open(item.Id);
        fixture.Library.Open(item.Id);
        var opened = fixture.Item(item.Id);
        Equal(2, opened.OpenCount, "Each successful open must increment once.");
        Require(opened.LastOpenedAt is not null, "Successful launch must set last-opened time.");
        Equal(item.Target, fixture.Platform.LastOpenedPath, "The platform must receive the real target.");
        Equal("file", fixture.Platform.LastOpenedType, "The platform must receive the resource type.");
        fixture.Library.OpenLocation(item.Id);
        Equal(item.Target, fixture.Platform.LastLocationPath, "Open location must receive the real target.");
        Equal(2, fixture.Item(item.Id).OpenCount, "Opening the containing location must not count as opening the resource.");
        var folder = fixture.Add(fixture.Folder("open-folder"));
        fixture.Library.Open(folder.Id);
        Equal("folder", fixture.Platform.LastOpenedType, "Folder launch must preserve resource dispatch type.");
    }

    private static void FailedMoveIsAtomic()
    {
        using var fixture = new Fixture();
        var before = fixture.ContextItem(fixture.File("locked.txt", "content"));
        fixture.Platform.FailMove = true;
        Throws(() => fixture.Library.RenamePhysical(before.Id, "renamed.txt"), "Physical move failure must be reported.");
        var after = fixture.Item(before.Id);
        Equal(before.Target, after.Target, "Physical failure must not update the target.");
        Equal(before.UpdatedAt, after.UpdatedAt, "Physical failure must not advance the timestamp.");
        SameContext(before, after);
        Require(System.IO.File.Exists(before.Target), "Physical failure must preserve the source file.");
        Require(!System.IO.File.Exists(Path.Combine(fixture.SourceRoot, "renamed.txt")), "Physical failure must not create a destination.");
    }

    private static void PartialAdd()
    {
        using var fixture = new Fixture();
        var path = fixture.File("valid.txt", "content");
        var result = fixture.Library.AddPaths([path, Path.Combine(fixture.SourceRoot, "missing.txt")]);
        Equal(1, result.Added, "A valid entry should succeed in a mixed add.");
        Equal(1, result.Errors.Count, "Invalid inputs must produce actionable errors.");
        Equal(1, fixture.Library.GetSnapshot().Items.Count, "An invalid target must not be inserted.");
    }

    private static void ProjectEdits()
    {
        using var fixture = new Fixture();
        var item = fixture.Add(fixture.File("project.txt", "content"));
        var project = fixture.Library.CreateProject("BeforeName", "BeforeDescription");
        fixture.Library.AddToProject(item.Id, project.Id);
        fixture.Library.AddToProject(item.Id, project.Id);
        Equal(1, fixture.Item(item.Id).Projects.Count, "Adding the same membership twice must be idempotent.");
        fixture.Library.UpdateProject(project.Id, "AfterName", "AfterDescription");
        var updated = fixture.Item(item.Id);
        Equal(project.Id, updated.Projects.Single().Id, "Project updates must preserve identity.");
        Equal("AfterName", updated.Projects.Single().Name, "Membership must reflect project name edits.");
        Equal("AfterDescription", updated.Projects.Single().Description, "Project description edits must persist.");
        Equal(1, new LocalTextSearchProvider().Search(fixture.Library.GetSnapshot().Items, "AfterName").Count(), "Search must reflect updated project names.");
        Throws(() => fixture.Library.CreateProject("   "), "Blank project names must be rejected.");
    }

    private static void WindowsPathAliases()
    {
        using var fixture = new Fixture();
        var path = fixture.File("Unicode-Ä中文", "Über.txt", "content");
        var before = fixture.Add(path);
        foreach (var alias in new[] { path.ToUpperInvariant(), path + ".", path + " ", @"\\?\" + path })
        {
            var result = fixture.Library.AddPaths([alias]);
            Equal(0, result.Added, "Windows aliases must not create new items.");
            Equal(1, result.Existing, "Windows aliases must resolve the existing item.");
            Equal(0, result.Errors.Count, "Supported Windows aliases must be accepted.");
        }
        Equal(1, fixture.Library.GetSnapshot().Items.Count, "Unicode and path aliases must preserve a single logical item.");
        Equal(before.Target, fixture.Item(before.Id).Target, "Re-adding aliases must preserve the original display path.");
    }

    private static void MissingMembership()
    {
        using var fixture = new Fixture();
        var before = fixture.ContextItem(fixture.File("missing-membership.txt", "content"));
        System.IO.File.Delete(before.Target);
        var extraProject = fixture.Library.CreateProject("Extra project");
        var result = fixture.Library.AddPaths([before.Target], extraProject.Id);
        Equal(0, result.Added, "A missing registered path must not create a new item.");
        Equal(1, result.Existing, "A missing registered path must resolve its existing item.");
        var after = fixture.Item(before.Id);
        Require(after.IsMissing, "Re-adding a missing path must retain missing status.");
        Equal(3, after.Projects.Count, "Another project can refer to a known missing resource.");
        Equal(before.Alias, after.Alias, "Membership changes must retain the missing resource's context.");
    }

    private static void CaseOnlyFolderRename()
    {
        using var fixture = new Fixture();
        var folderPath = fixture.Folder("CaseFolder");
        var child = fixture.ContextItem(fixture.File("CaseFolder", "child.txt", "content"));
        var folder = fixture.ContextItem(folderPath);
        fixture.Library.RenamePhysical(folder.Id, "CASEFOLDER");
        var newFolderPath = Path.Combine(fixture.SourceRoot, "CASEFOLDER");
        Equal(newFolderPath, fixture.Item(folder.Id).Target, "Folder casing must update in the library.");
        Equal(Path.Combine(newFolderPath, "child.txt"), fixture.Item(child.Id).Target, "Descendant targets must adopt new parent casing.");
        Equal("CASEFOLDER", Path.GetFileName(Directory.GetDirectories(fixture.SourceRoot).Single()), "Physical folder casing must change.");
        SameContext(folder, fixture.Item(folder.Id));
        SameContext(child, fixture.Item(child.Id));
    }

    private static void RepairOverlappingKeys()
    {
        using var fixture = new Fixture();
        var newRoot = fixture.Folder("repeat");
        var originalRoot = fixture.Folder("repeat", "repeat");
        var originalMiddle = fixture.Folder("repeat", "repeat", "repeat");
        var originalDeep = fixture.Folder("repeat", "repeat", "repeat", "repeat");
        var root = fixture.ContextItem(originalRoot);
        var middle = fixture.ContextItem(originalMiddle);
        var deep = fixture.ContextItem(originalDeep);
        fixture.Library.RepairPath(root.Id, newRoot);
        Equal(newRoot, fixture.Item(root.Id).Target, "The parent must adopt its repaired ancestor target.");
        Equal(originalRoot, fixture.Item(middle.Id).Target, "The middle item must take the parent's old path without a false conflict.");
        Equal(originalMiddle, fixture.Item(deep.Id).Target, "The deepest item must take the middle item's old path without a false conflict.");
        SameContext(root, fixture.Item(root.Id));
        SameContext(middle, fixture.Item(middle.Id));
        SameContext(deep, fixture.Item(deep.Id));
        Equal(0, fixture.Platform.MoveCalls, "Repairing references must not move any physical folders.");
    }

    private static void DatabaseRenameRollback()
    {
        using var fixture = new Fixture();
        var oldFolder = fixture.Folder("rollback-folder");
        var childPath = fixture.File("rollback-folder", "child.txt", "must survive");
        var folder = fixture.ContextItem(oldFolder);
        var child = fixture.ContextItem(childPath);
        fixture.ExecuteSql("""
            CREATE TRIGGER reject_file_target_update BEFORE UPDATE OF target ON Item
            WHEN OLD.type = 'file'
            BEGIN SELECT RAISE(ABORT, 'Injected target update failure'); END;
            """);
        Throws(() => fixture.Library.RenamePhysical(folder.Id, "uncommitted-folder"), "A database write failure after physical move must be reported.");
        Equal(2, fixture.Platform.MoveCalls, "Database failure must move the physical folder back exactly once.");
        Equal(oldFolder, fixture.Item(folder.Id).Target, "Database failure must roll back the parent target.");
        Equal(childPath, fixture.Item(child.Id).Target, "Database failure must roll back descendant targets.");
        Require(Directory.Exists(oldFolder), "Database failure must restore the original physical folder.");
        Require(!Directory.Exists(Path.Combine(fixture.SourceRoot, "uncommitted-folder")), "No uncommitted physical destination may remain.");
        Equal("must survive", System.IO.File.ReadAllText(childPath), "Rollback must preserve the original file content.");
        SameContext(folder, fixture.Item(folder.Id));
        SameContext(child, fixture.Item(child.Id));
        fixture.ExecuteSql("DROP TRIGGER reject_file_target_update;");
        fixture.Library.RenamePhysical(folder.Id, "committed-folder");
        Require(Directory.Exists(Path.Combine(fixture.SourceRoot, "committed-folder")), "After rollback the resource must still be renameable.");
    }

    private static void DatabaseMetadataRollback()
    {
        using var fixture = new Fixture();
        var before = fixture.ContextItem(fixture.File("metadata-rollback.txt", "content"));
        var another = fixture.Library.CreateProject("Another project");
        fixture.ExecuteSql("""
            CREATE TRIGGER reject_new_membership BEFORE INSERT ON ProjectItem
            BEGIN SELECT RAISE(ABORT, 'Injected membership write failure'); END;
            """);
        Throws(() => fixture.Library.UpdateItem(before.Id, "uncommitted alias", "uncommitted description", "uncommitted note", [another.Id]),
            "A membership write failure after metadata edits must be reported.");
        var after = fixture.Item(before.Id);
        SameContext(before, after);
        Equal(before.UpdatedAt, after.UpdatedAt, "Database rollback must restore the original update timestamp.");
    }

    private static void CaseOnlyRenameRollback()
    {
        using var fixture = new Fixture();
        var before = fixture.ContextItem(fixture.File("CaseRename.txt", "content"));
        fixture.Platform.FailMoveOnCall = 2;
        Throws(() => fixture.Library.RenamePhysical(before.Id, "CASERENAME.txt"), "A failure moving the intermediate filename to its destination must be reported.");
        Equal(3, fixture.Platform.MoveCalls, "A case-only rename must restore the original path when its second move fails.");
        var after = fixture.Item(before.Id);
        Equal(before.Target, after.Target, "A failed case-only rename must preserve the database target.");
        Equal(before.UpdatedAt, after.UpdatedAt, "A failed case-only rename must preserve the update time.");
        SameContext(before, after);
        var remainingFiles = Directory.GetFiles(fixture.SourceRoot);
        Equal(1, remainingFiles.Length, "No temporary rename files may remain after successful recovery.");
        Equal("CaseRename.txt", Path.GetFileName(remainingFiles[0]), "Recovery must restore the original physical filename casing.");
    }

    private static void SameContext(ResourceItem before, ResourceItem after)
    {
        Equal(before.Id, after.Id, "Item ID must stay stable.");
        Equal(before.Type, after.Type, "Item type must stay stable.");
        Equal(before.Alias, after.Alias, "Alias must stay intact.");
        Equal(before.Description, after.Description, "Description must stay intact.");
        Equal(before.Note, after.Note, "Note must stay intact.");
        SetEqual(before.Projects.Select(project => project.Id), after.Projects.Select(project => project.Id), "All project relationships must stay intact.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}");
    }

    private static void SetEqual(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        Require(new HashSet<string>(expected).SetEquals(actual), message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "LocalResourceLibrary.Checks", Guid.NewGuid().ToString("N"));
        public string SourceRoot { get; }
        public string DatabasePath { get; }
        public TestResourcePlatform Platform { get; } = new();
        public LibraryService Library { get; }

        public Fixture()
        {
            SourceRoot = Path.Combine(root, "resources");
            DatabasePath = Path.Combine(root, "state", "library.db");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            Library = new LibraryService(DatabasePath, Platform);
        }

        public string File(params string[] parts)
        {
            if (parts.Length < 2) throw new ArgumentException("File requires at least a filename and content.");
            var path = Path.Combine([SourceRoot, .. parts[..^1]]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, parts[^1]);
            return path;
        }

        public string Folder(params string[] parts)
        {
            var path = Path.Combine([SourceRoot, .. parts]);
            Directory.CreateDirectory(path);
            return path;
        }

        public ResourceItem Item(string id) => Library.GetSnapshot().Items.Single(item => item.Id == id);
        public ResourceItem ItemAt(string path) => Library.GetSnapshot().Items.Single(item => string.Equals(item.Target.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

        public ResourceItem Add(string path)
        {
            var result = Library.AddPaths([path]);
            Require(result.Errors.Count == 0, $"Setup add failed: {string.Join("; ", result.Errors)}");
            return ItemAt(path);
        }

        public ResourceItem ContextItem(string path)
        {
            var item = Add(path);
            var first = Library.CreateProject($"Project Alpha {item.Id}");
            var second = Library.CreateProject($"Project Beta {item.Id}");
            Library.UpdateItem(item.Id, "Example alias", "Example description", "Example note", [first.Id, second.Id]);
            return Item(item.Id);
        }

        public void ExecuteSql(string sql)
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            // Only this fixture's unique temporary directory is ever eligible for cleanup.
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LocalResourceLibrary.Checks"));
            var resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside the test fixture directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class TestResourcePlatform : IResourcePlatform
    {
        public bool FailOpen { get; set; }
        public bool FailMove { get; set; }
        public int? FailMoveOnCall { get; set; }
        public int MoveCalls { get; private set; }
        public string? LastOpenedPath { get; private set; }
        public string? LastOpenedType { get; private set; }
        public string? LastLocationPath { get; private set; }

        public bool FileExists(string path) => System.IO.File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void Open(string path, string type)
        {
            if (FailOpen) throw new IOException("Simulated Windows shell launch failure.");
            LastOpenedPath = path;
            LastOpenedType = type;
        }

        public void OpenLocation(string path, string type) => LastLocationPath = path;

        public void Move(string oldPath, string newPath, string type)
        {
            MoveCalls++;
            if (FailMove || MoveCalls == FailMoveOnCall) throw new IOException("Simulated Windows move failure.");
            if (type == "folder") Directory.Move(oldPath, newPath);
            else System.IO.File.Move(oldPath, newPath);
        }
    }
}
