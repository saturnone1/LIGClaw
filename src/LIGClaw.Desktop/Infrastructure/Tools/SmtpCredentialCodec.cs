using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal static class SmtpCredentialCodec
{
    internal const string Prefix = "ligclaw-smtp-auth-v1:";
    private const int LengthFrameBytes = 4;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly int MaximumFrameBytes =
        LengthFrameBytes + SmtpWireCredential.MaximumUsernameBytes +
        SmtpWireCredential.MaximumPasswordBytes;
    internal static readonly int MaximumEncodedCharacters =
        Prefix.Length + 4 * ((MaximumFrameBytes + 2) / 3);

    public static string Encode(string username, string password)
    {
        if (!SmtpWireCredential.IsValidUsername(username) ||
            !SmtpWireCredential.IsValidPassword(password))
            throw new ArgumentException("SMTP credential 형식이 올바르지 않습니다.");

        var usernameBytes = Encoding.ASCII.GetBytes(username);
        var passwordBytes = StrictUtf8.GetBytes(password);
        var frame = new byte[checked(LengthFrameBytes + usernameBytes.Length + passwordBytes.Length)];
        try
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)usernameBytes.Length));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), checked((ushort)passwordBytes.Length));
            usernameBytes.CopyTo(frame, LengthFrameBytes);
            passwordBytes.CopyTo(frame, LengthFrameBytes + usernameBytes.Length);
            return Prefix + Convert.ToBase64String(frame);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(frame);
        }
    }

    public static bool TryDecode(string? value, out SmtpWireCredential? credential)
    {
        credential = null;
        if (value is null || value.Length > MaximumEncodedCharacters ||
            !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var encoded = value[Prefix.Length..];
        if (encoded.Length == 0 || encoded.Length % 4 != 0) return false;
        var frame = new byte[encoded.Length / 4 * 3];
        try
        {
            if (!Convert.TryFromBase64String(encoded, frame, out var written) ||
                written < LengthFrameBytes || written > MaximumFrameBytes ||
                !StringComparer.Ordinal.Equals(
                    encoded,
                    Convert.ToBase64String(frame, 0, written)))
                return false;

            var usernameLength = BinaryPrimitives.ReadUInt16BigEndian(frame);
            var passwordLength = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            if (usernameLength is < 1 or > SmtpWireCredential.MaximumUsernameBytes ||
                passwordLength is < 1 or > SmtpWireCredential.MaximumPasswordBytes ||
                written != LengthFrameBytes + usernameLength + passwordLength)
                return false;

            var usernameBytes = frame.AsSpan(LengthFrameBytes, usernameLength);
            if (usernameBytes.ContainsAnyExceptInRange((byte)0x21, (byte)0x7e)) return false;
            var username = Encoding.ASCII.GetString(usernameBytes);
            var password = StrictUtf8.GetString(
                frame,
                LengthFrameBytes + usernameLength,
                passwordLength);
            if (!SmtpWireCredential.IsValidUsername(username) ||
                !SmtpWireCredential.IsValidPassword(password))
                return false;
            credential = new SmtpWireCredential(username, password);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or EncoderFallbackException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
        }
    }

    public static bool IsValid(string? value)
    {
        if (!TryDecode(value, out var credential)) return false;
        credential!.Dispose();
        return true;
    }
}
