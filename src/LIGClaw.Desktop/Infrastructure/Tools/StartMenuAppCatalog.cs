using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record RegisteredAppEntry(
    string AppId,
    string DisplayName,
    string LaunchTarget);

internal interface IRegisteredAppCatalog
{
    RegisteredAppEntry? Resolve(string appName);
}

internal interface IInstalledAppSearch
{
    IReadOnlyList<RegisteredAppEntry> Search(string query, int maximumResults, out bool truncated);
}

internal interface IRegisteredAppLauncher
{
    Task LaunchAsync(RegisteredAppEntry app, CancellationToken cancellationToken);
}

internal sealed class StartMenuAppCatalog : IRegisteredAppCatalog, IInstalledAppSearch
{
    private const int MaximumShortcutCount = 5_000;
    private readonly IReadOnlyList<string> _roots;
    private readonly Lazy<IReadOnlyList<RegisteredAppEntry>> _packagedApps;

    public StartMenuAppCatalog(
        IEnumerable<string>? roots = null,
        Func<IReadOnlyList<RegisteredAppEntry>>? packagedApps = null)
    {
        _roots = (roots ??
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        ])
        .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
        var loadPackagedApps = packagedApps ?? (roots is null ? EnumeratePackagedApps : () => []);
        _packagedApps = new Lazy<IReadOnlyList<RegisteredAppEntry>>(
            loadPackagedApps,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RegisteredAppEntry? Resolve(string appName)
    {
        var normalizedName = appName.Trim();
        if (normalizedName.Length == 0) return null;
        return EnumerateApps().FirstOrDefault(app =>
            StringComparer.OrdinalIgnoreCase.Equals(app.DisplayName, normalizedName));
    }

    public IReadOnlyList<RegisteredAppEntry> Search(string query, int maximumResults, out bool truncated)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResults, 1);
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length == 0)
        {
            truncated = false;
            return [];
        }
        var matches = EnumerateApps()
            .Where(app => app.DisplayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .GroupBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(app => StringComparer.OrdinalIgnoreCase.Equals(app.DisplayName, normalizedQuery))
            .ThenBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximumResults + 1)
            .ToArray();
        truncated = matches.Length > maximumResults;
        return matches.Take(maximumResults).ToArray();
    }

    private IEnumerable<RegisteredAppEntry> EnumerateApps()
    {
        foreach (var root in _roots)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 8,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", options)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumShortcutCount)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                var displayName = Path.GetFileNameWithoutExtension(shortcut);
                var fullPath = Path.GetFullPath(shortcut);
                var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                yield return new RegisteredAppEntry(fullPath, displayName, fullPath);
            }
        }
        foreach (var app in _packagedApps.Value) yield return app;
    }

    private static IReadOnlyList<RegisteredAppEntry> EnumeratePackagedApps()
    {
        object? shell = null;
        object? folder = null;
        object? items = null;
        var results = new List<RegisteredAppEntry>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null || Activator.CreateInstance(shellType) is not { } createdShell) return [];
            shell = createdShell;
            folder = shellType.InvokeMember(
                "NameSpace",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                shell,
                ["shell:AppsFolder"]);
            if (folder is null) return [];
            items = folder.GetType().InvokeMember(
                "Items",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                folder,
                null);
            if (items is null) return [];
            var count = Convert.ToInt32(items.GetType().InvokeMember(
                "Count",
                System.Reflection.BindingFlags.GetProperty,
                binder: null,
                items,
                null), System.Globalization.CultureInfo.InvariantCulture);
            for (var index = 0; index < Math.Min(count, MaximumShortcutCount); index++)
            {
                object? item = null;
                try
                {
                    item = items.GetType().InvokeMember(
                        "Item",
                        System.Reflection.BindingFlags.InvokeMethod,
                        binder: null,
                        items,
                        [index]);
                    if (item is null) continue;
                    var name = item.GetType().InvokeMember(
                        "Name", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                    var appUserModelId = item.GetType().InvokeMember(
                        "Path", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !IsSafeAppUserModelId(appUserModelId)) continue;
                    results.Add(new RegisteredAppEntry(
                        $"apps-folder:{appUserModelId}",
                        name.Trim(),
                        $"shell:AppsFolder\\{appUserModelId}"));
                }
                catch (Exception exception) when (exception is COMException or InvalidOperationException)
                {
                    // One broken shell item must not hide the rest of the registered app catalog.
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NotSupportedException)
        {
            return results;
        }
        finally
        {
            ReleaseComObject(items);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
        return results;
    }

    private static bool IsSafeAppUserModelId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value.Count(character => character == '!') == 1 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '!');

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.FinalReleaseComObject(value);
    }
}

internal sealed class ShellRegisteredAppLauncher : IRegisteredAppLauncher
{
    public Task LaunchAsync(RegisteredAppEntry app, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = app.LaunchTarget,
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }
}
