using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using LIGClaw.Application.Connections;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record ConnectionHttpProbeResult(
    bool Succeeded,
    string? ErrorCode = null);

internal interface IConnectionHttpProbe
{
    Task<ConnectionHttpProbeResult> ProbeAsync(
        HttpConnectionProfile profile,
        CancellationToken cancellationToken);
}

internal sealed class ConnectionHttpProbe : IConnectionHttpProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<ConnectionProfileId, ConnectionCredentialKind, CancellationToken, Task<string?>> readCredential;
    private readonly HttpClient httpClient;

    public ConnectionHttpProbe(
        Func<ConnectionProfileId, ConnectionCredentialKind, CancellationToken, Task<string?>> readCredential)
    {
        this.readCredential = readCredential ?? throw new ArgumentNullException(nameof(readCredential));
        httpClient = CreateDefaultClient();
    }

    internal ConnectionHttpProbe(
        Func<ConnectionProfileId, ConnectionCredentialKind, CancellationToken, Task<string?>> readCredential,
        HttpMessageHandler handler)
    {
        this.readCredential = readCredential ?? throw new ArgumentNullException(nameof(readCredential));
        ArgumentNullException.ThrowIfNull(handler);
        httpClient = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<ConnectionHttpProbeResult> ProbeAsync(
        HttpConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile is null || !profile.Enabled ||
            profile.State != ConnectionProfileLifecycleState.Stable ||
            !HttpConnectionProfilePolicy.IsValid(profile) ||
            !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var target))
            return Failed("connection.probe.profile_invalid");

        string? bearer = null;
        if (profile.Authentication == ConnectionAuthenticationKind.Bearer)
        {
            try
            {
                bearer = await readCredential(
                    profile.Id,
                    ConnectionCredentialKind.BearerToken,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Failed("connection.probe.credential_unavailable");
            }
            if (!ConnectionHttpBearerTokenPolicy.IsValid(bearer))
                return Failed("connection.probe.credential_unavailable");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, target)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            // Keep the wire body empty while preventing SocketsHttpHandler from
            // replaying a bodyless GET after a premature response EOF.
            Content = new ByteArrayContent([]),
        };
        request.Headers.ConnectionClose = true;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        request.Headers.UserAgent.ParseAdd("LIGClaw-ConnectionProbe/1.0");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            if (statusCode is >= 300 and <= 399)
                return Failed("connection.probe.redirect_denied");
            return statusCode is >= 200 and <= 299
                ? new ConnectionHttpProbeResult(true)
                : Failed("connection.probe.status_rejected");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed("connection.probe.timeout");
        }
        catch
        {
            return Failed("connection.probe.transport_failed");
        }
    }

    internal static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
            MaxConnectionsPerServer = 1,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = ProbeTimeout,
        };
    }

    private static ConnectionHttpProbeResult Failed(string errorCode) =>
        new(false, errorCode);
}
