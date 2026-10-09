using System.Globalization;
using LocalResourceLibrary.Core;

namespace LocalResourceLibrary.Checks;

internal static partial class Program
{
    private static void ProjectGroupLifecycle()
    {
        using var fixture = new Fixture();
        var library = fixture.Library;
        var work = library.CreateProjectGroup(" 工作 ");
        var reference = library.CreateProjectGroup("资料");
        Equal("工作", work.Name, "Group names trim surrounding whitespace.");
        Require(Guid.TryParseExact(work.Id, "N", out _), "New groups receive UUIDs.");
        var first = library.CreateProject("项目文件", "Existing description", "blue", work.Id);
        var pinned = library.CreateProject("GitHub", "", "purple", work.Id);
        var ungrouped = library.CreateProject("Unsorted");
        var unrelated = library.CreateProject("参考", groupId: reference.Id);
        var path = fixture.File("group-resource.txt", "keep original");
        var item = library.AddResource("file", path, "Alias", "Description", "Note", [first.Id, pinned.Id, unrelated.Id]).Item;
        library.SetProjectPinned(pinned.Id, true);
        var renamed = library.PatchProjectGroup(work.Id, "工程");
        Equal(work.Id, renamed.Id, "Group renaming preserves permanent identity.");
        Equal(work.CreatedAt, renamed.CreatedAt, "Group renaming preserves creation time.");
        Equal(work.SortOrder, renamed.SortOrder, "Group renaming preserves ordering.");
        Equal(work.Id, library.GetProject(first.Id).GroupId, "Renaming keeps project membership.");
        Throws(() => library.CreateProjectGroup(" 工程 "), "Duplicate normalized group names must be rejected.");
        var before = library.GetResource(item.Id, false);
        var pinnedOrder = library.GetProject(pinned.Id).SortOrder;
        library.DeleteProjectGroup(work.Id);
        Equal(1, library.GetSnapshot(false).ProjectGroups!.Count, "Deleting one group preserves other groups.");
        Equal(4, library.GetSnapshot(false).Projects.Count, "Deleting a group keeps every project.");
        Require(library.GetProject(first.Id).GroupId is null && library.GetProject(pinned.Id).GroupId is null,
            "Deleted-group projects must become ungrouped.");
        Require(library.GetProject(pinned.Id).IsPinned, "Group deletion preserves pin status.");
        Equal(pinnedOrder, library.GetProject(pinned.Id).SortOrder, "Group deletion preserves global pinned position.");
        Equal("Unsorted,项目文件", ProjectNames(library, false, null), "Deleted-group projects append after existing ungrouped projects.");
        Equal(reference.Id, library.GetProject(unrelated.Id).GroupId, "Other group memberships stay unchanged.");
        var after = library.GetResource(item.Id, false);
        SameContext(before, after);
        Equal(before.UpdatedAt, after.UpdatedAt, "Moving projects must not rewrite resource metadata.");
        Equal("keep original", System.IO.File.ReadAllText(path), "Group deletion must preserve physical resources.");
        var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform);
        Equal(first.Id, reopened.GetProject(first.Id).Id, "Identity remains addressable after restart.");
        Equal("blue", reopened.GetProject(first.Id).Color, "Colors survive group deletion and restart.");
        Equal(reference.Id, reopened.GetProject(unrelated.Id).GroupId, "Group relationships persist after restart.");
        Throws(() => reopened.GetProjectGroup(work.Id), "Deleted group IDs must no longer resolve.");
        _ = ungrouped;
    }

    private static void ProjectColorsAndIdentity()
    {
        using var fixture = new Fixture();
        var library = fixture.Library;
        var group = library.CreateProjectGroup("Color group");
        var project = library.CreateProject("BeforeName", "Keep description");
        Require(Guid.TryParseExact(project.Id, "N", out _), "New projects receive UUIDs.");
        var item = library.AddResource("url", "https://example.test/project-color", projectIds: [project.Id]).Item;
        var updated = library.PatchProject(project.Id, name: "AfterName", color: " TEAL ", groupId: group.Id);
        Equal(project.Id, updated.Id, "Name, color and group patches preserve ID.");
        Equal("teal", updated.Color, "Color keys are normalized.");
        Equal(group.Id, updated.GroupId, "One group ID stores the project membership.");
        Equal(project.Description, updated.Description, "Omitted description is preserved.");
        Equal(updated, library.GetResource(item.Id, false).Projects.Single(), "Resource joins expose current project metadata.");
        Require(new LocalTextSearchProvider().Search([library.GetResource(item.Id, false)], "AfterName").Any(), "Project rename remains searchable through resource memberships.");
        Throws(() => library.PatchProject(project.Id, name: "Should roll back", color: "cyan", groupId: "missing-group"),
            "Missing groups reject the entire patch.");
        Equal(updated, library.GetProject(project.Id), "Invalid grouping must leave metadata and ordering untouched.");
        Throws(() => library.PatchProject(project.Id, name: "Should not persist", color: "#FFFFFF"), "Arbitrary colors must be rejected.");
        Throws(() => library.CreateProject("Invalid group project", groupId: "missing"), "Invalid groups must not create projects.");
        Throws(() => library.PatchProject(project.Id, groupId: group.Id, clearGroup: true), "Conflicting group operations must be rejected.");
        Equal(1, library.GetSnapshot(false).Projects.Count, "Invalid creations must not persist.");
        Throws(() => fixture.ExecuteSql($"UPDATE Project SET id='changed-project-id' WHERE id='{project.Id}';"), "Database project identity is read-only.");
        Throws(() => fixture.ExecuteSql($"UPDATE ProjectGroup SET id='changed-group-id' WHERE id='{group.Id}';"), "Database group identity is read-only.");
        Throws(() => fixture.ExecuteSql($"UPDATE Project SET id=NULL WHERE id='{project.Id}';"), "Project IDs cannot be cleared.");
        Throws(() => fixture.ExecuteSql($"UPDATE ProjectGroup SET id=NULL WHERE id='{group.Id}';"), "Group IDs cannot be cleared.");
        Throws(() => fixture.ExecuteSql($"UPDATE Project SET color='unknown' WHERE id='{project.Id}';"), "Database validates stored preset keys.");
        Throws(() => fixture.ExecuteSql($"PRAGMA foreign_keys=ON; UPDATE Project SET group_id='missing-group' WHERE id='{project.Id}';"), "Database enforces group foreign keys.");
        library.PatchProject(project.Id, color: "rose");
        var renamed = library.PatchProject(project.Id, name: "Independent rename", expected: updated);
        Equal("rose", renamed.Color, "Expected name patches preserve concurrent color edits.");
        Throws(() => library.PatchProject(project.Id, expected: updated, color: "blue"), "Stale expected colors must be rejected.");
        library.MoveProjectToGroup(project.Id, null);
        Throws(() => library.PatchProject(project.Id, expected: renamed, groupId: group.Id), "Stale expected groups must be rejected.");
        var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform);
        Equal("rose", reopened.GetProject(project.Id).Color, "Color changes persist after restart.");
        Equal(project.Id, reopened.GetResource(item.Id, false).Projects.Single().Id, "Resource associations preserve permanent IDs after rename.");
        library.DeleteProject(project.Id);
        var replacement = library.CreateProject("Independent rename");
        Require(project.Id != replacement.Id, "Deleting and recreating the same display name must not reuse project identity.");
        library.DeleteProjectGroup(group.Id);
        var replacementGroup = library.CreateProjectGroup("Color group");
        Require(group.Id != replacementGroup.Id, "Deleting and recreating a group name must not reuse identity.");
        foreach (var preset in ProjectColors.Presets)
        {
            Equal(preset.Key, ProjectColors.Normalize(preset.Key), "Preset keys remain stable.");
            foreach (var background in new[] { "#FFFFFF", "#F3F3F3", "#E5E5E5" })
                Require(Contrast(preset.LightHex, background) >= 3, $"Light-theme folder color {preset.Key} must meet icon contrast on {background}.");
            foreach (var background in new[] { "#202020", "#272727", "#3B3B3B" })
                Require(Contrast(preset.DarkHex, background) >= 3, $"Dark-theme folder color {preset.Key} must meet icon contrast on {background}.");
        }
    }

    private static void ProjectGroupOrdering()
    {
        using var fixture = new Fixture();
        var library = fixture.Library;
        var first = library.CreateProjectGroup("First");
        var second = library.CreateProjectGroup("Second");
        var third = library.CreateProjectGroup("Third");
        library.MoveProjectGroup(third.Id, first.Id, false);
        Equal("Third,First,Second", string.Join(",", library.GetSnapshot(false).ProjectGroups!.Select(group => group.Name)), "Groups reorder independently.");
        var a = library.CreateProject("A", groupId: first.Id);
        var b = library.CreateProject("B", groupId: first.Id);
        var c = library.CreateProject("C", groupId: second.Id);
        var d = library.CreateProject("D", groupId: second.Id);
        var ungrouped = library.CreateProject("Ungrouped");
        library.MoveProject(b.Id, a.Id, false);
        Equal("B,A", ProjectNames(library, false, first.Id), "Ordinary projects sort inside their own group.");
        Equal("C,D", ProjectNames(library, false, second.Id), "Ordering another group stays unchanged.");
        library.MoveProject(b.Id, d.Id, true);
        Equal(second.Id, library.GetProject(b.Id).GroupId, "Cross-group drops adopt the target project group.");
        Equal("C,D,B", ProjectNames(library, false, second.Id), "Cross-group moves honor the target position.");
        Equal("A", ProjectNames(library, false, first.Id), "A moved project leaves its previous group.");
        library.MoveProjectToGroup(c.Id, first.Id);
        Equal("A,C", ProjectNames(library, false, first.Id), "Moving to a group appends.");
        library.SetProjectPinned(a.Id, true);
        library.SetProjectPinned(d.Id, true);
        library.MoveProject(d.Id, a.Id, false);
        Equal("D,A", string.Join(",", library.GetSnapshot(false).Projects.Where(project => project.IsPinned).Select(project => project.Name)), "Pinned ordering spans project groups.");
        Equal(second.Id, library.GetProject(d.Id).GroupId, "Pinned reordering does not adopt the target group.");
        var pinOrder = library.GetProject(d.Id).SortOrder;
        library.MoveProjectToGroup(d.Id, third.Id);
        Equal(pinOrder, library.GetProject(d.Id).SortOrder, "Pinned grouping keeps global order.");
        Require(library.GetProject(d.Id).IsPinned, "Moving a pinned project keeps pin state.");
        library.SetProjectPinned(d.Id, false);
        Equal("D", ProjectNames(library, false, third.Id), "Unpin returns the project to its own group.");
        library.PatchProject(c.Id, clearGroup: true);
        Equal("Ungrouped,C", ProjectNames(library, false, null), "Clear-group patches append to ungrouped.");
        Throws(() => library.MoveProject(c.Id, a.Id, false), "Cross-pin reordering must remain rejected.");
        var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform);
        Equal("Third,First,Second", string.Join(",", reopened.GetSnapshot(false).ProjectGroups!.Select(group => group.Name)), "Group order survives restart.");
        Equal("D", ProjectNames(reopened, false, third.Id), "Project group and local order survive restart.");
        Equal(ungrouped.Id, reopened.GetSnapshot(false).Projects.First(project => !project.IsPinned && project.GroupId is null).Id, "Existing ungrouped position stays intact.");
    }

    private static void ProjectGroupRollback()
    {
        using var fixture = new Fixture();
        var library = fixture.Library;
        var first = library.CreateProjectGroup("First");
        var second = library.CreateProjectGroup("Second");
        var third = library.CreateProjectGroup("Third");
        var a = library.CreateProject("A", groupId: first.Id);
        var b = library.CreateProject("B", groupId: first.Id);
        var beforeProjects = library.GetSnapshot(false).Projects.ToArray();
        fixture.ExecuteSql($"""
            CREATE TRIGGER reject_group_clear BEFORE UPDATE OF group_id ON Project
                WHEN NEW.id='{b.Id}' AND NEW.group_id IS NULL
                BEGIN SELECT RAISE(ABORT,'simulated group deletion failure'); END;
            """);
        Throws(() => library.DeleteProjectGroup(first.Id), "A database failure must abort group deletion.");
        Equal(first, library.GetProjectGroup(first.Id), "Failed deletion keeps its group.");
        Require(beforeProjects.SequenceEqual(library.GetSnapshot(false).Projects), "A failed deletion rolls back every earlier project update.");
        fixture.ExecuteSql("DROP TRIGGER reject_group_clear;");
        fixture.ExecuteSql($"""
            CREATE TRIGGER reject_group_order BEFORE UPDATE OF sort_order ON ProjectGroup
                WHEN NEW.id='{second.Id}' BEGIN SELECT RAISE(ABORT,'simulated reorder failure'); END;
            """);
        var beforeGroups = library.GetSnapshot(false).ProjectGroups!.ToArray();
        Throws(() => library.MoveProjectGroup(third.Id, first.Id, false), "Failed group ordering must be atomic.");
        Require(beforeGroups.SequenceEqual(library.GetSnapshot(false).ProjectGroups!), "Every group position rolls back after reorder failure.");
        Throws(() => library.PatchProjectGroup(first.Id, "Second"), "Existing group names reject rename collisions.");
        Throws(() => library.CreateProjectGroup(" "), "Blank group names must be rejected.");
        Throws(() => library.MoveProjectToGroup(a.Id, "missing"), "Invalid target groups must be rejected.");
        Equal(first.Id, library.GetProject(a.Id).GroupId, "Invalid moves keep the old group.");
        Equal(3, library.GetSnapshot(false).ProjectGroups!.Count, "Invalid operations keep all groups.");
    }

    private static void ProjectGroupMigration()
    {
        foreach (var version in new[] { 1, 3, 4 })
        {
            string path = "";
            using var fixture = new Fixture(beforeInitialize: setup =>
            {
                path = setup.File($"version-{version}.txt", "original migration content");
                setup.CreateLegacyDatabase([(path, "file")]);
                setup.ExecuteSql("""
                    INSERT INTO Project VALUES('other-project','Other project','OTHER PROJECT','Other description');
                    INSERT INTO ProjectItem VALUES('other-project','legacy-item-0');
                    """);
                if (version >= 3)
                    setup.ExecuteSql("""
                        ALTER TABLE Item ADD COLUMN volume_id TEXT;
                        ALTER TABLE Item ADD COLUMN file_id TEXT;
                        ALTER TABLE Item ADD COLUMN favicon BLOB;
                        UPDATE Item SET volume_id='kept-volume',file_id='kept-file';
                        PRAGMA user_version=3;
                        """);
                if (version == 4)
                    setup.ExecuteSql("""
                        ALTER TABLE Project ADD COLUMN is_pinned INTEGER NOT NULL DEFAULT 0;
                        ALTER TABLE Project ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0;
                        UPDATE Project SET is_pinned=1,sort_order=27 WHERE id='legacy-project';
                        UPDATE Project SET sort_order=13 WHERE id='other-project';
                        PRAGMA user_version=4;
                        """);
            });
            var library = fixture.Library;
            var snapshot = library.GetSnapshot(false);
            Equal(5L, Convert.ToInt64(fixture.SqlScalar("PRAGMA user_version;")), $"Version {version} migrates to schema five.");
            Equal(0, snapshot.ProjectGroups!.Count, "Migration adds no invented groups.");
            Equal(2, snapshot.Projects.Count, "Every old project remains.");
            var project = library.GetProject("legacy-project");
            Equal("Legacy project", project.Name, "Original project names remain.");
            Equal("Legacy project context", project.Description, "Original descriptions remain.");
            Equal("default", project.Color, "Legacy projects receive the neutral preset.");
            Require(project.GroupId is null, "Legacy projects start ungrouped.");
            if (version == 4)
            {
                Require(project.IsPinned, "Existing pin states remain.");
                Equal(27L, project.SortOrder, "Existing project order remains.");
            }
            var item = snapshot.Items.Single();
            Equal("legacy-item-0", item.Id, "Resource IDs stay unchanged.");
            Equal(path, item.Target, "Resource targets stay unchanged.");
            Equal("Legacy alias", item.Alias, "Resource aliases remain.");
            Equal("Legacy description", item.Description, "Resource descriptions remain.");
            Equal("Legacy note", item.Note, "Resource notes remain.");
            Equal(7L, item.OpenCount, "Open history remains.");
            Equal(DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"), item.CreatedAt, "Creation times remain.");
            Equal(DateTimeOffset.Parse("2026-02-03T04:05:06+00:00"), item.UpdatedAt, "Update times remain.");
            Equal(DateTimeOffset.Parse("2026-02-02T03:04:05+00:00"), item.LastOpenedAt, "Last-opened times remain.");
            SetEqual(["legacy-project", "other-project"], item.Projects.Select(member => member.Id), "Many-to-many memberships remain.");
            if (version >= 3) Equal(new FileIdentity("kept-volume", "kept-file"), item.FileIdentity, "Captured file identities remain.");
            Require(fixture.SqlScalar("PRAGMA foreign_key_check;") is null, "Migrated foreign keys must be valid.");
            var group = library.CreateProjectGroup("Migrated group");
            library.PatchProject(project.Id, name: "Renamed migrated project", color: "amber", groupId: group.Id);
            var reopened = new LibraryService(fixture.DatabasePath, fixture.Platform);
            Equal(project.Id, reopened.GetProject(project.Id).Id, "Old IDs remain permanent after first edits and restart.");
            SetEqual(["legacy-project", "other-project"], reopened.GetResource(item.Id, false).Projects.Select(member => member.Id), "Editing migrated projects keeps every join.");
        }
    }

    private static void ProjectGroupMigrationRollback()
    {
        using var fixture = new Fixture(beforeInitialize: setup =>
        {
            setup.CreateLegacyDatabase([]);
            setup.ExecuteSql("""
                ALTER TABLE Item ADD COLUMN volume_id TEXT;
                ALTER TABLE Item ADD COLUMN file_id TEXT;
                ALTER TABLE Item ADD COLUMN favicon BLOB;
                CREATE TABLE ProjectGroup(conflict TEXT);
                PRAGMA user_version=3;
                """);
            Throws(() => new LibraryService(setup.DatabasePath, setup.Platform), "Conflicting schema must abort the whole migration.");
            Equal(3L, Convert.ToInt64(setup.SqlScalar("PRAGMA user_version;")), "Failure restores the old schema version.");
            Equal(0L, Convert.ToInt64(setup.SqlScalar("SELECT COUNT(*) FROM pragma_table_info('Project') WHERE name IN ('sort_order','is_pinned','color','group_id');")),
                "Migration failure rolls back schema-four and schema-five column changes.");
            Equal("Legacy project", setup.SqlScalar("SELECT name FROM Project WHERE id='legacy-project';") as string, "Failed migration preserves user records.");
            setup.ExecuteSql("DROP TABLE ProjectGroup;");
        });
        Equal(5L, Convert.ToInt64(fixture.SqlScalar("PRAGMA user_version;")), "Migration succeeds after the schema conflict is resolved.");
    }

    private static string ProjectNames(LibraryService library, bool pinned, string? groupId) => string.Join(",",
        library.GetSnapshot(false).Projects.Where(project => project.IsPinned == pinned && project.GroupId == groupId).Select(project => project.Name));

    private static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var value = int.Parse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            static double Channel(int value)
            {
                var channel = value / 255d;
                return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
            }
            return .2126 * Channel((value >> 16) & 255) + .7152 * Channel((value >> 8) & 255) + .0722 * Channel(value & 255);
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
