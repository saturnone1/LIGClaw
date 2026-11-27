namespace LIGClaw.Application.Mail;

public static class MailAddressPolicy
{
    public const int MaximumAddressCharacters = 254;

    public static bool IsValidAsciiMailbox(string? value)
    {
        if (value is null || value.Length is < 3 or > MaximumAddressCharacters ||
            value.Any(character => character > 0x7f))
            return false;

        var separator = value.IndexOf('@');
        if (separator is <= 0 || separator != value.LastIndexOf('@') || separator == value.Length - 1)
            return false;

        var local = value.AsSpan(0, separator);
        var domain = value.AsSpan(separator + 1);
        if (local.Length > 64 || domain.Length > 253 || domain[^1] == '.' ||
            local[0] == '.' || local[^1] == '.' || local.IndexOf("..", StringComparison.Ordinal) >= 0 ||
            local.IndexOfAnyExcept(
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$%&'*+-/=?^_`{|}~.") >= 0)
            return false;

        var labelStart = 0;
        while (labelStart < domain.Length)
        {
            var remainder = domain[labelStart..];
            var dot = remainder.IndexOf('.');
            var label = dot < 0 ? remainder : remainder[..dot];
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.IndexOfAnyExcept(
                    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-") >= 0)
                return false;
            if (dot < 0) break;
            labelStart += dot + 1;
        }
        return true;
    }
}
