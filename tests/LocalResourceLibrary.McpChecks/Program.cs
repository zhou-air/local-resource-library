using System.Diagnostics;
using System.Text.Json;
using LocalResourceLibrary.Core;
using LocalResourceLibrary.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LocalResourceLibrary.McpChecks;

internal static class Program
{
    private static int assertions;
    private static readonly CancellationTokenSource Deadline = new(TimeSpan.FromMinutes(3));

    private static async Task<int> Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalResourceLibrary-McpChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var server = FindServer(args);
            await ProtocolOutput(server, Path.Combine(root, "protocol"));
            await Integration(server, root);
            PreviewTokenLifetime();
            Console.WriteLine($"PASS: {assertions} MCP assertions; real stdio child processes, isolated SQLite, no WinUI launch.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL after {assertions} assertions: {error}");
            return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            // root is generated under Temp above; no user library or source resource is a cleanup target.
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindServer(string[] args)
    {
        if (args.Length == 2 && args[0] == "--server") return Path.GetFullPath(args[1]);
        if (args.Length != 0) throw new ArgumentException("Usage: McpChecks [--server PATH-TO-SERVER-DLL]");
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source != null && !Directory.Exists(Path.Combine(source.FullName, "src", "LocalResourceLibrary.Mcp")))
            source = source.Parent;
        if (source == null) throw new DirectoryNotFoundException("Cannot locate the source tree. Use --server PATH.");
        var configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar)
            ? "Release" : "Debug";
        var server = Path.Combine(source.FullName, "src", "LocalResourceLibrary.Mcp", "bin", configuration, "net10.0", "LocalResourceLibrary.Mcp.dll");
        if (!File.Exists(server)) throw new FileNotFoundException("Build the MCP server before running checks.", server);
        return server;
    }

    private static async Task<McpClient> Connect(string server, string dataDirectory, bool allowBatch = false)
    {
        List<string> arguments = [server, "--data-dir", dataDirectory];
        if (allowBatch) arguments.Add("--allow-batch-commit");
        var transport = new StdioClientTransport(new()
        {
            Command = "dotnet",
            Arguments = arguments,
            Name = "LocalResourceLibrary checks",
            WorkingDirectory = Path.GetDirectoryName(server),
            ShutdownTimeout = TimeSpan.FromSeconds(3)
        });
        return await McpClient.CreateAsync(transport, cancellationToken: Deadline.Token);
    }

    private static async Task ProtocolOutput(string server, string dataDirectory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(server);
        start.ArgumentList.Add("--data-dir");
        start.ArgumentList.Add(dataDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Server failed to start.");
        var stderr = process.StandardError.ReadToEndAsync(Deadline.Token);
        try
        {
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"raw-stdio-check\",\"version\":\"1\"}}}");
            using var initialized = JsonDocument.Parse(await ReadLine(process));
            Assert(initialized.RootElement.GetProperty("jsonrpc").GetString() == "2.0" && initialized.RootElement.GetProperty("id").GetInt32() == 1,
                "stdout initialization is one JSON-RPC line without startup banners");
            Assert(initialized.RootElement.GetProperty("result").TryGetProperty("serverInfo", out _), "raw initialization discovers server info");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
            using var listed = JsonDocument.Parse(await ReadLine(process));
            Assert(listed.RootElement.GetProperty("id").GetInt32() == 2 && listed.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() >= 11,
                "stdout tool discovery contains only protocol JSON");
            process.StandardInput.Close();
            await process.WaitForExitAsync(Deadline.Token);
            var tail = await process.StandardOutput.ReadToEndAsync(Deadline.Token);
            Assert(string.IsNullOrWhiteSpace(tail), "stdout has no shutdown or diagnostic text");
            Assert(process.ExitCode == 0, "server exits normally after stdin closes");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await stderr;
        }
    }

    private static async Task<string> ReadLine(Process process) =>
        await process.StandardOutput.ReadLineAsync(Deadline.Token) ?? throw new EndOfStreamException("Server closed stdout before responding.");

    private static async Task Integration(string server, string root)
    {
        var dataDirectory = Path.Combine(root, "library");
        var file = Path.Combine(root, "PDMS-Catalogue-name-needle.txt");
        await File.WriteAllTextAsync(file, "Source file stays unchanged. 内容由 Agent 自己读取。", Deadline.Token);
        var folder = Path.Combine(root, "folder-path-needle");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "unindexed-child.txt"), "Folder collection does not scan this file.", Deadline.Token);
        const string url = "https://example.invalid/catalogue/url-needle?ref=1#section";
        await using var client = await Connect(server, dataDirectory);
        var tools = await client.ListToolsAsync(cancellationToken: Deadline.Token);
        string[] expected = ["search_resources", "get_resource", "list_projects", "list_project_resources", "add_resource", "update_resource",
            "create_project", "update_project", "set_resource_projects", "preview_resource_updates", "commit_resource_updates"];
        Assert(tools.Select(tool => tool.Name).Order().SequenceEqual(expected.Order()), "server exposes exactly the scoped tools; no file deletion, execution, scanning, or parsing tools");
        foreach (var name in expected)
        {
            var tool = tools.Single(tool => tool.Name == name).ProtocolTool;
            Assert(!string.IsNullOrWhiteSpace(tool.Description), $"{name} has an explanation");
            var schema = JsonSerializer.SerializeToElement(tool.InputSchema);
            Assert(schema.GetProperty("type").GetString() == "object", $"{name} advertises an object input schema");
            Assert(tool.Annotations != null && tool.Annotations.OpenWorldHint == false, $"{name} declares its local scope");
        }
        foreach (var name in new[] { "search_resources", "list_projects", "list_project_resources", "preview_resource_updates" })
            Assert(tools.Single(tool => tool.Name == name).ProtocolTool.Annotations?.ReadOnlyHint == true, $"{name} is marked read-only");
        Assert(tools.Single(tool => tool.Name == "get_resource").ProtocolTool.Annotations?.ReadOnlyHint == false,
            "get_resource accurately declares possible file-identity recovery writes");
        Assert(tools.Single(tool => tool.Name == "update_resource").ProtocolTool.Annotations?.ReadOnlyHint == false, "metadata update is marked write");
        Assert(tools.Single(tool => tool.Name == "commit_resource_updates").ProtocolTool.Annotations?.DestructiveHint == true, "batch submit advertises its higher-impact permission boundary");
        var updateSchema = JsonSerializer.SerializeToElement(tools.Single(tool => tool.Name == "update_resource").ProtocolTool.InputSchema);
        Assert(updateSchema.GetProperty("properties").TryGetProperty("item_id", out _) && updateSchema.GetProperty("properties").TryGetProperty("description", out _),
            "tool schema uses the documented snake_case inputs");

        Assert(Items(await Ok(client, "search_resources")).Length == 0, "fresh isolated library is empty without WinUI");
        var projectA = (await Ok(client, "create_project", ("name", "PDMS project-needle"), ("description", "Original project description"))).GetProperty("project");
        var projectB = (await Ok(client, "create_project", ("name", "ModelCreator"), ("description", "Interop"))).GetProperty("project");
        var idA = Id(projectA);
        var idB = Id(projectB);
        Assert((await Ok(client, "list_projects")).GetProperty("projects").GetArrayLength() == 2, "projects list shared Core records");
        await Error(client, "create_project", ("name", "pdms project-needle"));
        await Error(client, "create_project", ("name", " "));

        var fileAdded = await Ok(client, "add_resource", ("type", "file"), ("target", file), ("alias", "alias-needle"),
            ("description", "description-needle"), ("note", "note-needle"), ("project_ids", new[] { idA }));
        var fileId = Id(fileAdded.GetProperty("item"));
        var folderAdded = await Ok(client, "add_resource", ("type", "folder"), ("target", folder), ("description", "Folder description"), ("project_ids", new[] { idB }));
        var folderId = Id(folderAdded.GetProperty("item"));
        var urlAdded = await Ok(client, "add_resource", ("type", "url"), ("target", "  " + url + "  "), ("alias", "Web Catalogue"),
            ("description", "URL description"), ("note", "URL note"), ("project_ids", new[] { idA, idB }));
        var urlId = Id(urlAdded.GetProperty("item"));
        Assert(fileAdded.GetProperty("added").GetBoolean() && folderAdded.GetProperty("added").GetBoolean() && urlAdded.GetProperty("added").GetBoolean(), "all three resource types can be registered");
        var all = await Ok(client, "search_resources");
        Assert(Items(all).Length == 3 && all.GetProperty("total").GetInt32() == 3, "folder collection records only the folder; no recursive scan");
        Assert(Items(all).Select(item => String(item, "type")).Order().SequenceEqual(new[] { "file", "folder", "url" }), "search returns files, folders, and URLs together");
        foreach (var type in new[] { "file", "folder", "url" })
            Assert(Items(await Ok(client, "search_resources", ("type", type))).Length == 1, $"type filter selects {type}");
        foreach (var needle in new[] { "name-needle", "alias-needle", "description-needle", "note-needle", "project-needle" })
            Assert(Items(await Ok(client, "search_resources", ("query", needle))).Any(item => Id(item) == fileId), $"search includes {needle}");
        Assert(Items(await Ok(client, "search_resources", ("query", "folder-path-needle"))).Single().GetProperty("id").GetString() == folderId, "search includes physical target path");
        Assert(Items(await Ok(client, "search_resources", ("query", "url-needle"))).Single().GetProperty("id").GetString() == urlId, "search includes complete URL");
        Assert(Items(await Ok(client, "search_resources", ("query", "PDMS description-needle"))).Single().GetProperty("id").GetString() == fileId, "multiple search words match across metadata fields");
        Assert(Items(await Ok(client, "search_resources", ("project_id", idA))).Length == 2, "project filter reuses shared memberships");
        Assert(Items(await Ok(client, "list_project_resources", ("project_id", idB))).Length == 2, "project resources include folder and URL");
        var page1 = await Ok(client, "search_resources", ("limit", 1), ("offset", 0));
        var page2 = await Ok(client, "search_resources", ("limit", 1), ("offset", 1));
        Assert(Items(page1).Length == 1 && page1.GetProperty("total").GetInt32() == 3 && page1.GetProperty("has_more").GetBoolean(), "limit reports total and additional results");
        Assert(Id(Items(page1).Single()) != Id(Items(page2).Single()), "stable pagination advances to the next resource");
        Assert(Items(await Ok(client, "search_resources", ("offset", 100))).Length == 0, "offset beyond the library is empty");
        var projectPage = await Ok(client, "list_project_resources", ("project_id", idB), ("limit", 1), ("offset", 1));
        Assert(Items(projectPage).Length == 1 && projectPage.GetProperty("total").GetInt32() == 2, "project listing supports pagination");

        var fileItem = (await Ok(client, "get_resource", ("item_id", fileId))).GetProperty("item");
        var folderItem = (await Ok(client, "get_resource", ("item_id", folderId))).GetProperty("item");
        var urlItem = (await Ok(client, "get_resource", ("item_id", urlId))).GetProperty("item");
        Assert(String(fileItem, "path") == file && String(folderItem, "path") == folder && String(urlItem, "url") == url, "get_resource returns the real paths and exact trimmed URL");
        Assert(fileItem.GetProperty("available").GetBoolean() && folderItem.GetProperty("available").GetBoolean(), "local availability is checked");
        Assert(urlItem.GetProperty("available").ValueKind == JsonValueKind.Null && String(urlItem, "availability") == "not_checked", "URL availability is explicitly unprobed");
        Assert(String(fileItem, "alias") == "alias-needle" && String(fileItem, "description") == "description-needle" && String(fileItem, "note") == "note-needle", "get_resource exposes full resource context");

        var repeatedFile = await Ok(client, "add_resource", ("type", "file"), ("target", file), ("alias", "do not overwrite"), ("description", "do not overwrite"), ("note", "do not overwrite"), ("project_ids", new[] { idB, idB }));
        var repeatedFolder = await Ok(client, "add_resource", ("type", "folder"), ("target", folder), ("description", "do not overwrite"));
        var repeatedUrl = await Ok(client, "add_resource", ("type", "url"), ("target", url), ("alias", "do not overwrite"), ("description", "do not overwrite"), ("project_ids", new[] { idA }));
        Assert(!repeatedFile.GetProperty("added").GetBoolean() && Id(repeatedFile.GetProperty("item")) == fileId && String(repeatedFile.GetProperty("item"), "alias") == "alias-needle", "duplicate file reuses Item ID and preserves metadata");
        Assert(!repeatedFolder.GetProperty("added").GetBoolean() && Id(repeatedFolder.GetProperty("item")) == folderId && String(repeatedFolder.GetProperty("item"), "description") == "Folder description", "duplicate folder preserves metadata");
        Assert(!repeatedUrl.GetProperty("added").GetBoolean() && Id(repeatedUrl.GetProperty("item")) == urlId && String(repeatedUrl.GetProperty("item"), "alias") == "Web Catalogue", "duplicate URL preserves metadata");
        Assert(Projects(repeatedFile.GetProperty("item")).Order().SequenceEqual(new[] { idA, idB }.Order()), "re-add merges distinct projects without duplicate resource records");
        Assert(Items(await Ok(client, "search_resources")).Length == 3, "duplicate collection keeps one record per resource");

        var patched = (await Ok(client, "update_resource", ("item_id", fileId), ("description", "patched description"))).GetProperty("item");
        Assert(String(patched, "alias") == "alias-needle" && String(patched, "note") == "note-needle" && String(patched, "description") == "patched description", "metadata patch updates only explicitly provided fields");
        var cleared = (await Ok(client, "update_resource", ("item_id", fileId), ("alias", ""))).GetProperty("item");
        Assert(String(cleared, "alias") == "" && String(cleared, "description") == "patched description", "explicit empty string clears just that field");
        var urlPatched = (await Ok(client, "update_resource", ("item_id", urlId), ("note", "patched URL note"))).GetProperty("item");
        Assert(String(urlPatched, "note") == "patched URL note" && String(urlPatched, "target") == url && Projects(urlPatched).Length == 2, "URL metadata patch preserves target and memberships");
        var nullPatched = (await Ok(client, "update_resource", ("item_id", urlId), ("alias", null), ("description", "URL description"))).GetProperty("item");
        Assert(String(nullPatched, "alias") == "Web Catalogue" && String(nullPatched, "note") == "patched URL note", "optional null preserves existing fields instead of clearing metadata");
        var membership = (await Ok(client, "set_resource_projects", ("item_id", fileId), ("add_project_ids", new[] { idA, idB, idB }))).GetProperty("item");
        Assert(Projects(membership).Length == 2, "membership add is idempotent and retains other projects");
        membership = (await Ok(client, "set_resource_projects", ("item_id", fileId), ("remove_project_ids", new[] { idA }))).GetProperty("item");
        Assert(Projects(membership).SequenceEqual(new[] { idB }), "membership remove preserves unrelated projects");
        await Ok(client, "set_resource_projects", ("item_id", fileId), ("add_project_ids", new[] { idA }));
        await Error(client, "set_resource_projects", ("item_id", fileId), ("add_project_ids", new[] { idA }), ("remove_project_ids", new[] { idA }));
        await Error(client, "set_resource_projects", ("item_id", fileId), ("remove_project_ids", new[] { idB }), ("add_project_ids", new[] { "missing-project" }));
        Assert(Projects((await Ok(client, "get_resource", ("item_id", fileId))).GetProperty("item")).Length == 2, "invalid membership change rolls back all requested additions and removals");
        var renamed = (await Ok(client, "update_project", ("project_id", idA), ("name", "PDMS Renamed"))).GetProperty("project");
        Assert(Id(renamed) == idA && String(renamed, "description") == "Original project description", "project name patch preserves ID and omitted description");
        var described = (await Ok(client, "update_project", ("project_id", idA), ("description", "New project description"))).GetProperty("project");
        Assert(String(described, "name") == "PDMS Renamed", "project description patch preserves omitted name");
        Assert(Items(await Ok(client, "list_project_resources", ("project_id", idA))).Length == 2, "project edits preserve resource memberships");

        foreach (var arguments in new[] { Args(("type", "bad")), Args(("limit", 0)), Args(("limit", 1001)), Args(("offset", -1)), Args(("project_id", "missing")) })
            await Error(client, "search_resources", arguments);
        await Error(client, "get_resource", ("item_id", "missing"));
        await Error(client, "list_project_resources", ("project_id", "missing"));
        await Error(client, "update_resource", ("item_id", "missing"), ("note", "x"));
        await Error(client, "update_resource", ("item_id", fileId));
        await Error(client, "update_project", ("project_id", idA));
        await Error(client, "set_resource_projects", ("item_id", fileId));
        await Error(client, "update_project", ("project_id", "missing"), ("name", "x"));
        await Error(client, "add_resource", ("type", "file"), ("target", folder));
        await Error(client, "add_resource", ("type", "folder"), ("target", file));
        await Error(client, "add_resource", ("type", "url"), ("target", "file:///C:/private.txt"));
        await Error(client, "add_resource", ("type", "url"), ("target", "https://example.invalid/new"), ("project_ids", new[] { "missing" }));
        var invalidMembershipFile = Path.Combine(root, "invalid-membership.txt");
        await File.WriteAllTextAsync(invalidMembershipFile, "keep this file", Deadline.Token);
        await Error(client, "add_resource", ("type", "file"), ("target", invalidMembershipFile), ("project_ids", new[] { "missing" }));
        Assert(Items(await Ok(client, "search_resources")).Length == 3, "invalid resource additions leave no partial records");
        await ProtocolError(client, "get_resource", new Dictionary<string, object?>());
        await ProtocolError(client, "unknown_tool", new Dictionary<string, object?>());

        var service = new LibraryService(Path.Combine(dataDirectory, "library.db"));
        Assert(service.GetSnapshot(false).Items.Single(item => item.Id == fileId).Description == "patched description", "Core/WinUI peer reads changes committed by MCP");
        await Batch(server, dataDirectory, client, service, fileId, folderId);
        await ConcurrentClients(server, dataDirectory, client, service, fileId);
        await Recovery(client, service, root, file, fileId, folder, folderId);
        await using var reopened = await Connect(server, dataDirectory);
        Assert((await Ok(reopened, "get_resource", ("item_id", urlId))).GetProperty("item").GetProperty("note").GetString() == "patched URL note", "a newly started server sees persisted metadata");
        Assert(File.Exists(Path.Combine(folder + "-moved", "unindexed-child.txt")), "MCP never deletes or copies original folder contents");
        Assert(await File.ReadAllTextAsync(Path.Combine(root, "renamed-resource.txt"), Deadline.Token) == "Source file stays unchanged. 内容由 Agent 自己读取。", "MCP never edits the source file content");
    }

    private static async Task Batch(string server, string dataDirectory, McpClient client, LibraryService service, string fileId, string folderId)
    {
        var before = service.GetSnapshot(false).Items.Single(item => item.Id == fileId);
        var updates = new[] { new Dictionary<string, object?> { ["item_id"] = fileId, ["description"] = "batch description" },
            new Dictionary<string, object?> { ["item_id"] = folderId, ["note"] = "batch folder note" } };
        var preview = await Ok(client, "preview_resource_updates", ("updates", updates));
        Assert(!preview.GetProperty("commit_enabled").GetBoolean() && preview.GetProperty("changes").GetArrayLength() == 2, "default server can preview batch updates while commit remains disabled");
        Assert(service.GetSnapshot(false).Items.Single(item => item.Id == fileId).Description == before.Description, "batch preview writes no resource metadata");
        await Error(client, "commit_resource_updates", ("confirmation_token", String(preview, "confirmation_token")));
        Assert(service.GetSnapshot(false).Items.Single(item => item.Id == fileId).Description == before.Description, "disabled batch commit leaves metadata untouched");
        await using var enabled = await Connect(server, dataDirectory, allowBatch: true);
        preview = await Ok(enabled, "preview_resource_updates", ("updates", updates));
        Assert(preview.GetProperty("commit_enabled").GetBoolean(), "explicit server opt-in enables the batch submit boundary");
        var token = String(preview, "confirmation_token");
        await using var otherEnabled = await Connect(server, dataDirectory, allowBatch: true);
        await Error(otherEnabled, "commit_resource_updates", ("confirmation_token", token));
        var commit = await Ok(enabled, "commit_resource_updates", ("confirmation_token", token));
        Assert(commit.GetProperty("committed").GetBoolean() && commit.GetProperty("items").GetArrayLength() == 2 && commit.GetProperty("failures").GetArrayLength() == 0,
            "confirmed batch commits all metadata in one transaction");
        Assert(service.GetSnapshot(false).Items.Single(item => item.Id == fileId).Description == "batch description" && service.GetSnapshot(false).Items.Single(item => item.Id == folderId).Note == "batch folder note", "Core reads every committed batch result");
        await Error(enabled, "commit_resource_updates", ("confirmation_token", token));
        await Error(enabled, "commit_resource_updates", ("confirmation_token", "not-a-token"));

        updates[0]["description"] = "should roll back";
        updates[1]["note"] = "should roll back";
        preview = await Ok(enabled, "preview_resource_updates", ("updates", updates));
        var stale = service.GetSnapshot(false).Items.Single(item => item.Id == folderId);
        service.UpdateItem(folderId, stale.Alias, stale.Description, "external note after preview", stale.Projects.Select(project => project.Id));
        var failure = await Error(enabled, "commit_resource_updates", ("confirmation_token", String(preview, "confirmation_token")));
        Assert(!failure.GetProperty("committed").GetBoolean() && failure.GetProperty("failures").GetArrayLength() > 0, "stale preview reports failed resource IDs");
        Assert(service.GetSnapshot(false).Items.Single(item => item.Id == fileId).Description == "batch description" && service.GetSnapshot(false).Items.Single(item => item.Id == folderId).Note == "external note after preview", "one stale item rolls back the complete batch without overwriting peer changes");
        await Error(enabled, "preview_resource_updates", ("updates", new[] { new Dictionary<string, object?> { ["item_id"] = "missing", ["note"] = "x" } }));
        await Error(enabled, "preview_resource_updates", ("updates", Array.Empty<object>()));
        await Error(enabled, "preview_resource_updates", ("updates", Enumerable.Range(0, 101).Select(_ => new Dictionary<string, object?> { ["item_id"] = fileId, ["note"] = "x" }).ToArray()));
        await Error(enabled, "preview_resource_updates", ("updates", new[] { new Dictionary<string, object?> { ["item_id"] = fileId, ["note"] = "x" }, new Dictionary<string, object?> { ["item_id"] = fileId, ["note"] = "y" } }));
    }

    private static async Task ConcurrentClients(string server, string dataDirectory, McpClient client, LibraryService service, string fileId)
    {
        await using var peer = await Connect(server, dataDirectory);
        var before = service.GetSnapshot(false).Items.Single(item => item.Id == fileId);
        var tasks = Enumerable.Range(0, 12).Select(async index =>
        {
            await Task.WhenAll(
                Ok(client, "update_resource", ("item_id", fileId), ("alias", "parallel alias " + index)),
                Ok(peer, "update_resource", ("item_id", fileId), ("description", "parallel description " + index)),
                Task.Run(() => service.PatchResourceMetadata(new ResourceMetadataPatch(fileId, Note: "parallel Core note " + index))));
        });
        await Task.WhenAll(tasks);
        var actual = service.GetSnapshot(false).Items.Single(item => item.Id == fileId);
        Assert(actual.Alias.StartsWith("parallel alias ", StringComparison.Ordinal) && actual.Description.StartsWith("parallel description ", StringComparison.Ordinal), "two simultaneous MCP servers preserve independently patched fields");
        Assert(actual.Note.StartsWith("parallel Core note ", StringComparison.Ordinal) && actual.Projects.Select(project => project.Id).Order().SequenceEqual(before.Projects.Select(project => project.Id).Order()), "MCP and a concurrent Core/WinUI peer retain all three field changes and project associations");
        var duplicateAdds = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Ok(index % 2 == 0 ? client : peer, "add_resource", ("type", "url"), ("target", "https://example.invalid/concurrent-duplicate"))));
        Assert(duplicateAdds.Select(result => Id(result.GetProperty("item"))).Distinct().Count() == 1 && duplicateAdds.Count(result => result.GetProperty("added").GetBoolean()) == 1, "concurrent add reuses a single unique record across both server processes");
    }

    private static async Task Recovery(McpClient client, LibraryService service, string root, string file, string fileId, string folder, string folderId)
    {
        var before = service.GetSnapshot(false).Items.Single(item => item.Id == fileId);
        Assert(before.FileIdentity != null, "Windows NTFS file identity was captured by MCP add");
        var renamed = Path.Combine(root, "renamed-resource.txt");
        File.Move(file, renamed);
        var unresolved = (await Ok(client, "get_resource", ("item_id", fileId), ("resolve_path", false))).GetProperty("item");
        Assert(String(unresolved, "target") == file && unresolved.GetProperty("is_missing").GetBoolean(), "resolve_path=false reports old missing path without recovery");
        var recovered = (await Ok(client, "get_resource", ("item_id", fileId))).GetProperty("item");
        Assert(Id(recovered) == fileId && String(recovered, "path") == renamed && recovered.GetProperty("available").GetBoolean(), "default get_resource recovers externally renamed file by native identity");
        var after = service.GetSnapshot(false).Items.Single(item => item.Id == fileId);
        Assert(after.FileIdentity == before.FileIdentity && after.Alias == before.Alias && after.Description == before.Description && after.Note == before.Note && after.Projects.Select(project => project.Id).Order().SequenceEqual(before.Projects.Select(project => project.Id).Order()), "file recovery preserves permanent Item ID, identity, metadata, and memberships");
        Directory.Move(folder, folder + "-moved");
        var missingFolder = (await Ok(client, "get_resource", ("item_id", folderId))).GetProperty("item");
        Assert(String(missingFolder, "target") == folder && missingFolder.GetProperty("is_missing").GetBoolean(), "folders remain path references with no automatic identity scan");
    }

    private static void PreviewTokenLifetime()
    {
        var clock = new AdjustableClock();
        var store = new BatchPreviewStore(clock);
        var patches = new[] { new ResourceMetadataPatch("isolated-token-check", Description: "preview") };
        var valid = store.Create(patches, []);
        Assert(valid.ExpiresAt == clock.GetUtcNow().AddMinutes(10), "preview tokens have a bounded ten-minute lifetime");
        Assert(store.Take(valid.Token).Patches.Single() == patches.Single(), "valid token retrieves the exact staged patch");
        Throws<InvalidOperationException>(() => store.Take(valid.Token), "consumed token cannot be replayed");
        var expired = store.Create(patches, []);
        clock.Advance(TimeSpan.FromMinutes(10));
        Throws<InvalidOperationException>(() => store.Take(expired.Token), "expired preview token is rejected at its exact deadline without wall-clock waiting");
        Throws<InvalidOperationException>(() => store.Take(expired.Token), "expired token is permanently consumed");
        Throws<InvalidOperationException>(() => store.Take("unknown-token"), "unknown token is rejected");
        Throws<ArgumentException>(() => store.Take(" "), "blank token is rejected");
        var pending = Enumerable.Range(0, 32).Select(_ => store.Create(patches, [])).ToArray();
        Assert(pending.Select(batch => batch.Token).Distinct().Count() == 32 && pending.All(batch => batch.Token.Length >= 64), "preview tokens are distinct cryptographic values");
        Throws<InvalidOperationException>(() => store.Create(patches, []), "pending previews are bounded per server process");
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert(store.Create(patches, []).ExpiresAt > clock.GetUtcNow(), "expired previews are pruned before new previews are staged");
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { Assert(true, label); return; }
        throw new InvalidOperationException(label);
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] arguments) => arguments.ToDictionary(pair => pair.Key, pair => pair.Value);
    private static Task<JsonElement> Ok(McpClient client, string tool, params (string Key, object? Value)[] arguments) => Call(client, tool, Args(arguments), isError: false);
    private static Task<JsonElement> Error(McpClient client, string tool, params (string Key, object? Value)[] arguments) => Error(client, tool, Args(arguments));
    private static Task<JsonElement> Error(McpClient client, string tool, Dictionary<string, object?> arguments) => Call(client, tool, arguments, isError: true);

    private static async Task<JsonElement> Call(McpClient client, string tool, Dictionary<string, object?> arguments, bool isError)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Deadline.Token);
        var contents = result.Content.OfType<TextContentBlock>().ToArray();
        if ((result.IsError == true) != isError)
            throw new InvalidOperationException($"{tool}: expected isError={isError}, actual={result.IsError}; {string.Join(" ", contents.Select(content => content.Text))}");
        if (isError) assertions++;
        var structured = JsonSerializer.SerializeToElement(result.StructuredContent);
        if (structured.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(tool + " returned no structured object.");
        if (contents.Length == 0) throw new InvalidOperationException(tool + " returned no text compatibility content.");
        using var text = JsonDocument.Parse(contents.Single().Text);
        if (!JsonElement.DeepEquals(text.RootElement, structured)) throw new InvalidOperationException(tool + " structured and text results differ.");
        return structured;
    }

    private static async Task ProtocolError(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        try
        {
            var result = await client.CallToolAsync(tool, arguments, cancellationToken: Deadline.Token);
            Assert(result.IsError == true, tool + " rejects absent required arguments or unknown tool");
        }
        catch (McpException) { assertions++; }
    }

    private static string Id(JsonElement item) => String(item, "id");
    private static string String(JsonElement item, string property) => item.GetProperty(property).GetString() ?? throw new InvalidOperationException(property + " was null.");
    private static JsonElement[] Items(JsonElement response) => response.GetProperty("items").EnumerateArray().ToArray();
    private static string[] Projects(JsonElement item) => item.GetProperty("projects").EnumerateArray().Select(Id).ToArray();
    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        assertions++;
        Console.WriteLine("PASS: " + label);
    }
}
