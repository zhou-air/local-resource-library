namespace LocalResourceLibrary.Core;

/// <summary>Website targets retain the user's exact spelling after trimming surrounding whitespace.</summary>
public static class ResourceUrls
{
    public const string Type = "url";
    public const int MaxFaviconBytes = 512 * 1024;

    public static string Normalize(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var target = url.Trim();
        if (target.Any(char.IsControl) ||
            !(target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ||
            !Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请输入完整的 HTTP 或 HTTPS 网址，不能包含控制字符或登录凭据。", nameof(url));
        return target;
    }

    public static string Key(string url) => "URL:" + Normalize(url);

    public static string DisplayName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : url;

    /// <summary>Only bounded bitmap icons are stored; HTML and active SVG content are rejected.</summary>
    public static bool IsSupportedFavicon(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || data.Length > MaxFaviconBytes) return false;
        var bytes = data.AsSpan();
        return (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) ||
               (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) ||
               (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) ||
               (bytes.Length >= 6 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0 &&
                (bytes[4] != 0 || bytes[5] != 0));
    }

    internal static byte[]? CopyFavicon(byte[]? favicon)
    {
        if (favicon == null) return null;
        if (!IsSupportedFavicon(favicon))
            throw new ArgumentException("网站图标必须为 PNG、JPEG、GIF 或 ICO 图片，且不超过 512 KB。", nameof(favicon));
        return favicon.ToArray();
    }
}
