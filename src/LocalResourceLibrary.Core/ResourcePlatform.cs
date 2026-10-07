using System.Diagnostics;

namespace LocalResourceLibrary.Core;

public interface IResourcePlatform
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void Open(string path, string type);
    void OpenLocation(string path, string type);
    void Move(string oldPath, string newPath, string type);
}

public sealed class WindowsResourcePlatform : IResourcePlatform
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void Open(string path, string type)
    {
        RequireWindows();
        if (type == "folder")
        {
            StartExplorer(path, selectFile: false);
            return;
        }
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenLocation(string path, string type)
    {
        RequireWindows();
        StartExplorer(path, selectFile: type == "file");
    }

    private static void StartExplorer(string path, bool selectFile)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
        if (selectFile) start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start);
    }

    public void Move(string oldPath, string newPath, string type)
    {
        if (type == "folder") Directory.Move(oldPath, newPath);
        else if (type == "file") File.Move(oldPath, newPath, overwrite: false);
        else throw new NotSupportedException("此资源类型暂不支持重命名。");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("打开资源需要 Windows。");
    }
}

internal static class ResourcePaths
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var extendedPath = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal);
        // Normalize Win32 long-path aliases so the same ordinary path cannot be added twice.
        if (fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) fullPath = @"\\" + fullPath[8..];
        else if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            if (fullPath.Length < 7 || !char.IsAsciiLetter(fullPath[4]) || fullPath[5] != ':' || fullPath[6] != '\\')
                throw new ArgumentException("不能添加设备路径。");
            fullPath = fullPath[4..];
        }
        if (fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)) throw new ArgumentException("不能添加设备路径。");
        // Win32 treats trailing spaces and dots in ordinary path components as aliases.
        // Preserve the root while storing the ordinary path only once.
        var root = Path.GetPathRoot(fullPath)!;
        var rawSegments = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (extendedPath && rawSegments.Any(segment => segment != segment.TrimEnd(' ', '.')))
            throw new ArgumentException("暂不支持名称末尾包含点或空格的扩展路径资源。");
        var segments = rawSegments.Select(segment => segment.TrimEnd(' ', '.')).ToArray();
        if (segments.Any(segment => segment.Length == 0)) throw new ArgumentException("路径包含无效名称。");
        fullPath = segments.Length == 0 ? root : Path.Combine(root, Path.Combine(segments));
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static string Key(string path) => path.ToUpperInvariant();

    public static bool IsDescendant(string path, string directory) => path.StartsWith(
        Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    public static string Rebase(string path, string oldDirectory, string newDirectory) =>
        Normalize(Path.Combine(newDirectory, Path.GetRelativePath(oldDirectory, path)));

    public static void ValidateName(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName is "." or ".." ||
            newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            newName.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 ||
            newName.Any(char.IsControl) || newName.EndsWith(' ') || newName.EndsWith('.') || newName.Length > 255)
            throw new ArgumentException("请输入有效的文件名，不能包含路径、特殊字符或末尾的点和空格。", nameof(newName));
        var stem = newName.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
             (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³')))
            throw new ArgumentException("该名称是 Windows 保留名称。", nameof(newName));
    }
}
