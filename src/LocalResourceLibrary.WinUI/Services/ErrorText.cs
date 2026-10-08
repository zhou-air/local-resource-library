using LocalResourceLibrary.WinUI.Localization;

namespace LocalResourceLibrary.WinUI.Services;

/// <summary>Translates application-owned errors without altering paths or operating-system details.</summary>
public static class ErrorText
{
    private static readonly (string Chinese, string English)[] Messages =
    [
        ("请输入完整的 HTTP 或 HTTPS 网址，不能包含控制字符或登录凭据。", "Enter a complete HTTP or HTTPS URL without control characters or embedded credentials."),
        ("网站图标必须为 PNG、JPEG、GIF 或 ICO 图片，且不超过 512 KB。", "The website icon must be PNG, JPEG, GIF or ICO and no larger than 512 KB."),
        ("此操作仅适用于网址资源。", "This operation is only available for URL resources."),
        ("资源库中已存在完全相同的网址，请使用现有记录。", "This exact URL is already in the library. Use the existing record."),
        ("网址没有本地文件位置。", "A URL has no local file location."),
        ("此资源类型暂不支持重命名。", "Renaming is not supported for this resource type yet."),
        ("打开资源需要 Windows。", "Opening resources requires Windows."),
        ("路径不能为空。", "The path cannot be empty."),
        ("不能添加设备路径。", "Device paths cannot be added."),
        ("暂不支持名称末尾包含点或空格的扩展路径资源。", "Extended paths with names ending in a dot or space are not supported yet."),
        ("路径包含无效名称。", "The path contains an invalid name."),
        ("请输入有效的文件名，不能包含路径、特殊字符或末尾的点和空格。", "Enter a valid name without a path, invalid characters, or a trailing dot or space."),
        ("该名称是 Windows 保留名称。", "This name is reserved by Windows."),
        ("数据库来自较新版本，请使用较新版本的应用打开。", "This database was created by a newer version. Open it with a newer version of the application."),
        ("路径不存在或暂时无法访问。", "The path does not exist or is temporarily unavailable."),
        ("不能重命名驱动器或共享根目录。", "A drive or shared root folder cannot be renamed."),
        ("新路径不存在、暂时无法访问，或资源类型与原记录不同。", "The new path does not exist, is temporarily unavailable, or has a different resource type."),
        ("资源记录不存在。", "The resource record no longer exists."),
        ("资源已被其他进程修改，请保留当前草稿，刷新后重新编辑。", "Another process changed this resource. Keep your current draft, then refresh and edit it again."),
        ("项目已被其他进程修改，请刷新后重试。", "Another process changed this project. Refresh and try again."),
        ("此类型暂不支持修改本地路径。", "Changing the local path is not supported for this resource type yet."),
        ("目标名称已存在，不能覆盖其他文件或文件夹。", "The destination name already exists. Existing files or folders cannot be overwritten."),
        ("新路径不能位于原文件夹内部。", "The new path cannot be inside the original folder."),
        ("修改后存在重复的资源路径。", "This change would create duplicate resource paths."),
        ("新路径已由另一个资源记录使用，请先处理重复记录。", "Another resource record already uses this path. Resolve the duplicate record first."),
        ("资源已缺失或暂时无法访问，请先修复路径。", "The resource is missing or temporarily unavailable. Repair its path first."),
        ("项目不存在。", "The project no longer exists."),
        ("项目名称不能为空。", "The project name cannot be empty."),
        ("项目名称不能超过 200 个字符。", "The project name cannot exceed 200 characters."),
        ("已存在同名项目。", "A project with this name already exists.")
    ];

    private static readonly (string ChinesePrefix, string EnglishPrefix)[] RecoveryMessages =
    [
        ("数据库更新失败，原名称也未能恢复。资源当前可能位于“",
            "The database update failed, and the original name could not be restored. The resource may now be at “"),
        ("重命名失败，恢复原名称失败。资源暂时位于“",
            "The rename failed, and the original name could not be restored. The resource is temporarily at “")
    ];

    public static string Format(Exception ex, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(text);
        return !text.IsEnglish ? ex.Message : TranslateMessage(ex.Message);
    }

    public static string FormatMessage(string message, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(text);
        if (!text.IsEnglish) return message;
        var translated = TranslateMessage(message);
        if (!string.Equals(translated, message, StringComparison.Ordinal)) return translated;

        // AddPaths prefixes an individual error with the original path and ": ".
        // Translate only the suffix that is a recognized application error.
        var separator = message.IndexOf(": ", StringComparison.Ordinal);
        while (separator >= 0)
        {
            var offset = separator + 2;
            var error = message[offset..];
            translated = TranslateMessage(error);
            if (!string.Equals(translated, error, StringComparison.Ordinal)) return message[..offset] + translated;
            separator = message.IndexOf(": ", offset, StringComparison.Ordinal);
        }
        return message;
    }

    private static string TranslateMessage(string message)
    {
        foreach (var (chinese, english) in Messages)
        {
            if (!message.StartsWith(chinese, StringComparison.Ordinal)) continue;
            var suffix = message[chinese.Length..];
            if (suffix.Length > 0 && !suffix.StartsWith(" (", StringComparison.Ordinal) &&
                !suffix.StartsWith('（') && !suffix.StartsWith('\r') && !suffix.StartsWith('\n')) continue;
            // .NET may append a parameter name or file name; leave that exact suffix intact.
            return english + suffix;
        }

        const string recoverySuffix = "”，请用修复路径重新关联。";
        foreach (var (chinesePrefix, englishPrefix) in RecoveryMessages)
        {
            if (!message.StartsWith(chinesePrefix, StringComparison.Ordinal) ||
                !message.EndsWith(recoverySuffix, StringComparison.Ordinal)) continue;
            var path = message[chinesePrefix.Length..^recoverySuffix.Length];
            return englishPrefix + path + "”. Use Repair path to reconnect it.";
        }
        return message;
    }
}
