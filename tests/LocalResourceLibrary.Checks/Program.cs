using LocalResourceLibrary.Core;
using Microsoft.Data.Sqlite;

namespace LocalResourceLibrary.Checks;

internal static class Program
{
    private static int Main()
    {
        (string Name, Action Run)[] checks =
        [
            ("Project pinning and grouped ordering survive restart and reject cross-group moves", ProjectOrdering),
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
            ("Bulk removal preserves originals and unrelated project memberships", BulkRemoval),
            ("Bulk removal rolls back every record on validation or SQLite failure", BulkRemovalRollback),
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
            ("Failed case-only rename restores the original directory entry", CaseOnlyRenameRollback),
            ("Files capture physical identity while folders keep path-only behavior", CaptureFileIdentity),
            ("External file rename recovers after restart with native Windows identity", ExternalRenameRecovery),
            ("Existing paths open before physical identity recovery", ExistingPathWins),
            ("Recovery failure preserves the missing item and its context", UnrecoverableFile),
            ("Recovery verifies file ID, volume ID, existence, and resource type", RejectInvalidRecoveryCandidates),
            ("Unavailable file identity keeps ordinary opening usable", UnavailableFileIdentity),
            ("External folder rename does not trigger folder recovery", NoFolderRecovery),
            ("Explicit file repair captures the selected file identity", RepairCapturesIdentity),
            ("Recovery never merges another item at the new path", RecoveryPathConflict),
            ("Version-one data migrates and backfills accessible files only", LegacyIdentityBackfill),
            ("Recovery database failure leaves the original reference unchanged", RecoveryDatabaseFailure),
            ("Shell failure keeps the recovered path without advancing history", RecoveryShellFailure),
            ("Expected identity access failures preserve collection and Missing behavior", IdentityAccessFailure),
            ("URLs use exact deduplication and retain their spelling", UrlExactDeduplication),
            ("Duplicate URLs preserve user fields and add project memberships", UrlDuplicateContext),
            ("URL editing is atomic on collision and membership failure", UrlEditRollback),
            ("URL icons and metadata persist without replacing manual edits", UrlPersistence),
            ("URL opening records only successful dispatch and skips file checks", UrlOpenHistory),
            ("URLs reject physical operations and coexist with folder transformations", UrlPhysicalSafety),
            ("Mixed URL and physical bulk operations retain unrelated records", UrlMixedBulk),
            ("Version-two databases retain physical identity during URL migration", UrlVersionTwoMigration),
            ("Invalid URL and icon inputs cannot create or partly edit records", UrlInputValidation)
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

    private static void ProjectOrdering()
    {
        using var fixture = new Fixture();
        var library = fixture.Library;
        library.CreateProject("Legacy B");
        library.CreateProject("Legacy A");
        fixture.ExecuteSql("ALTER TABLE Project DROP COLUMN is_pinned; ALTER TABLE Project DROP COLUMN sort_order; PRAGMA user_version=3;");
        library = new LibraryService(fixture.DatabasePath, fixture.Platform);
        Equal("Legacy A,Legacy B", string.Join(",", library.GetSnapshot(false).Projects.Select(p => p.Name)), "Migration preserves alphabetical order.");
        Equal(0, library.GetSnapshot(false).Projects.Count(p => p.IsPinned), "Migration leaves projects unpinned.");
        foreach (var project in library.GetSnapshot(false).Projects) library.DeleteProject(project.Id);
        var a = library.CreateProject("A");
        var b = library.CreateProject("B");
        var c = library.CreateProject("C");
        library.MoveProject(c.Id, a.Id, false);
        Equal("C,A,B", string.Join(",", library.GetSnapshot(false).Projects.Select(p => p.Name)), "Normal projects reorder.");
        library.SetProjectPinned(a.Id, true);
        library.SetProjectPinned(b.Id, true);
        library.MoveProject(b.Id, a.Id, false);
        Throws(() => library.MoveProject(c.Id, a.Id, true), "Cross-group moves must be rejected.");
        var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform);
        Equal("B,A,C", string.Join(",", reopened.GetSnapshot(false).Projects.Select(p => p.Name)), "Pinned order survives restart.");
        Equal(2, reopened.GetSnapshot(false).Projects.Count(p => p.IsPinned), "Pin state survives restart.");
        library.UpdateProject(b.Id, "Z");
        Equal(b.Id, library.GetSnapshot(false).Projects[0].Id, "Rename must preserve position.");
        library.SetProjectPinned(b.Id, false);
        library.SetProjectPinned(b.Id, false);
        Equal("A,C,Z", string.Join(",", library.GetSnapshot(false).Projects.Select(p => p.Name)), "Unpin appends to normal group.");
        var d = library.CreateProject("D");
        Equal(d.Id, library.GetSnapshot(false).Projects.Last().Id, "New projects append.");
        var before = string.Join(",", library.GetSnapshot(false).Projects.Select(p => p.Id));
        Throws(() => library.MoveProject(d.Id, "missing", false), "Missing targets must be rejected.");
        Equal(before, string.Join(",", library.GetSnapshot(false).Projects.Select(p => p.Id)), "Failed moves must preserve order.");
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

    private static void BulkRemoval()
    {
        using var fixture = new Fixture();
        var file = fixture.ContextItem(fixture.File("bulk-file.txt", "keep content"));
        var folder = fixture.Add(fixture.Folder("bulk-folder"));
        var untouched = fixture.Add(fixture.File("untouched.txt", "untouched"));
        foreach (var item in new[] { folder, untouched })
            fixture.Library.UpdateItem(item.Id, "", "", "", file.Projects.Select(project => project.Id));
        var projectId = file.Projects[0].Id;
        fixture.Library.RemoveItemsFromProject([file.Id, folder.Id, file.Id], projectId);
        Equal(1, fixture.Item(file.Id).Projects.Count, "The file keeps its other project.");
        Equal(1, fixture.Item(folder.Id).Projects.Count, "The folder keeps its other project.");
        Equal(2, fixture.Item(untouched.Id).Projects.Count, "Unselected memberships remain.");
        fixture.Library.RemoveItemsFromLibrary([file.Id, folder.Id, file.Id]);
        var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform).GetSnapshot();
        Equal(1, reopened.Items.Count, "Batch deletion persists after reopening.");
        Equal(untouched.Id, reopened.Items.Single().Id, "The unselected resource remains.");
        Equal(2, reopened.Projects.Count, "Resource deletion preserves projects.");
        Equal("keep content", System.IO.File.ReadAllText(file.Target), "The original file content remains.");
        Require(Directory.Exists(folder.Target), "The original folder remains.");
        Equal(2L, (long)fixture.SqlScalar("SELECT COUNT(*) FROM ProjectItem;")!, "No deleted-item memberships remain.");
    }

    private static void BulkRemovalRollback()
    {
        using var fixture = new Fixture();
        var first = fixture.ContextItem(fixture.File("first.txt", "first"));
        var second = fixture.ContextItem(fixture.File("second.txt", "second"));
        fixture.Library.UpdateItem(second.Id, second.Alias, second.Description, second.Note, first.Projects.Select(project => project.Id));
        second = fixture.Item(second.Id);
        Throws(() => fixture.Library.RemoveItemsFromLibrary([first.Id, "missing-id"]), "An invalid record must abort the whole batch.");
        Throws(() => fixture.Library.RemoveItemsFromProject([first.Id, "missing-id"], first.Projects[0].Id), "Invalid membership removal must abort the whole batch.");
        fixture.ExecuteSql($"""
            CREATE TRIGGER reject_second_removal BEFORE DELETE ON Item WHEN OLD.id = '{second.Id}'
            BEGIN SELECT RAISE(ABORT, 'Injected removal failure'); END;
            CREATE TRIGGER reject_second_membership BEFORE DELETE ON ProjectItem WHEN OLD.item_id = '{second.Id}'
            BEGIN SELECT RAISE(ABORT, 'Injected membership removal failure'); END;
            """);
        Throws(() => fixture.Library.RemoveItemsFromLibrary([first.Id, second.Id]), "A database failure must roll back earlier deletions.");
        Throws(() => fixture.Library.RemoveItemsFromProject([first.Id, second.Id], first.Projects[0].Id), "A database failure must roll back earlier membership removals.");
        Equal(2, fixture.Library.GetSnapshot().Items.Count, "Both records survive failed deletion.");
        SameContext(first, fixture.Item(first.Id));
        SameContext(second, fixture.Item(second.Id));
        Require(System.IO.File.Exists(first.Target) && System.IO.File.Exists(second.Target), "Both originals survive failed deletion.");
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

    private static void CaptureFileIdentity()
    {
        var identities = new TestFileIdentityProvider();
        using var fixture = new Fixture(identities);
        var path = fixture.File("identity.txt", "content");
        var identity = new FileIdentity("test-volume", "physical-file-id");
        identities.Identities[path] = identity;
        var file = fixture.Add(path);
        var folder = fixture.Add(fixture.Folder("identity-folder"));
        Equal(identity, file.FileIdentity, "Adding a physical file must capture volume identity and file ID.");
        Require(file.Id != identity.FileId && file.Id != path, "The permanent Item ID must be separate from file identity and path.");
        Require(folder.FileIdentity is null, "Folders must not capture a physical file identity.");
        Equal(1, identities.GetPaths.Count, "Only adding the file must ask for physical identity.");
        Equal(path, identities.GetPaths.Single(), "Identity capture must use the collected file path.");
        var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform, identities).GetSnapshot(checkPaths: false);
        Equal(identity, reopened.Items.Single(item => item.Id == file.Id).FileIdentity, "Physical identity must persist in SQLite.");
        Equal(0, identities.ResolveIdentities.Count, "Adding and reading must never locate renamed files.");
    }

    private static void ExternalRenameRecovery()
    {
        using var fixture = new Fixture();
        var originalPath = fixture.File("New Document.txt", "same physical file");
        var before = fixture.ContextItem(originalPath);
        Require(before.FileIdentity is not null, "Windows must capture identity for the generated local file.");
        var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform);
        var renamedPath = Path.Combine(fixture.SourceRoot, "PDMS 手册.txt");
        System.IO.File.Move(originalPath, renamedPath);
        var missing = reopened.GetSnapshot().Items.Single(item => item.Id == before.Id);
        Require(missing.IsMissing, "A snapshot must report the stale path without performing recovery.");
        Equal(originalPath, missing.Target, "Reading the library must leave the old path unchanged.");
        reopened.Open(before.Id);
        var recovered = reopened.GetSnapshot().Items.Single(item => item.Id == before.Id);
        Equal(renamedPath, recovered.Target, "Native Windows file identity must locate the externally renamed file.");
        Equal(renamedPath, fixture.Platform.LastOpenedPath, "The shell must receive the recovered path.");
        Require(!recovered.IsMissing, "Successful recovery must clear missing state.");
        SameContext(before, recovered);
        Equal(before.FileIdentity, recovered.FileIdentity, "The physical identity must remain unchanged across rename.");
        Equal(before.CreatedAt, recovered.CreatedAt, "Recovery must preserve creation time.");
        Equal(1L, recovered.OpenCount, "Successful recovery and open must count once.");
        Equal("same physical file", System.IO.File.ReadAllText(renamedPath), "Recovery must preserve physical file content.");
        var secondRestart = new LibraryService(fixture.DatabasePath, fixture.Platform).GetSnapshot().Items.Single();
        Equal(renamedPath, secondRestart.Target, "The recovered path must survive another restart.");
        Equal(before.FileIdentity, secondRestart.FileIdentity, "The native file identity must survive another restart.");
        SameContext(before, secondRestart);
        Equal(0, fixture.Platform.MoveCalls, "Recovery must never move the physical file.");
    }

