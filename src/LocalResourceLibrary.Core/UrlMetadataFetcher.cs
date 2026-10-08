using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalResourceLibrary.Core;

public record WebsiteMetadata(string? Title, string? Description, byte[]? Favicon, string? Error);

/// <summary>Reads optional website metadata without storing it or changing the user's fields.</summary>
public sealed class UrlMetadataFetcher
{
    public const int MaxHtmlBytes = 1024 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex TitleTag = new(@"<title\b[^>]*>(?<text>[\s\S]*?)</title\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex MetadataTags = new("<(?<name>meta|link)\\b(?<attributes>(?:[^>\"']|\"[^\"]*\"|'[^']*')*)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Attributes = new("(?<name>[^\\s=/>]+)(?:\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<plain>[^\\s\"'=<>`]+)))?",
        RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex CharsetValue = new(@"(?:^|;)\s*charset\s*=\s*(?<encoding>[^;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex NonMetadataContent = new(@"<!--[\s\S]*?-->|<(script|style)\b[^>]*>[\s\S]*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    static UrlMetadataFetcher() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <param name="httpClient">Optional caller-owned client, primarily for deterministic transport checks.</param>
    /// <param name="timeout">Total time budget shared by the page, redirects, and favicon requests.</param>
    public UrlMetadataFetcher(HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _client = httpClient ?? SharedClient;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout), "网页信息获取超时必须在 0 到 60 秒之间。");
    }

    public async Task<WebsiteMetadata> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        var token = budget.Token;
        string? title = null;
        string? description = null;
        byte[]? favicon = null;
        try
        {
            var uri = new Uri(ResourceUrls.Normalize(url), UriKind.Absolute);
            using var response = await GetAsync(uri, false, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var bytes = await ReadBoundedAsync(response.Content, MaxHtmlBytes, "网页内容超过 1 MB，已停止自动获取。", token)
                .ConfigureAwait(false);
            var html = NonMetadataContent.Replace(DecodeHtml(bytes, response.Content.Headers.ContentType?.CharSet), " ");
            token.ThrowIfCancellationRequested();
            var titleMatch = TitleTag.Match(html);
            if (titleMatch.Success) title = CleanText(titleMatch.Groups["text"].Value, 1024, true);

            string? openGraphTitle = null;
            string? openGraphDescription = null;
            var iconLinks = new List<Uri>();
            foreach (Match tag in MetadataTags.Matches(html))
            {
                token.ThrowIfCancellationRequested();
                var attributes = ReadAttributes(tag.Groups["attributes"].Value);
                if (tag.Groups["name"].Value.Equals("meta", StringComparison.OrdinalIgnoreCase))
                {
                    attributes.TryGetValue("name", out var name);
                    attributes.TryGetValue("property", out var property);
                    if (!attributes.TryGetValue("content", out var content)) continue;
                    if (name?.Equals("description", StringComparison.OrdinalIgnoreCase) == true)
                        description ??= CleanText(content, 8192);
                    if (property?.Equals("og:title", StringComparison.OrdinalIgnoreCase) == true)
                        openGraphTitle ??= CleanText(content, 1024);
                    if (property?.Equals("og:description", StringComparison.OrdinalIgnoreCase) == true)
                        openGraphDescription ??= CleanText(content, 8192);
                    continue;
                }
                if (!attributes.TryGetValue("rel", out var rel) || !attributes.TryGetValue("href", out var href)) continue;
                var relations = rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (!relations.Any(value => value.Equals("icon", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("apple-touch-icon", StringComparison.OrdinalIgnoreCase))) continue;
                if (attributes.TryGetValue("type", out var type) && type.Contains("svg", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Uri.TryCreate(finalUri, WebUtility.HtmlDecode(href), out var iconUri) || !IsHttp(iconUri) ||
                    iconUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
                if (iconLinks.Count < 3 && !iconLinks.Contains(iconUri)) iconLinks.Add(iconUri);
            }
            title ??= openGraphTitle;
            description ??= openGraphDescription;
            var fallback = new Uri(finalUri, "/favicon.ico");
            if (!iconLinks.Contains(fallback)) iconLinks.Add(fallback);
            string? iconError = null;
            foreach (var iconUri in iconLinks)
            {
                try
                {
                    using var iconResponse = await GetAsync(iconUri, true, token).ConfigureAwait(false);
                    iconResponse.EnsureSuccessStatusCode();
                    if (iconResponse.Content.Headers.ContentType?.MediaType?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true)
                        throw new InvalidDataException("网站图标格式不受支持。");
                    var data = await ReadBoundedAsync(iconResponse.Content, ResourceUrls.MaxFaviconBytes,
                        "网站图标超过 512 KB，已使用默认图标。", token).ConfigureAwait(false);
                    if (!ResourceUrls.IsSupportedFavicon(data)) throw new InvalidDataException("网站图标格式不受支持。");
                    favicon = data;
                    iconError = null;
                    break;
                }
                catch (Exception exception) when (IsFetchFailure(exception))
                {
                    iconError = exception is InvalidDataException ? exception.Message : "未能获取网站图标，可使用默认图标。";
                }
            }
            token.ThrowIfCancellationRequested();
            return new WebsiteMetadata(title, description, favicon, iconError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebsiteMetadata(title, description, favicon, "获取网页信息超时，可手动填写后保存。");
        }
        catch (Exception exception) when (IsFetchFailure(exception) || exception is ArgumentException or RegexMatchTimeoutException)
        {
            var error = exception is InvalidDataException or ArgumentException ? exception.Message :
                "未能获取网页信息，可手动填写后保存。";
            return new WebsiteMetadata(title, description, favicon, error);
        }
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = DefaultTimeout
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private async Task<HttpResponseMessage> GetAsync(Uri uri, bool image, CancellationToken token)
    {
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("LocalResourceLibrary/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(image ? "image/*" : "text/html"));
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                var responseUri = response.RequestMessage?.RequestUri ?? uri;
                response.Dispose();
                if (redirect == 5 || location == null || !Uri.TryCreate(responseUri, location, out var nextUri) || !IsHttp(nextUri))
                    throw new HttpRequestException("网页重定向无效或次数过多。");
                uri = nextUri;
                continue;
            }
            return response;
        }
        throw new HttpRequestException("网页重定向次数过多。");
    }

    private static bool IsHttp(Uri uri) => uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
    private static bool IsFetchFailure(Exception exception) => exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException;

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, string limitError, CancellationToken token)
    {
        if (content.Headers.ContentLength > maxBytes) throw new InvalidDataException(limitError);
        await using var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - (int)result.Length + 1)), token)
                .ConfigureAwait(false);
            if (count == 0) return result.ToArray();
            if (result.Length + count > maxBytes) throw new InvalidDataException(limitError);
            result.Write(buffer, 0, count);
        }
    }

    private static string DecodeHtml(byte[] bytes, string? headerCharset)
    {
        Encoding? encoding = null;
        var offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { encoding = Encoding.UTF8; offset = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = Encoding.Unicode; offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = Encoding.BigEndianUnicode; offset = 2; }
        encoding ??= FindEncoding(headerCharset);
        if (encoding == null)
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 8192));
            foreach (Match tag in MetadataTags.Matches(head))
            {
                if (!tag.Groups["name"].Value.Equals("meta", StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = ReadAttributes(tag.Groups["attributes"].Value);
                if (attributes.TryGetValue("charset", out var charset)) encoding = FindEncoding(charset);
                if (encoding == null && attributes.TryGetValue("http-equiv", out var httpEquiv) &&
                    httpEquiv.Equals("content-type", StringComparison.OrdinalIgnoreCase) && attributes.TryGetValue("content", out var content))
                {
                    var match = CharsetValue.Match(content);
                    if (match.Success) encoding = FindEncoding(match.Groups["encoding"].Value);
                }
                if (encoding != null) break;
            }
        }
        return (encoding ?? Encoding.UTF8).GetString(bytes, offset, bytes.Length - offset);
    }

    private static Encoding? FindEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return null;
        try { return Encoding.GetEncoding(charset.Trim().Trim('\'', '"')); }
        catch (ArgumentException) { return null; }
    }

    private static Dictionary<string, string> ReadAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Attributes.Matches(text))
        {
            var value = match.Groups["double"].Success ? match.Groups["double"].Value :
                match.Groups["single"].Success ? match.Groups["single"].Value : match.Groups["plain"].Value;
            attributes.TryAdd(match.Groups["name"].Value, value);
        }
        return attributes;
    }

    private static string? CleanText(string text, int maxLength, bool stripTags = false)
    {
        // Bound unusually long metadata before running text cleanup expressions.
        if (text.Length > maxLength * 4) text = text[..(maxLength * 4)];
        if (stripTags) text = Tags.Replace(text, " ");
        text = Whitespace.Replace(WebUtility.HtmlDecode(text), " ").Trim();
        return text.Length == 0 ? null : text[..Math.Min(text.Length, maxLength)];
    }
}
