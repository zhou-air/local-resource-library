using System.Net;
using System.Text;
using LocalResourceLibrary.Core;
using LocalResourceLibrary.WinUI.Services;

namespace LocalResourceLibrary.UrlChecks;

internal static class Program
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2];

    private static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] checks =
        [
            ("HTML metadata ignores attribute order and casing, decodes entities once", HtmlMetadata),
            ("Redirects resolve relative favicons against the final page URL", RedirectRelativeIcon),
            ("OpenGraph supplies missing title and description", OpenGraphFallback),
            ("Missing metadata still allows a successful fetch", NoMetadata),
            ("Favicon errors retain page title and description", FaviconFailure),
            ("Broken preferred icons fall back to a valid root favicon", BrokenIconFallback),
            ("SVG links are skipped and /favicon.ico is used", SvgFallback),
            ("Comments and JavaScript do not supply metadata", IgnoreScriptsAndComments),
            ("Untrusted HTML disguised as an icon is rejected", InvalidIcon),
            ("Failed HTTP and invalid targets return editable failure metadata", PageFailures),
            ("Relative redirect chains are bounded and non-HTTP redirects rejected", RedirectLimits),
            ("Header and meta character encodings retain Chinese titles", ChineseCharsets),
            ("Oversized declared HTML never reads or fetches an icon", HtmlDeclaredLimit),
            ("Oversized streaming HTML stops at the read limit", HtmlStreamingLimit),
            ("Oversized icons are rejected while metadata survives", IconLimit),
            ("Oversized streaming icons stop at the read limit", IconStreamingLimit),
            ("Page timeout returns a failure within the shared time budget", PageTimeout),
            ("Favicon timeout retains already fetched page metadata", IconTimeout),
            ("External cancellation propagates to HTTP and caller", CallerCancellation),
            ("Draft fills untouched fields and preserves manual edits and deliberate blanks", DraftManualEdits),
            ("Draft rejects old URL results after changing away and back", DraftStaleResponses),
            ("Editing an existing item protects even intentionally empty saved fields", DraftExistingFields),
            ("Draft clears favicon on URL change and preserves it on same-URL failure", DraftFavicon)
        ];
        var failures = 0;
        foreach (var (name, run) in checks)
        {
            try { await run(); Console.WriteLine($"PASS  {name}"); }
            catch (Exception exception) { failures++; Console.WriteLine($"FAIL  {name}\n      {exception}"); }
        }
        Console.WriteLine($"\n{checks.Length - failures}/{checks.Length} checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task HtmlMetadata()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/icon.png"
            ? Icon() : Html("<TITLE> 中文 &amp; Tools </TITLE><META CONTENT='A &amp; B &amp;lt;literal&amp;gt;' NAME=DeScRiPtIoN><LINK HREF='/icon.png' REL='SHORTCUT ICON' TYPE='image/png'>"));
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page");
        Equal("中文 & Tools", result.Title);
        Equal("A & B &lt;literal&gt;", result.Description);
        Require(result.Favicon?.SequenceEqual(Png) == true, "PNG bytes must be retained.");
        Equal(null, result.Error);
    }

    private static async Task RedirectRelativeIcon()
    {
        var paths = new List<string>();
        using var client = Client(request =>
        {
            paths.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath == "/start") return Redirect("/docs/page.html");
            if (request.RequestUri.AbsolutePath == "/docs/page.html") return Html("<title>Final</title><link rel=icon href='assets/icon.png?a=1&amp;b=2'>");
            return request.RequestUri.AbsoluteUri == "https://example.test/docs/assets/icon.png?a=1&b=2" ? Icon() : Missing();
        });
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/start");
        Equal("Final", result.Title);
        Require(result.Favicon != null, "Final-page relative icon must be fetched.");
        Equal(3, paths.Count);
        Equal("https://example.test/docs/assets/icon.png?a=1&b=2", paths[2]);
    }

    private static async Task OpenGraphFallback()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/favicon.ico" ? Icon() :
            Html("<title> </title><meta content='Fallback &amp; title' PROPERTY='og:title'><meta property='OG:DESCRIPTION' content='Fallback description'>"));
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test");
        Equal("Fallback & title", result.Title);
        Equal("Fallback description", result.Description);
    }

    private static async Task NoMetadata()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/favicon.ico" ? Icon() : Html("<html><body>Hello</body></html>"));
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test");
        Equal(null, result.Title);
        Equal(null, result.Description);
        Require(result.Favicon != null, "Missing title must not prevent fetching the icon.");
    }

    private static async Task FaviconFailure()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/page" ?
            Html("<title>Retained</title><meta name=description content='Also retained'><link rel=icon href='/broken'>") : Missing());
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page");
        Equal("Retained", result.Title);
        Equal("Also retained", result.Description);
        Equal(null, result.Favicon);
        Require(result.Error != null, "Icon failure must be reported without dropping the page data.");
    }

    private static async Task SvgFallback()
    {
        var paths = new List<string>();
        using var client = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return path == "/favicon.ico" ? Icon() : Html("<title>Fallback</title><link rel=icon type='image/svg+xml' href='/vector'><link rel=icon href='/image.svg'>");
        });
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page");
        Require(result.Favicon != null, "Fallback must supply a bitmap icon.");
        Require(paths.SequenceEqual(new[] { "/page", "/favicon.ico" }), "Unsupported SVGs must be skipped.");
    }

    private static async Task BrokenIconFallback()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/page" => Html("<title>Title</title><link rel=icon href=/missing.png>"),
            "/favicon.ico" => Icon(),
            _ => Missing()
        });
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page");
        Require(result.Favicon != null, "Failed linked icon must try the conventional fallback.");
        Equal(null, result.Error);
    }

    private static async Task IgnoreScriptsAndComments()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/favicon.ico" ? Icon() :
            Html("<!-- <title>Comment title</title> --><script>var sample='<meta name=description content=Fake><title>Script title</title>';</script><title>Actual title</title><meta name=description content=Actual>"));
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page");
        Equal("Actual title", result.Title);
        Equal("Actual", result.Description);
    }

    private static async Task InvalidIcon()
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/page" ? Html("<title>Safe title</title>") :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<svg><script>unsafe</script></svg>", Encoding.UTF8, "image/png") });
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page");
        Equal("Safe title", result.Title);
        Equal(null, result.Favicon);
        Require(result.Error != null, "Unsupported bytes must be rejected regardless of the MIME type.");
    }

    private static async Task PageFailures()
    {
        using var client = Client(_ => Missing());
        var fetcher = new UrlMetadataFetcher(client);
        var http = await fetcher.FetchAsync("https://example.test/page");
        Require(http.Error != null && http.Title == null, "HTTP failure must return an empty editable result.");
        var invalid = await fetcher.FetchAsync("file:///c:/secret");
        Require(invalid.Error != null, "Non-HTTP URLs must be rejected.");
    }

    private static async Task RedirectLimits()
    {
        var calls = 0;
        using var loopClient = Client(_ => { calls++; return Redirect("/loop"); });
        var loop = await new UrlMetadataFetcher(loopClient).FetchAsync("https://example.test/loop");
        Require(loop.Error != null && calls == 6, "Redirect loops must stop after five redirects.");
        calls = 0;
        using var unsafeClient = Client(_ => { calls++; return Redirect("file:///c:/secret"); });
        var unsafeResult = await new UrlMetadataFetcher(unsafeClient).FetchAsync("https://example.test/page");
        Require(unsafeResult.Error != null && calls == 1, "Non-HTTP redirects must never be requested.");
    }

    private static async Task ChineseCharsets()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var html = "<meta charset=gbk><title>工程资料</title>";
        using var metaClient = Client(request => request.RequestUri!.AbsolutePath == "/favicon.ico" ? Icon() :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.GetEncoding("gbk").GetBytes(html)) });
        Equal("工程资料", (await new UrlMetadataFetcher(metaClient).FetchAsync("https://example.test/page")).Title);
        using var headerClient = Client(request => request.RequestUri!.AbsolutePath == "/favicon.ico" ? Icon() :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<title>工程资料</title>", Encoding.GetEncoding("gbk"), "text/html") });
        Equal("工程资料", (await new UrlMetadataFetcher(headerClient).FetchAsync("https://example.test/page")).Title);
    }

    private static async Task HtmlDeclaredLimit()
    {
        var calls = 0;
        using var client = Client(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[UrlMetadataFetcher.MaxHtmlBytes + 1]) }; });
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page");
        Require(result.Error != null && result.Title == null && calls == 1, "Oversized HTML must stop the workflow immediately.");
    }

    private static async Task HtmlStreamingLimit()
    {
        var stream = new CountingStream(UrlMetadataFetcher.MaxHtmlBytes * 2);
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page");
        Require(result.Error != null, "Chunked oversized HTML must be rejected.");
        Equal(UrlMetadataFetcher.MaxHtmlBytes + 1, stream.BytesRead);
    }

    private static async Task IconLimit()
    {
        var bytes = new byte[ResourceUrls.MaxFaviconBytes + 1];
        Png.CopyTo(bytes, 0);
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/page" ? Html("<title>Retained</title>") :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page");
        Equal("Retained", result.Title);
        Require(result.Favicon == null && result.Error != null, "Oversized favicon must not be returned.");
    }

    private static async Task PageTimeout()
    {
        using var client = new HttpClient(new MockHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Html(""); }));
        var started = DateTime.UtcNow;
        var result = await new UrlMetadataFetcher(client, TimeSpan.FromMilliseconds(100)).FetchAsync("https://example.test/page");
        Require(result.Error?.Contains("超时", StringComparison.Ordinal) == true, "Timeout must return useful failure metadata.");
        Require(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "Timeout must bound the request duration.");
    }

    private static async Task IconStreamingLimit()
    {
        var stream = new CountingStream(ResourceUrls.MaxFaviconBytes * 2);
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/page" ? Html("<title>Retained</title>") :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var result = await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page");
        Equal("Retained", result.Title);
        Require(result.Favicon == null && result.Error != null, "Chunked oversized icon must be rejected.");
        Equal(ResourceUrls.MaxFaviconBytes + 1, stream.BytesRead);
    }

    private static async Task IconTimeout()
    {
        using var client = new HttpClient(new MockHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/page") return Html("<title>Before timeout</title>");
            await Task.Delay(Timeout.Infinite, token);
            return Icon();
        }));
        var result = await new UrlMetadataFetcher(client, TimeSpan.FromMilliseconds(100)).FetchAsync("https://example.test/page");
        Equal("Before timeout", result.Title);
        Require(result.Error?.Contains("超时", StringComparison.Ordinal) == true, "Favicon shares the total page time budget.");
    }

    private static async Task CallerCancellation()
    {
        var transportCancelled = false;
        using var client = new HttpClient(new MockHandler(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { transportCancelled = true; throw; }
            return Html("");
        }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        try { await new UrlMetadataFetcher(client).FetchAsync("https://example.test/page", cancellation.Token); throw new Exception("Caller cancellation was swallowed."); }
        catch (OperationCanceledException) { Require(transportCancelled, "Cancellation must reach HTTP transport."); }
    }

    private static Task DraftManualEdits()
    {
        var draft = new UrlEditorDraft();
        draft.SetTarget("https://example.test");
        draft.EditAlias("My title");
        Require(draft.Apply(draft.Revision, draft.Target, new WebsiteMetadata("Fetched title", "Fetched description", Png, null)), "Current result must apply.");
        Equal("My title", draft.Alias);
        Equal("Fetched description", draft.Description);
        draft.EditAlias("");
        draft.EditDescription("");
        draft.Apply(draft.Revision, draft.Target, new WebsiteMetadata("Overwrite", "Overwrite", null, null));
        Equal("", draft.Alias);
        Equal("", draft.Description);
        return Task.CompletedTask;
    }

    private static Task DraftStaleResponses()
    {
        var draft = new UrlEditorDraft();
        draft.SetTarget("https://a.test");
        var oldRevision = draft.Revision;
        draft.SetTarget("https://b.test");
        draft.SetTarget("https://a.test");
        Require(!draft.Apply(oldRevision, "https://a.test", new WebsiteMetadata("Stale", "Stale", Png, null)), "Returning to the old URL must still invalidate its old request.");
        Equal("", draft.Alias);
        Equal(null, draft.Favicon);
        draft.EditAlias("Manual remains");
        draft.SetTarget("https://c.test");
        Equal("Manual remains", draft.Alias);
        return Task.CompletedTask;
    }

    private static Task DraftExistingFields()
    {
        var draft = new UrlEditorDraft(Item("", ""));
        draft.Apply(draft.Revision, draft.Target, new WebsiteMetadata("Fetched", "Fetched", Png, null));
        Equal("", draft.Alias);
        Equal("", draft.Description);
        return Task.CompletedTask;
    }

    private static Task DraftFavicon()
    {
        var draft = new UrlEditorDraft(Item("Saved", "Saved", Png));
        draft.Apply(draft.Revision, draft.Target, new WebsiteMetadata(null, null, null, "Failure"));
        Require(draft.Favicon?.SequenceEqual(Png) == true, "Same-URL failure must preserve cached favicon.");
        draft.SetTarget("https://other.test");
        Equal(null, draft.Favicon);
        return Task.CompletedTask;
    }

    private static ResourceItem Item(string alias, string description, byte[]? favicon = null) =>
        new("id", ResourceUrls.Type, "https://example.test", alias, description, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            null, 0, false, [], null, favicon);
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> action) =>
        new(new MockHandler((request, _) => Task.FromResult(action(request))));
    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Icon() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
    private static HttpResponseMessage Missing() => new(HttpStatusCode.NotFound);
    private static HttpResponseMessage Redirect(string target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', found '{actual}'.");
    }

    private sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await action(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class CountingStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, length - BytesRead);
            buffer.AsSpan(offset, read).Fill((byte)'a');
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..read].Fill((byte)'a');
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