    private static void ExistingPathWins()
    {
        var identities = new TestFileIdentityProvider();
        using var fixture = new Fixture(identities);
        var path = fixture.File("existing.txt", "original");
        var originalIdentity = new FileIdentity("volume-a", "original-file");
        identities.Identities[path] = originalIdentity;
        var before = fixture.ContextItem(path);
        var movedPath = Path.Combine(fixture.SourceRoot, "renamed-original.txt");
        System.IO.File.Move(path, movedPath);
        System.IO.File.WriteAllText(path, "replacement at the existing path");
        identities.Identities[path] = new FileIdentity("volume-a", "replacement-file");
        identities.Identities[movedPath] = originalIdentity;
        identities.ResolvedPaths[originalIdentity] = movedPath;
        fixture.Library.Open(before.Id);
        Equal(path, fixture.Platform.LastOpenedPath, "An existing stored path must open normally.");
        Equal(0, identities.ResolveIdentities.Count, "An existing path must not invoke recovery.");
        Equal(path, fixture.Item(before.Id).Target, "An existing path must remain the stored target.");
        SameContext(before, fixture.Item(before.Id));
    }

    private static void UnrecoverableFile()
    {
        var identities = new TestFileIdentityProvider();
        using var fixture = new Fixture(identities);
        var path = fixture.File("deleted.txt", "content");
        var identity = new FileIdentity("offline-volume", "deleted-file");
        identities.Identities[path] = identity;
        var before = fixture.ContextItem(path);
        System.IO.File.Delete(path);
        Throws(() => fixture.Library.Open(before.Id), "An unresolved file identity must report missing.");
        var after = fixture.Item(before.Id);
        Require(after.IsMissing, "An unrecovered file must remain Missing.");
        Equal(path, after.Target, "Failed recovery must retain the old path.");
        SameContext(before, after);
        Equal(before.FileIdentity, after.FileIdentity, "Failed recovery must retain physical identity for a later retry.");
        Equal(before.UpdatedAt, after.UpdatedAt, "Failed recovery must not change the metadata timestamp.");
        Equal(before.OpenCount, after.OpenCount, "Failed recovery must not advance open history.");
        Equal(before.LastOpenedAt, after.LastOpenedAt, "Failed recovery must not set last-opened time.");
        Require(fixture.Platform.LastOpenedPath is null, "An unresolved resource must not reach shell dispatch.");
        Equal(identity, identities.ResolveIdentities.Single(), "Recovery must use the stored volume and file ID.");
    }

