using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace LIGClaw.Desktop.Infrastructure.Shell;

internal static class WindowsPackageIdentity
{
    internal static bool IsAvailable()
    {
        try
        {
            _ = Package.Current.Id.FullName;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
