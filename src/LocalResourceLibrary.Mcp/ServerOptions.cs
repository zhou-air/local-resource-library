namespace LocalResourceLibrary.Mcp;

public sealed record ServerOptions(string DataDirectory, bool AllowBatchCommit)
{
    public static ServerOptions Parse(string[] args)
    {
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalResourceLibrary");
        var allowBatchCommit = false;
        var hasDataDirectory = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--data-dir" when !hasDataDirectory && index + 1 < args.Length:
                    dataDirectory = args[++index];
                    ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
                    if (!Path.IsPathFullyQualified(dataDirectory))
                        throw new ArgumentException("--data-dir must be an absolute path.");
                    hasDataDirectory = true;
                    break;
                case "--allow-batch-commit" when !allowBatchCommit:
                    allowBatchCommit = true;
                    break;
                default:
                    throw new ArgumentException("Usage: LocalResourceLibrary.Mcp [--data-dir ABSOLUTE_PATH] [--allow-batch-commit]");
            }
        }
        return new ServerOptions(Path.GetFullPath(dataDirectory), allowBatchCommit);
    }
}