    private static void RejectInvalidRecoveryCandidates()
    {
        foreach (var candidateKind in new[] { "wrong-file-id", "wrong-volume-id", "folder", "missing" })
        {
            var identities = new TestFileIdentityProvider();
            using var fixture = new Fixture(identities);
            var oldPath = fixture.File("original.txt", "original");
            var identity = new FileIdentity("volume-a", "original-file");
            identities.Identities[oldPath] = identity;
            var before = fixture.ContextItem(oldPath);
            var candidatePath = candidateKind == "folder" ? fixture.Folder("candidate") :
                candidateKind == "missing" ? Path.Combine(fixture.SourceRoot, "absent.txt") : fixture.File("candidate.txt", "different file");
            identities.Identities[candidatePath] = candidateKind switch
            {
                "wrong-file-id" => new FileIdentity(identity.VolumeId, "different-file"),
                "wrong-volume-id" => new FileIdentity("volume-b", identity.FileId),
                _ => identity
            };
            identities.ResolvedPaths[identity] = candidatePath;
            System.IO.File.Delete(oldPath);
            Throws(() => fixture.Library.Open(before.Id), $"Recovery must reject a {candidateKind} candidate.");
            var after = fixture.Item(before.Id);
            Equal(oldPath, after.Target, $"A {candidateKind} candidate must not replace the old target.");
            Require(after.IsMissing, $"A {candidateKind} candidate must leave the item Missing.");
            SameContext(before, after);
            Equal(before.UpdatedAt, after.UpdatedAt, "Rejected candidates must preserve timestamps.");
            Equal(0L, after.OpenCount, "Rejected candidates must not advance history.");
            Require(fixture.Platform.LastOpenedPath is null, "Rejected candidates must never reach the shell.");
        }
    }

    private static void UnavailableFileIdentity()
    {
        var identities = new TestFileIdentityProvider();
        using var fixture = new Fixture(identities);
        var path = fixture.File("unsupported-identity.txt", "content");
        var before = fixture.ContextItem(path);
        Require(before.FileIdentity is null, "Identity capture can be unavailable without blocking collection.");
        fixture.Library.Open(before.Id);
        Equal(path, fixture.Platform.LastOpenedPath, "Accessible files must open even without captured identity.");
        var opened = fixture.Item(before.Id);
        System.IO.File.Move(path, Path.Combine(fixture.SourceRoot, "renamed-without-identity.txt"));
        Throws(() => fixture.Library.Open(before.Id), "A missing file without identity cannot be guessed by filename.");
        var missing = fixture.Item(before.Id);
        Require(missing.IsMissing, "A missing file without identity must stay Missing.");
        Equal(path, missing.Target, "A missing file without identity must keep its original path.");
        Equal(opened.OpenCount, missing.OpenCount, "A failed unidentified open must not advance history.");
        Equal(0, identities.ResolveIdentities.Count, "A missing identity must not invoke the resolver.");
        SameContext(before, missing);
    }

    private static void NoFolderRecovery()
    {
        var identities = new TestFileIdentityProvider();
        using var fixture = new Fixture(identities);
        var path = fixture.Folder("original-folder");
        var before = fixture.ContextItem(path);
        Directory.Move(path, Path.Combine(fixture.SourceRoot, "renamed-folder"));
        Throws(() => fixture.Library.Open(before.Id), "An externally renamed folder must keep the existing Missing behavior.");
        var missing = fixture.Item(before.Id);
        Require(missing.IsMissing, "An externally renamed folder must be Missing until explicit repair.");
        Equal(path, missing.Target, "File recovery must not alter folder targets.");
        Require(missing.FileIdentity is null, "Folder references must not acquire file identity.");
        Equal(0, identities.GetPaths.Count, "Folder operations must not request file identity.");
        Equal(0, identities.ResolveIdentities.Count, "Folder opening must not invoke file recovery.");
        SameContext(before, missing);
    }

    private static void RepairCapturesIdentity()
    {
        var identities = new TestFileIdentityProvider();
        using var fixture = new Fixture(identities);
        var oldPath = fixture.File("old-file.txt", "old content");
        var originalIdentity = new FileIdentity("volume-a", "old-file");
        identities.Identities[oldPath] = originalIdentity;
        var before = fixture.ContextItem(oldPath);
        var selectedPath = fixture.File("selected-file.txt", "selected content");
        var selectedIdentity = new FileIdentity("volume-a", "selected-file");
        identities.Identities[selectedPath] = selectedIdentity;
        System.IO.File.Delete(oldPath);
        fixture.Library.RepairPath(before.Id, selectedPath);
        var repaired = fixture.Item(before.Id);
        Equal(selectedIdentity, repaired.FileIdentity, "Explicit repair must capture the selected physical file's identity.");
        SameContext(before, repaired);
        var renamedPath = Path.Combine(fixture.SourceRoot, "selected-renamed.txt");
        System.IO.File.Move(selectedPath, renamedPath);
        identities.Identities[renamedPath] = selectedIdentity;
        identities.ResolvedPaths[selectedIdentity] = renamedPath;
        fixture.Library.Open(before.Id);
        Equal(renamedPath, fixture.Item(before.Id).Target, "Later recovery must use the repaired file's identity.");
        Equal(selectedIdentity, identities.ResolveIdentities.Single(), "Repair must replace the obsolete physical identity.");
        Equal("selected content", System.IO.File.ReadAllText(renamedPath), "Repair and recovery must not rewrite physical content.");
        SameContext(before, fixture.Item(before.Id));
    }

