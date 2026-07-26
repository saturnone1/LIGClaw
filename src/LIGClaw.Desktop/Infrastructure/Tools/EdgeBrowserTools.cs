using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record EdgeBrowserSession(
    string Handle,
    string WindowId,
    int ProcessId,
    long ProcessStartedAtUtcTicks,
    Uri RequestedUri,
    DateTimeOffset ExpiresAtUtc);

internal sealed record EdgeBrowserSnapshot(
    EdgeBrowserSession Session,
    string Title,
    IReadOnlyList<UiAutomationElementSummary> Elements,
    bool Truncated);

internal interface IEdgeBrowserSessionService
{
    Task<EdgeBrowserSession> OpenAsync(Uri uri, CancellationToken cancellationToken);
    Task<EdgeBrowserSession?> GetSessionAsync(string handle, CancellationToken cancellationToken);
    Task<EdgeBrowserSnapshot?> SnapshotAsync(
        string handle,
        string? query,
        int maximumElements,
        CancellationToken cancellationToken);
}

internal interface IEdgeProcessLauncher
{
    void Launch(string executablePath, string profilePath, Uri uri);
}

internal sealed class EdgeProcessLauncher : IEdgeProcessLauncher
{
    public void Launch(string executablePath, string profilePath, Uri uri)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add($"--user-data-dir={profilePath}");
        startInfo.ArgumentList.Add("--profile-directory=Default");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--disable-sync");
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(uri.AbsoluteUri);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Edge를 실행하지 못했습니다.");
    }
}

internal sealed class EdgeBrowserSessionService(
    IWindowCatalog windows,
    IUiAutomationService automation,
    IEdgeProcessLauncher launcher,
    Func<string?>? locateEdge = null,
    Func<DateTimeOffset>? utcNow = null,
    Func<string>? profilePath = null)
    : IEdgeBrowserSessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, EdgeBrowserSession> _sessions = new(StringComparer.Ordinal);
    private readonly Func<string?> _locateEdge = locateEdge ?? FindEdgeExecutable;
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<string> _profilePath = profilePath ?? (() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LIGClaw", "Browser", "EdgeProfile"));

    public async Task<EdgeBrowserSession> OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        var safeUri = BoundedWebContentClient.ValidateUri(uri);
        var executable = _locateEdge();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Microsoft Edge 실행 파일을 찾을 수 없습니다.");
        var previous = (await windows.ListWindowsAsync(cancellationToken).ConfigureAwait(false))
            .Where(IsEdgeWindow)
            .Select(window => window.WindowId)
            .ToHashSet(StringComparer.Ordinal);
        var profilePath = _profilePath();
        Directory.CreateDirectory(profilePath);
        launcher.Launch(executable, profilePath, safeUri);

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = (await windows.ListWindowsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(window => IsEdgeWindow(window) && !previous.Contains(window.WindowId));
            if (candidate is not null)
            {
                var identity = await automation.ResolveWindowAsync(candidate.WindowId, cancellationToken).ConfigureAwait(false);
                if (identity is not null && StringComparer.OrdinalIgnoreCase.Equals(identity.ProcessName, "msedge"))
                {
                    var session = new EdgeBrowserSession(
                        Guid.NewGuid().ToString("N"), identity.WindowId, identity.ProcessId,
                        identity.ProcessStartedAtUtcTicks, safeUri, _utcNow().Add(SessionLifetime));
                    _sessions[session.Handle] = session;
                    TrimExpired();
                    return session;
                }
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("전용 Edge 창이 준비되지 않았습니다.");
    }

    public async Task<EdgeBrowserSession?> GetSessionAsync(string handle, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(handle, out var session) || session.ExpiresAtUtc <= _utcNow())
        {
            _sessions.TryRemove(handle, out _);
            return null;
        }
        var identity = await automation.ResolveWindowAsync(session.WindowId, cancellationToken).ConfigureAwait(false);
        if (identity is null || identity.ProcessId != session.ProcessId ||
            identity.ProcessStartedAtUtcTicks != session.ProcessStartedAtUtcTicks ||
            !StringComparer.OrdinalIgnoreCase.Equals(identity.ProcessName, "msedge"))
        {
            _sessions.TryRemove(handle, out _);
            return null;
        }
        return session;
    }

    public async Task<EdgeBrowserSnapshot?> SnapshotAsync(
        string handle,
        string? query,
        int maximumElements,
        CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(handle, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        var inspection = await automation.InspectAsync(
            session.WindowId, query, maximumElements, cancellationToken).ConfigureAwait(false);
        if (inspection is null || !StringComparer.OrdinalIgnoreCase.Equals(inspection.ProcessName, "msedge")) return null;
        return new EdgeBrowserSnapshot(session, inspection.Title, inspection.Elements, inspection.Truncated);
    }

    internal static string? FindEdgeExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsEdgeWindow(WindowCatalogEntry window) =>
        StringComparer.OrdinalIgnoreCase.Equals(window.ProcessName, "msedge");

    private void TrimExpired()
    {
        var now = _utcNow();
        foreach (var session in _sessions.Where(item => item.Value.ExpiresAtUtc <= now))
            _sessions.TryRemove(session.Key, out _);
    }
}

internal sealed class BrowserOpenTool(IEdgeBrowserSessionService sessions) : IWindowsToolAdapter
{
    public string Name => "browser.open.v1";
    public string Risk => "R3";
    public WindowsCapability RequiredCapabilities => WindowsCapability.WebAccess | WindowsCapability.UiAutomation;
    public int Priority => 0;
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryRead(input, out var uri, out var reason)) return null;
        return new WindowsToolApprovalPrompt(
            "전용 Edge에서 Web 페이지를 열까요?",
            $"개인 Edge와 분리된 LIGClaw 프로필로 {uri.Host}을 엽니다.",
            $"URL: {uri}\n요청 이유: {reason}\n\n개인 Edge 쿠키·방문 기록과 공유하지 않습니다. 다운로드·업로드·인증 입력은 수행하지 않습니다.",
            "R3");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var uri, out _)) return Failure("브라우저 URL 또는 요청 이유가 올바르지 않습니다.");
        var session = await sessions.OpenAsync(uri, cancellationToken).ConfigureAwait(false);
        return new WindowsToolExecutionResult(true, new Dictionary<string, object?>
        {
            ["browserHandle"] = session.Handle,
            ["windowId"] = session.WindowId,
            ["requestedUrl"] = session.RequestedUri.ToString(),
            ["expiresAtUtc"] = session.ExpiresAtUtc,
        }, ActivitySummary: "전용 Edge 프로필에서 페이지를 열었어요.");
    }

    private static bool TryRead(IReadOnlyDictionary<string, object?> input, out Uri uri, out string reason)
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

