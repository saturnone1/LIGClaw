using System.Xml.Linq;

namespace LIGClaw.Desktop.Tests;

public sealed class Phase5PackagingTests
{
    [Fact]
    public void ManifestDeclaresFullTrustDesktopAppAndDisabledStartupTask()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "packaging", "AppxManifest.xml.template"));
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
        XNamespace restricted = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

        var application = Assert.Single(document.Descendants(foundation + "Application"));
        Assert.Equal("LIGClaw.Desktop.exe", application.Attribute("Executable")?.Value);
        Assert.Equal("Windows.FullTrustApplication", application.Attribute("EntryPoint")?.Value);

        var startupTask = Assert.Single(document.Descendants(desktop + "StartupTask"));
        Assert.Equal("LIGClawStartup", startupTask.Attribute("TaskId")?.Value);
        Assert.Equal("false", startupTask.Attribute("Enabled")?.Value);
        Assert.Contains(document.Descendants(restricted + "Capability"),
            capability => capability.Attribute("Name")?.Value == "runFullTrust");
        Assert.Contains(document.Descendants(foundation + "DeviceCapability"),
            capability => capability.Attribute("Name")?.Value == "microphone");
    }

    [Fact]
    public void AppInstallerChecksOnLaunchAndAllowsVersionTransitions()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "packaging", "LIGClaw.appinstaller.template"));
        XNamespace appInstaller = "http://schemas.microsoft.com/appx/appinstaller/2021";

        var onLaunch = Assert.Single(document.Descendants(appInstaller + "OnLaunch"));
        Assert.Equal("0", onLaunch.Attribute("HoursBetweenUpdateChecks")?.Value);
        Assert.Equal("false", onLaunch.Attribute("UpdateBlocksActivation")?.Value);
        Assert.Equal("true", Assert.Single(document.Descendants(appInstaller + "ForceUpdateFromAnyVersion")).Value);
    }

    [Fact]
    public void BuildAndLifecycleScriptsPreserveReleaseSafetyRequirements()
    {
        var scripts = Path.Combine(FindRepositoryRoot(), "scripts");
        var build = File.ReadAllText(Path.Combine(scripts, "build-msix.ps1"));
        var development = File.ReadAllText(Path.Combine(scripts, "test-msix-development.ps1"));
        var lifecycle = File.ReadAllText(Path.Combine(scripts, "test-msix-lifecycle.ps1"));

        Assert.Contains("--self-contained true", build, StringComparison.Ordinal);
        Assert.Contains("'node.exe'", build, StringComparison.Ordinal);
        Assert.Contains("'Wpf.Ui.dll'", build, StringComparison.Ordinal);
        Assert.Contains("'ThirdPartyNotices.txt'", build, StringComparison.Ordinal);
        Assert.Contains("SignTool", build, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sha256", build, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Code Signing EKU", build, StringComparison.Ordinal);
        Assert.Contains("SubjectName.Name", build, StringComparison.Ordinal);
        Assert.Contains("StringComparison]::OrdinalIgnoreCase", build, StringComparison.Ordinal);
        Assert.Contains("certificate chain", build, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CertificateStoreLocation", build, StringComparison.Ordinal);
        Assert.Contains("Add-AppxPackage -Path $initial", lifecycle, StringComparison.Ordinal);
        Assert.Contains("Add-AppxPackage -Path $update", lifecycle, StringComparison.Ordinal);
        Assert.Contains("Remove-AppxPackage", lifecycle, StringComparison.Ordinal);
        Assert.Contains("AddSeconds(15)", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("New-SelfSignedCertificate", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("TrustedPeople", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentUser\\Root", lifecycle, StringComparison.Ordinal);
        Assert.Contains("CertificateThumbprint", development, StringComparison.Ordinal);
        Assert.Contains("must already be trusted", development, StringComparison.Ordinal);
        Assert.DoesNotContain("New-SelfSignedCertificate", development, StringComparison.Ordinal);
        Assert.DoesNotContain("X509Store", development, StringComparison.Ordinal);
        Assert.DoesNotContain("TrustedPeople", development, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentUser\\Root", development, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LIGClaw.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("LIGClaw repository root를 찾지 못했습니다.");
    }
}
