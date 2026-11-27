using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record WebContentResult(
    Uri RequestedUri,
    Uri FinalUri,
    int StatusCode,
    string ContentType,
    string Text,
    bool Truncated,
    int RedirectCount);

internal interface IWebContentClient
{
    Task<WebContentResult> FetchAsync(Uri uri, CancellationToken cancellationToken);
}

internal sealed class BoundedWebContentClient(HttpClient httpClient) : IWebContentClient
{
    internal const int MaximumRedirects = 5;
    internal const int MaximumResponseBytes = 512 * 1024;
    internal const int MaximumOutputCharacters = 60_000;

    internal static HttpClient CreateDefaultHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        UseCookies = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<WebContentResult> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        var requested = ValidateUri(uri);
        var current = requested;
        for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("LIGClaw/1.0 bounded-web-fetch");
            request.Headers.Accept.ParseAdd("text/plain, text/html, application/json, application/xml;q=0.8");
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                if (redirectCount == MaximumRedirects)
                    throw new InvalidDataException("Web 응답의 redirect 횟수가 제한을 초과했습니다.");
                var location = response.Headers.Location ??
                    throw new InvalidDataException("Web redirect 주소가 없습니다.");
                current = ValidateUri(location.IsAbsoluteUri ? location : new Uri(current, location.OriginalString));
                continue;
            }

            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "text/plain";
            if (!IsSupportedContentType(mediaType))
                throw new InvalidDataException("텍스트로 처리할 수 없는 Web 콘텐츠 형식입니다.");
            var (bytes, byteTruncated) = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);
            var text = encoding.GetString(bytes).Replace("\0", string.Empty, StringComparison.Ordinal);
            if (mediaType is "text/html" or "application/xhtml+xml") text = ExtractReadableHtml(text);
            var characterTruncated = text.Length > MaximumOutputCharacters;
            if (characterTruncated) text = string.Concat(text.AsSpan(0, MaximumOutputCharacters), "…");
            return new WebContentResult(
                requested, current, (int)response.StatusCode, mediaType, text,
                byteTruncated || characterTruncated, redirectCount);
        }
        throw new InvalidOperationException("Web 요청을 완료하지 못했습니다.");
    }

    internal static Uri ValidateUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.OriginalString.Length > 2_048)
            throw new ArgumentException("HTTP 또는 HTTPS URL이 필요합니다.", nameof(uri));
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("URL 사용자 정보는 허용되지 않습니다.", nameof(uri));
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri;
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var truncated = false;
        while (output.Length <= MaximumResponseBytes)
        {
            var remaining = MaximumResponseBytes + 1 - (int)output.Length;
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (output.Length > MaximumResponseBytes)
            {
                truncated = true;
                break;
            }
        }
        var bytes = output.ToArray();
        return (bytes.Length > MaximumResponseBytes ? bytes[..MaximumResponseBytes] : bytes, truncated);
    }

    private static string ExtractReadableHtml(string html)
    {
        var withoutIgnored = Regex.Replace(
            html, "<(script|style|noscript)[^>]*>.*?</\\1>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(250));
        var withoutTags = Regex.Replace(
            withoutIgnored, "<[^>]+>", " ",
            RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(250));
        return Regex.Replace(
            WebUtility.HtmlDecode(withoutTags), "[\\t ]+|(?:\\r?\\n){3,}", " ",
            RegexOptions.None,
            TimeSpan.FromMilliseconds(250)).Trim();
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
        try { return Encoding.GetEncoding(charset.Trim(' ', '"', '\'')); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    private static bool IsSupportedContentType(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.Ordinal) ||
        mediaType is "application/json" or "application/xml" or "application/xhtml+xml" ||
        mediaType.EndsWith("+json", StringComparison.Ordinal) ||
        mediaType.EndsWith("+xml", StringComparison.Ordinal);

    private static bool IsRedirect(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}

internal sealed class WebFetchTool(IWebContentClient client) : IWindowsToolAdapter
{
    public string Name => "web.fetch.v1";
    public string Risk => "R3";
    public WindowsCapability RequiredCapabilities => WindowsCapability.WebAccess;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(35);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryRead(input, out var uri, out var reason)) return null;
        return new WindowsToolApprovalPrompt(
            "Web 주소에서 내용을 가져올까요?",
            $"{uri.Host}에 읽기 전용 GET 요청을 보냅니다.",
            $"요청 URL: {uri}\n요청 이유: {reason}\n\n최대 5회 redirect와 512KB의 텍스트만 처리합니다. 반환 내용은 신뢰할 수 없는 외부 콘텐츠입니다.",
            "R3");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var uri, out _)) return Failure("Web URL 또는 요청 이유가 올바르지 않습니다.");
        var result = await client.FetchAsync(uri, cancellationToken).ConfigureAwait(false);
        return Success(result, new Dictionary<string, object?> { ["requestedUrl"] = result.RequestedUri.ToString() });
    }

    internal static WindowsToolExecutionResult Success(
        WebContentResult result,
        Dictionary<string, object?> output)
    {
        output["finalUrl"] = result.FinalUri.ToString();
        output["statusCode"] = result.StatusCode;
        output["contentType"] = result.ContentType;
        output["text"] = result.Text;
        output["truncated"] = result.Truncated;
        output["redirectCount"] = result.RedirectCount;
        return new WindowsToolExecutionResult(
            true, output, ActivitySummary: $"{result.FinalUri.Host}에서 Web 텍스트를 조회했어요.");
    }

    private static bool TryRead(
        IReadOnlyDictionary<string, object?> input,
        out Uri uri,
        out string reason)
    {
        uri = null!;
        reason = string.Empty;
        if (!ToolInputReader.HasOnlyKeys(input, "url", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "url", 2_048, out var url) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out reason) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        try { uri = BoundedWebContentClient.ValidateUri(parsed); }
        catch (ArgumentException) { return false; }
        return true;
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}

