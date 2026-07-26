namespace LIGClaw.Desktop.Infrastructure.Shell;

internal static class UiExceptionPolicy
{
    public static bool CanRecover(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is not (OutOfMemoryException or AccessViolationException or AppDomainUnloadedException or BadImageFormatException);
    }
}