    private static void RecoveryPathConflict()
    {
        var identities = new TestFileIdentityProvider();
        using var fixture = new Fixture(identities);
        var oldPath = fixture.File("collected-old.txt", "content");
        var identity = new FileIdentity("volume-a", "same-file");
        identities.Identities[oldPath] = identity;
        var before = fixture.ContextItem(oldPath);
        var newPath = Path.Combine(fixture.SourceRoot, "collected-new.txt");
        System.IO.File.Move(oldPath, newPath);
        identities.Identities[newPath] = identity;
        identities.ResolvedPaths[identity] = newPath;
        var other = fixture.ContextItem(newPath);
        Throws(() => fixture.Library.Open(before.Id), "A recovered path already registered to another Item must report a conflict.");
        Equal(2, fixture.Library.GetSnapshot().Items.Count, "Conflict must preserve both independently collected Item IDs.");
        var after = fixture.Item(before.Id);
        Equal(oldPath, after.Target, "Conflict must preserve the missing Item's old target.");
        Require(after.IsMissing, "Conflict must leave the old Item Missing.");
        SameContext(before, after);
        SameContext(other, fixture.Item(other.Id));
        Equal(newPath, fixture.Item(other.Id).Target, "Conflict must preserve the existing Item's target.");
        Equal(0L, after.OpenCount, "Conflict must not count as opening the old Item.");
        Require(fixture.Platform.LastOpenedPath is null, "Conflict must not reach shell dispatch.");
    }

