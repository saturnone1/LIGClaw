using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal sealed class WindowsConnectionCredentialStore : IConnectionCredentialStore
{
    internal Task<string?> ReadAsync(
        ConnectionProfileId profileId,
        ConnectionCredentialKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (kind == ConnectionCredentialKind.SmtpAuth)
            return Task.FromResult<string?>(null);
        return Task.FromResult(WindowsCredentialStore.Read(Target(profileId, kind)));
    }

    internal Task<SmtpWireCredential?> ReadSmtpAsync(
        ConnectionProfileId profileId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = WindowsCredentialStore.Read(Target(profileId, ConnectionCredentialKind.SmtpAuth));
        return Task.FromResult(
            SmtpCredentialCodec.TryDecode(value, out var credential) ? credential : null);
    }

    public Task<bool> ExistsAsync(
        ConnectionProfileId profileId,
        ConnectionCredentialKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Target(profileId, kind);
        if (kind != ConnectionCredentialKind.SmtpAuth)
            return Task.FromResult(WindowsCredentialStore.Exists(target));
        return Task.FromResult(SmtpCredentialCodec.IsValid(WindowsCredentialStore.Read(target)));
    }

    public Task ReplaceAsync(
        ConnectionProfileId profileId,
        ConnectionCredentialKind kind,
        string secret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(secret);
        var isValid = kind switch
        {
            ConnectionCredentialKind.BearerToken => ConnectionHttpBearerTokenPolicy.IsValid(secret),
            ConnectionCredentialKind.MailGatewayBearerToken => MailGatewayBearerTokenPolicy.IsValid(secret),
            ConnectionCredentialKind.SmtpAuth => SmtpCredentialCodec.IsValid(secret),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (!isValid)
            throw new ArgumentException("Connection 인증정보 형식이 올바르지 않습니다.", nameof(secret));
        WindowsCredentialStore.Write(Target(profileId, kind), secret);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        ConnectionProfileId profileId,
        ConnectionCredentialKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsCredentialStore.Delete(Target(profileId, kind));
        return Task.CompletedTask;
    }

    internal static string Target(ConnectionProfileId profileId, ConnectionCredentialKind kind)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        return kind switch
        {
            ConnectionCredentialKind.BearerToken =>
                $"LIGClaw/Connections/{profileId.Value}/BearerToken",
            ConnectionCredentialKind.MailGatewayBearerToken =>
                $"LIGClaw/MailGatewayConnections/{profileId.Value}/BearerToken",
            ConnectionCredentialKind.SmtpAuth =>
                $"LIGClaw/SmtpConnections/{profileId.Value}/AuthV1",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