internal sealed class BrowserSnapshotTool(IEdgeBrowserSessionService sessions) : IWindowsToolAdapter
{
    public string Name => "browser.snapshot.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.WebAccess | WindowsCapability.UiAutomation;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryRead(input, out var handle, out var query, out _, out var reason)) return null;
        var session = sessions.GetSessionAsync(handle, CancellationToken.None).GetAwaiter().GetResult();
        if (session is null) return null;
        return new WindowsToolApprovalPrompt(
            "전용 Edge 페이지의 내용을 읽을까요?",
            $"{session.RequestedUri.Host} 창의 접근성 텍스트를 읽습니다.",
            $"처음 연 URL: {session.RequestedUri}\n필터: {query ?? "없음"}\n요청 이유: {reason}\n\n최대 50개 요소만 읽으며 반환 내용은 신뢰할 수 없는 외부 콘텐츠입니다.");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        if (!TryRead(input, out var handle, out var query, out var maximum, out _))
            return Failure("브라우저 handle 또는 snapshot 조건이 올바르지 않습니다.");
        var snapshot = await sessions.SnapshotAsync(handle, query, maximum, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return Failure("브라우저 handle이 만료됐거나 전용 Edge 창이 닫혔습니다.");
        var elements = snapshot.Elements.Select(element => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["name"] = element.Name,
            ["controlType"] = element.ControlType,
            ["isEnabled"] = element.IsEnabled,
            ["isOffscreen"] = element.IsOffscreen,
        }).ToArray();
        return new WindowsToolExecutionResult(true, new Dictionary<string, object?>
        {
            ["browserHandle"] = snapshot.Session.Handle,
            ["windowId"] = snapshot.Session.WindowId,
            ["title"] = snapshot.Title,
            ["requestedUrl"] = snapshot.Session.RequestedUri.ToString(),
            ["elements"] = elements,
            ["truncated"] = snapshot.Truncated,
        }, ActivitySummary: "전용 Edge 페이지의 접근성 snapshot을 읽었어요.");
    }

    private static bool TryRead(
        IReadOnlyDictionary<string, object?> input,
        out string handle,
        out string? query,
        out int maximum,
        out string reason)
    {
        handle = string.Empty;
        query = null;
        reason = string.Empty;
        maximum = 30;
        if (!ToolInputReader.HasOnlyKeys(input, "browserHandle", "query", "maxElements", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "browserHandle", 32, out handle) ||
            handle.Length != 32 || !handle.All(char.IsAsciiHexDigitLower) ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out reason)) return false;
        if (input.ContainsKey("query") && !ToolInputReader.TryGetRequiredString(input, "query", 128, out query!)) return false;
        if (input.TryGetValue("maxElements", out var raw))
        {
            maximum = raw switch
            {
                int value => value,
                long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
                JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var value) => value,
                _ => -1,
            };
            if (maximum is < 1 or > 50) return false;
        }
        return true;
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