    private static void LegacyIdentityBackfill()
    {
        var identities = new TestFileIdentityProvider();
        var identity = new FileIdentity("legacy-volume", "legacy-file");
        string existingPath = "", missingPath = "", folderPath = "";
        using var fixture = new Fixture(identities, setup =>
        {
            existingPath = setup.File("legacy-existing.txt", "legacy content");
            missingPath = Path.Combine(setup.SourceRoot, "legacy-missing.txt");
            folderPath = setup.Folder("legacy-folder");
            identities.Identities[existingPath] = identity;
            setup.CreateLegacyDatabase([(existingPath, "file"), (missingPath, "file"), (folderPath, "folder")]);
        });
        var uncheckedSnapshot = fixture.Library.GetSnapshot(checkPaths: false);
        Require(uncheckedSnapshot.Items.All(item => item.FileIdentity is null), "Migration must not fabricate identity from stored paths.");
        Equal(0, identities.GetPaths.Count, "Unchecked snapshots must not inspect physical files.");
        var before = uncheckedSnapshot.Items.Single(item => item.Target == existingPath);
        var checkedSnapshot = fixture.Library.GetSnapshot();
        var backedUp = checkedSnapshot.Items.Single(item => item.Id == before.Id);
        Equal(identity, backedUp.FileIdentity, "An accessible legacy file must be backfilled during explicit path checking.");
        SameContext(before, backedUp);
        Equal(before.CreatedAt, backedUp.CreatedAt, "Identity backfill must preserve creation time.");
        Equal(before.UpdatedAt, backedUp.UpdatedAt, "Identity backfill must preserve metadata update time.");
        Equal(before.OpenCount, backedUp.OpenCount, "Identity backfill must preserve open count.");
        Equal(before.LastOpenedAt, backedUp.LastOpenedAt, "Identity backfill must preserve last-opened time.");
        var missing = checkedSnapshot.Items.Single(item => item.Target == missingPath);
        Require(missing.IsMissing && missing.FileIdentity is null, "An already missing legacy file must retain no identity and stay Missing.");
        Require(checkedSnapshot.Items.Single(item => item.Target == folderPath).FileIdentity is null, "Legacy folders must not receive physical file identity.");
        Equal(existingPath, identities.GetPaths.Single(), "Backfill must inspect only the accessible legacy file.");
        Throws(() => fixture.Library.Open(missing.Id), "An unidentified missing legacy file must require explicit repair.");
        Equal(0, identities.ResolveIdentities.Count, "Missing legacy files must not invoke identity recovery.");
        Equal(4L, Convert.ToInt64(fixture.SqlScalar("PRAGMA user_version;")), "The database must migrate to schema version four.");
        Require(checkedSnapshot.Items.All(item => item.Favicon is null), "Migration must leave physical resources without website icons.");
        var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform, identities).GetSnapshot(checkPaths: false);
        Equal(identity, reopened.Items.Single(item => item.Id == before.Id).FileIdentity, "Legacy backfill must persist across restart.");
        var renamedPath = Path.Combine(fixture.SourceRoot, "legacy-renamed.txt");
        System.IO.File.Move(existingPath, renamedPath);
        identities.Identities[renamedPath] = identity;
        identities.ResolvedPaths[identity] = renamedPath;
        fixture.Library.Open(before.Id);
        Equal(renamedPath, fixture.Item(before.Id).Target, "A backfilled legacy file must recover a later rename.");
        SameContext(before, fixture.Item(before.Id));
    }

    private static void RecoveryDatabaseFailure()
    {
        var identities = new TestFileIdentityProvider();
        using var fixture = new Fixture(identities);
        var oldPath = fixture.File("recovery-db-original.txt", "content");
        var identity = new FileIdentity("volume-a", "db-file");
        identities.Identities[oldPath] = identity;
        var before = fixture.ContextItem(oldPath);
        var newPath = Path.Combine(fixture.SourceRoot, "recovery-db-renamed.txt");
        System.IO.File.Move(oldPath, newPath);
        identities.Identities[newPath] = identity;
        identities.ResolvedPaths[identity] = newPath;
        fixture.ExecuteSql("""
            CREATE TRIGGER reject_recovery_target BEFORE UPDATE OF target ON Item
            BEGIN SELECT RAISE(ABORT, 'Injected recovery path write failure'); END;
            """);
        Throws(() => fixture.Library.Open(before.Id), "A recovery database failure must be reported before shell launch.");
        var after = fixture.Item(before.Id);
        Equal(oldPath, after.Target, "A failed recovery write must preserve the original target.");
        Require(after.IsMissing, "A failed recovery write must leave the old reference Missing.");
        Equal(before.UpdatedAt, after.UpdatedAt, "A failed recovery write must preserve update time.");
        Equal(before.FileIdentity, after.FileIdentity, "A failed recovery write must retain physical identity.");
        SameContext(before, after);
        Require(fixture.Platform.LastOpenedPath is null, "A failed recovery write must not reach shell dispatch.");
        Require(System.IO.File.Exists(newPath), "Recovery failure must not roll back the user's physical rename.");
        Equal(0, fixture.Platform.MoveCalls, "Recovery write failures must not perform physical moves.");
        fixture.ExecuteSql("DROP TRIGGER reject_recovery_target;");
        fixture.Library.Open(before.Id);
        Equal(newPath, fixture.Item(before.Id).Target, "Recovery must remain retryable after database failure.");
    }

    private static void RecoveryShellFailure()
    {
        var identities = new TestFileIdentityProvider();
        using var fixture = new Fixture(identities);
        var oldPath = fixture.File("recovery-shell-original.txt", "content");
        var identity = new FileIdentity("volume-a", "shell-file");
        identities.Identities[oldPath] = identity;
        var before = fixture.ContextItem(oldPath);
        var newPath = Path.Combine(fixture.SourceRoot, "recovery-shell-renamed.txt");
        System.IO.File.Move(oldPath, newPath);
        identities.Identities[newPath] = identity;
        identities.ResolvedPaths[identity] = newPath;
        fixture.Platform.FailOpen = true;
        Throws(() => fixture.Library.Open(before.Id), "A shell failure after recovery must still be reported.");
        var after = fixture.Item(before.Id);
        Equal(newPath, after.Target, "The verified recovered path must stay committed when shell launch fails.");
        Require(!after.IsMissing, "A shell launch failure must not mark the recovered existing file Missing.");
        SameContext(before, after);
        Equal(before.UpdatedAt, after.UpdatedAt, "Path recovery alone must preserve metadata update time.");
        Equal(before.CreatedAt, after.CreatedAt, "Path recovery must preserve creation time.");
        Equal(before.OpenCount, after.OpenCount, "Failed shell launch after recovery must not advance open count.");
        Equal(before.LastOpenedAt, after.LastOpenedAt, "Failed shell launch after recovery must preserve last-opened time.");
        fixture.Platform.FailOpen = false;
        fixture.Library.Open(before.Id);
        Equal(1, identities.ResolveIdentities.Count, "Opening the committed recovered path must not resolve again.");
        Equal(1L, fixture.Item(before.Id).OpenCount, "A later successful shell dispatch must count once.");
    }

    private static void IdentityAccessFailure()
    {
        var identities = new TestFileIdentityProvider { FailCapture = true };
        using var fixture = new Fixture(identities);
        var path = fixture.File("capture-denied.txt", "content");
        var before = fixture.Add(path);
        Require(before.FileIdentity is null, "Expected identity access failure must not prevent file collection.");
        fixture.Library.Open(before.Id);
        Equal(path, fixture.Platform.LastOpenedPath, "Identity access failure must not prevent opening an existing file.");
        identities.FailCapture = false;
        var identity = new FileIdentity("temporarily-offline-volume", "accessible-file");
        identities.Identities[path] = identity;
        var backedUp = fixture.Item(before.Id);
        Equal(identity, backedUp.FileIdentity, "A later path check must backfill identity when access is restored.");
        System.IO.File.Move(path, Path.Combine(fixture.SourceRoot, "capture-denied-renamed.txt"));
        identities.FailResolve = true;
        Throws(() => fixture.Library.Open(before.Id), "An expected identity resolver access failure must report Missing.");
        var missing = fixture.Item(before.Id);
        Require(missing.IsMissing, "Unavailable volume access must leave the item Missing.");
        Equal(path, missing.Target, "Unavailable volume access must preserve the stored path.");
        Equal(backedUp.OpenCount, missing.OpenCount, "Unavailable volume access must not count as opening the file.");
        Equal(backedUp.FileIdentity, missing.FileIdentity, "Unavailable volume access must retain identity for a future retry.");
        SameContext(before, missing);
    }

    private static byte[] UrlIcon() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+kDZkAAAAASUVORK5CYII=");

    private static void UrlExactDeduplication()
    {
        using var fixture = new Fixture();
        const string target = "https://Example.com:443/Articles?q=One#Part";
        var first = fixture.Library.AddUrl("  " + target + "  ", "", "", "", []);
        Require(first.Added && first.Item.IsUrl, "URLs must be first-class resources.");
        Equal(target, first.Item.Target, "The exact URL spelling must survive surrounding whitespace trimming.");
        Equal("example.com", first.Item.RealName, "The fallback name must use the website host rather than a file name.");
        var duplicate = fixture.Library.AddUrl(target, "ignored", "ignored", "ignored", []);
        Require(!duplicate.Added, "Only the exact same URL must reuse the existing item.");
        Equal(first.Item.Id, duplicate.Item.Id, "Exact URL duplicates must retain one stable ID.");
        string[] distinct = [
            "https://example.com:443/Articles?q=One#Part", "https://Example.com/Articles?q=One#Part",
            "http://Example.com:443/Articles?q=One#Part", "https://Example.com:443/articles?q=One#Part",
            "https://Example.com:443/Articles?q=one#Part", "https://Example.com:443/Articles?q=One#part",
            "https://Example.com:443/Articles?q=One", "HTTPS://Example.com:443/Articles?q=One#Part"
        ];
        foreach (var url in distinct)
        {
            var result = fixture.Library.AddUrl(url, "", "", "", []);
            Require(result.Added, $"Different exact spelling '{url}' must remain distinct.");
            Equal(url, result.Item.Target, "Saving a URL must not rewrite its target.");
            Equal("URL:" + url, ResourceUrls.Key(url), "URL keys must preserve the exact input.");
        }
        Equal(distinct.Length + 1, fixture.Library.GetSnapshot().Items.Count, "All exact-distinct URLs must coexist in the common Item table.");
    }

    private static void UrlDuplicateContext()
    {
        using var fixture = new Fixture();
        var firstProject = fixture.Library.CreateProject("URL project Alpha");
        var secondProject = fixture.Library.CreateProject("URL project Beta");
        var icon = UrlIcon();
        const string target = "https://example.test/manual";
        var before = fixture.Library.AddUrl(target, "Manual title", "Manual description", "Manual note", [firstProject.Id], icon).Item;
        fixture.Library.Open(before.Id);
        before = fixture.Item(before.Id);
        var duplicate = fixture.Library.AddUrl(target, "Fetched title", "Fetched description", "Fetched note", [secondProject.Id, secondProject.Id]);
        Require(!duplicate.Added, "Adding a known URL must report reuse.");
        Equal(before.Id, duplicate.Item.Id, "The known URL must retain its original ID.");
        Equal(before.Alias, duplicate.Item.Alias, "Duplicate collection must preserve a manually chosen title.");
        Equal(before.Description, duplicate.Item.Description, "Duplicate collection must preserve a manually chosen description.");
        Equal(before.Note, duplicate.Item.Note, "Duplicate collection must preserve the original note.");
        Require(icon.SequenceEqual(duplicate.Item.Favicon!), "Duplicate collection must preserve the stored favicon.");
        Equal(before.CreatedAt, duplicate.Item.CreatedAt, "Duplicate collection must preserve creation time.");
        Equal(before.OpenCount, duplicate.Item.OpenCount, "Duplicate collection must preserve open history.");
        SetEqual([firstProject.Id, secondProject.Id], duplicate.Item.Projects.Select(project => project.Id), "URLs must belong to multiple projects.");
        var unchanged = fixture.Library.AddUrl(target, "", "", "", [firstProject.Id, secondProject.Id]).Item;
        Equal(duplicate.Item.UpdatedAt, unchanged.UpdatedAt, "A duplicate with no new memberships must not touch metadata time.");
        Throws(() => fixture.Library.AddUrl(target, "", "", "", ["missing-project"]), "Invalid membership must be rejected even for a duplicate URL.");
        SameContext(unchanged, fixture.Item(before.Id));
        Equal(1, fixture.Library.GetSnapshot().Items.Count, "Duplicate collection must not create another resource.");
    }

    private static void UrlEditRollback()
    {
        using var fixture = new Fixture();
        var project = fixture.Library.CreateProject("URL editing context");
        var before = fixture.Library.AddUrl("https://example.test/one", "Original alias", "Original description", "Original note", [project.Id], UrlIcon()).Item;
        var other = fixture.Library.AddUrl("https://example.test/two", "Other", "", "", []).Item;
        Throws(() => fixture.Library.UpdateUrl(before.Id, other.Target, "Changed", "Changed", "Changed", []), "An edit collision must report the duplicate.");
        var after = fixture.Item(before.Id);
        SameContext(before, after);
        Equal(before.Target, after.Target, "A collision must preserve the original target.");
        Equal(before.UpdatedAt, after.UpdatedAt, "A collision must preserve metadata time.");
        Require(before.Favicon!.SequenceEqual(after.Favicon!), "A collision must preserve the favicon.");
        Throws(() => fixture.Library.UpdateUrl(before.Id, "https://example.test/new", "Changed", "Changed", "Changed", ["missing-project"]),
            "Invalid projects must prevent target and metadata edits.");
        Equal(before.Target, fixture.Item(before.Id).Target, "Project validation must occur before writing a new target.");
        fixture.ExecuteSql("""
            CREATE TRIGGER reject_url_membership BEFORE INSERT ON ProjectItem
            BEGIN SELECT RAISE(ABORT, 'Injected URL membership write failure'); END;
            """);
        Throws(() => fixture.Library.UpdateUrl(before.Id, "https://example.test/new", "Changed", "Changed", "Changed", [project.Id]),
            "A membership write failure must roll back URL and context changes.");
        after = fixture.Item(before.Id);
        SameContext(before, after);
        Equal(before.Target, after.Target, "Membership write failure must restore the old URL.");
        Equal(before.UpdatedAt, after.UpdatedAt, "Membership write failure must restore update time.");
        Require(before.Favicon!.SequenceEqual(after.Favicon!), "Membership write failure must restore the icon.");
        fixture.ExecuteSql("DROP TRIGGER reject_url_membership;");
        fixture.Library.UpdateUrl(before.Id, "https://example.test/new", "Changed", "Changed description", "Changed note", []);
        after = fixture.Item(before.Id);
        Equal("https://example.test/new", after.Target, "A successful edit must replace the URL.");
        Equal("Changed", after.Alias, "A successful edit must save manual context.");
        Equal(0, after.Projects.Count, "A successful edit must save the selected memberships.");
        Require(after.Favicon is null, "Changing the URL without a replacement favicon must drop the previous website's icon.");
        Equal(other.Id, fixture.Item(other.Id).Id, "URL edits must preserve other resources.");
    }

    private static void UrlPersistence()
    {
        using var fixture = new Fixture();
        var project = fixture.Library.CreateProject("Bookmark ProjectNeedle");
        var sourceIcon = UrlIcon();
        var item = fixture.Library.AddUrl("https://example.test/UrlNeedle?q=value#section", "AliasNeedle", "DescriptionNeedle", "NoteNeedle", [project.Id], sourceIcon).Item;
        sourceIcon[0] = 0;
        Require(item.Favicon![0] == 137, "The stored favicon must not share mutable input bytes.");
        fixture.Library.UpdateItem(item.Id, "Manual AliasNeedle", "Manual DescriptionNeedle", "Manual NoteNeedle", [project.Id]);
        fixture.Library.UpdateUrl(item.Id, item.Target, "Manual AliasNeedle", "Manual DescriptionNeedle", "Manual NoteNeedle", [project.Id]);
        fixture.Library.Open(item.Id);
        var before = fixture.Item(item.Id);
        Require(UrlIcon().SequenceEqual(before.Favicon!), "Common metadata edits and same-URL edits must preserve the icon.");
        var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform).GetSnapshot();
        var after = reopened.Items.Single();
        SameContext(before, after);
        Equal(before.Target, after.Target, "URLs must persist exactly across restart.");
        Equal(before.CreatedAt, after.CreatedAt, "Creation time must persist.");
        Equal(before.UpdatedAt, after.UpdatedAt, "Update time must persist.");
        Equal(before.LastOpenedAt, after.LastOpenedAt, "URL open time must persist.");
        Equal(before.OpenCount, after.OpenCount, "URL open count must persist.");
        Require(before.Favicon!.SequenceEqual(after.Favicon!), "Favicon bytes must persist in the same SQLite database.");
        Require(!after.IsMissing && after.FileIdentity is null, "URLs must never gain missing-file state or a physical identity.");
        var search = new LocalTextSearchProvider();
        foreach (var term in new[] { "AliasNeedle", "DescriptionNeedle", "NoteNeedle", "UrlNeedle", "q=value", "#section", "ProjectNeedle" })
            Equal(item.Id, search.Search(reopened.Items, term).Single().Id, "All shared searchable fields must include URL resources.");
    }

    private static void UrlOpenHistory()
    {
        var identities = new TestFileIdentityProvider();
        using var fixture = new Fixture(identities);
        const string target = "https://Example.test:443/Start?x=1#part";
        var before = fixture.Library.AddUrl(target, "", "", "", []).Item;
        fixture.Library.GetSnapshot();
        Equal(0, fixture.Platform.FileCheckPaths.Count, "URL snapshots must not check file existence.");
        Equal(0, fixture.Platform.DirectoryCheckPaths.Count, "URL snapshots must not check directory existence.");
        fixture.Platform.FailOpen = true;
        Throws(() => fixture.Library.Open(before.Id), "Browser dispatch failures must be reported.");
        var failed = fixture.Item(before.Id);
        Equal(0L, failed.OpenCount, "Failed browser dispatch must not count an open.");
        Equal(before.LastOpenedAt, failed.LastOpenedAt, "Failed dispatch must not change the last-opened time.");
        Equal(before.UpdatedAt, failed.UpdatedAt, "Failed dispatch must not change metadata time.");
        fixture.Platform.FailOpen = false;
        fixture.Library.Open(before.Id);
        var opened = fixture.Item(before.Id);
        Equal(target, fixture.Platform.LastOpenedPath, "The browser must receive the exact stored URL.");
        Equal(ResourceUrls.Type, fixture.Platform.LastOpenedType, "Browser dispatch must use the URL resource type.");
        Equal(1L, opened.OpenCount, "A successful dispatch must count exactly once.");
        Require(opened.LastOpenedAt != null && !opened.IsMissing, "A successful URL open must record time without missing-file state.");
        Equal(0, identities.GetPaths.Count, "Opening URLs must never capture file identity.");
        Equal(0, identities.ResolveIdentities.Count, "Opening URLs must never recover file identity.");
        Equal(0, fixture.Platform.FileCheckPaths.Count, "URL opening must not inspect physical files.");
        Equal(0, fixture.Platform.DirectoryCheckPaths.Count, "URL opening must not inspect physical directories.");
    }

    private static void UrlPhysicalSafety()
    {
        using var fixture = new Fixture();
        var folder = fixture.Add(fixture.Folder("URL coexistence"));
        var file = fixture.Add(fixture.File("URL coexistence", "child.txt", "original content"));
        var before = fixture.Library.AddUrl("https://example.test/URL%20coexistence/child.txt", "Website", "Description", "Note", [], UrlIcon()).Item;
        Throws(() => fixture.Library.OpenLocation(before.Id), "URLs have no physical explorer location.");
        Throws(() => fixture.Library.RenamePhysical(before.Id, "renamed.txt"), "Physical rename must reject URLs.");
        Throws(() => fixture.Library.RepairPath(before.Id, file.Target), "File-path repair must reject URLs.");
        Throws(() => fixture.Library.UpdateUrl(file.Id, before.Target, "", "", "", []), "URL editing must reject physical resources.");
        Equal(0, fixture.Platform.MoveCalls, "Rejected URL physical operations must never move a resource.");
        Require(fixture.Platform.LastLocationPath is null, "Rejected URL location operations must not call explorer.");
        fixture.Library.RenamePhysical(folder.Id, "URL renamed folder");
        var renamedFolder = fixture.Item(folder.Id);
        Equal(Path.Combine(renamedFolder.Target, "child.txt"), fixture.Item(file.Id).Target, "Folder rename must still rebase registered physical descendants.");
        var after = fixture.Item(before.Id);
        SameContext(before, after);
        Equal(before.Target, after.Target, "Folder rename must leave URL targets untouched.");
        Equal(before.UpdatedAt, after.UpdatedAt, "Folder rename must leave URL metadata times untouched.");
        Require(before.Favicon!.SequenceEqual(after.Favicon!), "Folder rename must preserve URL favicons.");
        var repairFolder = fixture.Folder("URL repair destination");
        fixture.File("URL repair destination", "child.txt", "replacement content");
        fixture.Library.RepairPath(folder.Id, repairFolder);
        Equal(Path.Combine(repairFolder, "child.txt"), fixture.Item(file.Id).Target, "Folder repair must still rebase physical descendants.");
        Equal(before.Target, fixture.Item(before.Id).Target, "Folder repair must preserve URL targets.");
    }

    private static void UrlMixedBulk()
    {
        using var fixture = new Fixture();
        var firstProject = fixture.Library.CreateProject("Mixed Alpha");
        var secondProject = fixture.Library.CreateProject("Mixed Beta");
        var file = fixture.Add(fixture.File("mixed.txt", "must remain"));
        var url = fixture.Library.AddUrl("https://example.test/mixed", "URL", "", "", [firstProject.Id, secondProject.Id]).Item;
        var unrelated = fixture.Library.AddUrl("https://example.test/unrelated", "Other URL", "", "", [secondProject.Id]).Item;
        fixture.Library.AddToProject(file.Id, firstProject.Id);
        fixture.Library.AddToProject(file.Id, secondProject.Id);
        fixture.Library.RemoveItemsFromProject([url.Id, file.Id], firstProject.Id);
        SetEqual([secondProject.Id], fixture.Item(url.Id).Projects.Select(project => project.Id), "Mixed project removal must preserve the URL's other membership.");
        SetEqual([secondProject.Id], fixture.Item(file.Id).Projects.Select(project => project.Id), "Mixed project removal must preserve the file's other membership.");
        fixture.Library.AddToProject(url.Id, firstProject.Id);
        fixture.Library.DeleteProject(firstProject.Id);
        Equal(url.Target, fixture.Item(url.Id).Target, "Deleting a project must preserve URL records.");
        Throws(() => fixture.Library.RemoveItemsFromLibrary([url.Id, file.Id, "unknown-item"]), "Invalid mixed selection must roll back all removals.");
        Equal(3, fixture.Library.GetSnapshot().Items.Count, "Invalid mixed removal must preserve all resources.");
        fixture.Library.RemoveItemsFromLibrary([url.Id, file.Id, url.Id]);
        Equal(unrelated.Id, fixture.Library.GetSnapshot().Items.Single().Id, "Mixed logical removal must retain unrelated URL resources.");
        Equal("must remain", System.IO.File.ReadAllText(file.Target), "Mixed removal must leave physical files untouched.");
        Equal(0, fixture.Platform.MoveCalls, "Logical URL and file removal must not perform physical moves.");
    }

    private static void UrlVersionTwoMigration()
    {
        var identities = new TestFileIdentityProvider();
        string file = "", folder = "";
        using var fixture = new Fixture(identities, setup =>
        {
            file = setup.File("v2-file.txt", "v2 content");
            folder = setup.Folder("v2-folder");
            setup.CreateLegacyDatabase([(file, "file"), (folder, "folder")]);
            setup.ExecuteSql("""
                ALTER TABLE Item ADD COLUMN volume_id TEXT;
                ALTER TABLE Item ADD COLUMN file_id TEXT;
                UPDATE Item SET volume_id='preserved-volume',file_id='preserved-file' WHERE type='file';
                PRAGMA user_version=2;
                """);
        });
        var migrated = fixture.Library.GetSnapshot(checkPaths: false);
        Equal(4L, Convert.ToInt64(fixture.SqlScalar("PRAGMA user_version;")), "Version-two databases must migrate to version four.");
        Equal(2, migrated.Items.Count, "Migration must preserve all file and folder records.");
        var physicalFile = migrated.Items.Single(item => item.Type == "file");
        var physicalFolder = migrated.Items.Single(item => item.Type == "folder");
        Equal(file, physicalFile.Target, "Migration must preserve file paths.");
        Equal(folder, physicalFolder.Target, "Migration must preserve folder paths.");
        Equal(new FileIdentity("preserved-volume", "preserved-file"), physicalFile.FileIdentity, "Migration must preserve captured physical identity.");
        Require(physicalFolder.FileIdentity is null, "Migration must preserve path-only folder behavior.");
        foreach (var item in migrated.Items)
        {
            Equal("Legacy alias", item.Alias, "Migration must preserve aliases.");
            Equal("Legacy description", item.Description, "Migration must preserve descriptions.");
            Equal("Legacy note", item.Note, "Migration must preserve notes.");
            Equal(7L, item.OpenCount, "Migration must preserve open count.");
            Equal(DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"), item.CreatedAt, "Migration must preserve creation time.");
            Equal(DateTimeOffset.Parse("2026-02-03T04:05:06+00:00"), item.UpdatedAt, "Migration must preserve update time.");
            Equal(DateTimeOffset.Parse("2026-02-02T03:04:05+00:00"), item.LastOpenedAt, "Migration must preserve last-opened time.");
            Equal("legacy-project", item.Projects.Single().Id, "Migration must preserve project membership.");
            Require(item.Favicon is null, "Migration must not assign website icons to physical resources.");
        }
        Equal(0, identities.GetPaths.Count, "Migration must not inspect or recapture physical identity.");
        fixture.Library.AddUrl("https://example.test/migrated", "New URL", "", "", ["legacy-project"]);
        Equal(3, fixture.Library.GetSnapshot(checkPaths: false).Items.Count, "Migrated databases must support URLs alongside existing resources.");
        fixture.ExecuteSql("PRAGMA user_version=5;");
        Throws(() => new LibraryService(fixture.DatabasePath, fixture.Platform), "A future database schema must be rejected without downgrade.");
        Equal(5L, Convert.ToInt64(fixture.SqlScalar("PRAGMA user_version;")), "Rejecting a future schema must not change its version.");
        fixture.ExecuteSql("PRAGMA user_version=4;");
    }

    private static void UrlInputValidation()
    {
        using var fixture = new Fixture();
        foreach (var invalid in new[] { "", "   ", "example.test", "/relative", "ftp://example.test", "file:///C:/test.txt", "javascript:alert(1)",
                     "https://user:password@example.test/path", "https://example.test/a\nb", "https://example.test/a\0b", "https:///", "http://" })
            Throws(() => fixture.Library.AddUrl(invalid, "", "", "", []), $"Invalid URL '{invalid.Replace("\n", "\\n").Replace("\0", "\\0")}' must be rejected.");
        Equal(0, fixture.Library.GetSnapshot().Items.Count, "Rejected URLs must not create resources.");
        var invalidIcons = new[] { Array.Empty<byte>(), "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), new byte[ResourceUrls.MaxFaviconBytes + 1] };
        foreach (var icon in invalidIcons)
        {
            Require(!ResourceUrls.IsSupportedFavicon(icon), "Invalid or oversized favicon content must be rejected.");
            Throws(() => fixture.Library.AddUrl("https://example.test/icon", "", "", "", [], icon), "Invalid icons must prevent partial insert.");
        }
        Throws(() => fixture.Library.AddUrl("https://example.test/membership", "", "", "", ["missing-project"], UrlIcon()),
            "Missing projects must prevent a partial URL insert.");
        Equal(0, fixture.Library.GetSnapshot().Items.Count, "Invalid icons or projects must not leave partial records.");
        var before = fixture.Library.AddUrl("https://example.test/valid", "Manual", "Manual description", "Manual note", [], UrlIcon()).Item;
        Throws(() => fixture.Library.UpdateUrl(before.Id, "ftp://example.test", "Changed", "Changed", "Changed", []), "Invalid edits must not affect saved context.");
        Throws(() => fixture.Library.UpdateUrl(before.Id, "https://example.test/changed", "Changed", "Changed", "Changed", [], invalidIcons[1]),
            "Invalid replacement icons must not partly update the target.");
        var after = fixture.Item(before.Id);
        SameContext(before, after);
        Equal(before.Target, after.Target, "Rejected edits must retain the URL.");
        Require(before.Favicon!.SequenceEqual(after.Favicon!), "Rejected edits must retain the saved icon.");
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

        public Fixture(IFileIdentityProvider? fileIdentityProvider = null, Action<Fixture>? beforeInitialize = null)
        {
            SourceRoot = Path.Combine(root, "resources");
            DatabasePath = Path.Combine(root, "state", "library.db");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            beforeInitialize?.Invoke(this);
            Library = new LibraryService(DatabasePath, Platform, fileIdentityProvider);
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

        public object? SqlScalar(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public void CreateLegacyDatabase(IEnumerable<(string Path, string Type)> targets)
        {
            ExecuteSql("""
                CREATE TABLE Item (
                    id TEXT PRIMARY KEY, type TEXT NOT NULL, target TEXT NOT NULL, path_key TEXT NOT NULL UNIQUE,
                    alias TEXT NOT NULL DEFAULT '', description TEXT NOT NULL DEFAULT '', note TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL, last_opened_at TEXT,
                    open_count INTEGER NOT NULL DEFAULT 0 CHECK (open_count >= 0)
                );
                CREATE TABLE Project (id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE, description TEXT NOT NULL DEFAULT '');
                CREATE TABLE ProjectItem (
                    project_id TEXT NOT NULL REFERENCES Project(id) ON DELETE CASCADE,
                    item_id TEXT NOT NULL REFERENCES Item(id) ON DELETE CASCADE,
                    PRIMARY KEY (project_id, item_id)
                );
                CREATE INDEX ix_ProjectItem_item ON ProjectItem(item_id);
                INSERT INTO Project(id,name,name_key,description) VALUES('legacy-project','Legacy project','LEGACY PROJECT','Legacy project context');
                PRAGMA user_version = 1;
                """);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
            connection.Open();
            var index = 0;
            foreach (var (path, type) in targets)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Item(id,type,target,path_key,alias,description,note,created_at,updated_at,last_opened_at,open_count)
                    VALUES($id,$type,$target,$key,'Legacy alias','Legacy description','Legacy note',
                        '2026-01-02T03:04:05.0000000+00:00','2026-02-03T04:05:06.0000000+00:00','2026-02-02T03:04:05.0000000+00:00',7);
                    INSERT INTO ProjectItem(project_id,item_id) VALUES('legacy-project',$id);
                    """;
                command.Parameters.AddWithValue("$id", "legacy-item-" + index++);
                command.Parameters.AddWithValue("$type", type);
                command.Parameters.AddWithValue("$target", path);
                command.Parameters.AddWithValue("$key", path.ToUpperInvariant());
                command.ExecuteNonQuery();
            }
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

    private sealed class TestFileIdentityProvider : IFileIdentityProvider
    {
        public Dictionary<string, FileIdentity> Identities { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<FileIdentity, string> ResolvedPaths { get; } = [];
        public List<string> GetPaths { get; } = [];
        public List<FileIdentity> ResolveIdentities { get; } = [];
        public bool FailCapture { get; set; }
        public bool FailResolve { get; set; }

        public FileIdentity? GetIdentity(string path)
        {
            GetPaths.Add(path);
            if (FailCapture) throw new UnauthorizedAccessException("Simulated file identity access failure.");
            return Identities.GetValueOrDefault(path);
        }

        public string? ResolvePath(FileIdentity identity)
        {
            ResolveIdentities.Add(identity);
            if (FailResolve) throw new IOException("Simulated unavailable volume.");
            return ResolvedPaths.GetValueOrDefault(identity);
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
        public List<string> FileCheckPaths { get; } = [];
        public List<string> DirectoryCheckPaths { get; } = [];

        public bool FileExists(string path)
        {
            FileCheckPaths.Add(path);
            return System.IO.File.Exists(path);
        }
        public bool DirectoryExists(string path)
        {
            DirectoryCheckPaths.Add(path);
            return Directory.Exists(path);
        }

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