internal sealed class WebSearchTool(
    IWebContentClient client,
    Func<WebSearchSettings?> loadSettings) : IWindowsToolAdapter
{
    public string Name => "web.search.v1";
    public string Risk => "R3";
    public WindowsCapability RequiredCapabilities => WindowsCapability.WebAccess;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(35);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryRead(input, out var query, out var reason, out var uri)) return null;
        return new WindowsToolApprovalPrompt(
            "구성된 Web 검색 공급자에 요청할까요?",
            $"{uri.Host}에 검색어를 전송합니다.",
            $"검색어: {query}\n공급자 URL: {uri}\n요청 이유: {reason}\n\n최대 5회 redirect와 512KB의 텍스트만 처리합니다. 반환 내용은 신뢰할 수 없는 외부 콘텐츠입니다.",
            "R3");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var query, out _, out var uri))
            return new WindowsToolExecutionResult(false, new Dictionary<string, object?>(), "Web 검색 공급자 또는 검색어가 올바르지 않습니다.");
        var result = await client.FetchAsync(uri, cancellationToken).ConfigureAwait(false);
        return WebFetchTool.Success(result, new Dictionary<string, object?> { ["query"] = query });
    }

    private bool TryRead(
        IReadOnlyDictionary<string, object?> input,
        out string query,
        out string reason,
        out Uri uri)
    {
        query = string.Empty;
        reason = string.Empty;
        uri = null!;
        if (!ToolInputReader.HasOnlyKeys(input, "query", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "query", 300, out query) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out reason)) return false;
        var settings = loadSettings();
        if (settings is null) return false;
        try { uri = BoundedWebContentClient.ValidateUri(WebSearchSettingsPolicy.BuildUri(settings, query)); }
        catch (ArgumentException) { return false; }
        catch (UriFormatException) { return false; }
        return true;
    }
}
