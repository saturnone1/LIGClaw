using System.IO;
using System.Runtime.InteropServices;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record ExplorerSelectedItem(string Path, string Name, bool IsDirectory);

internal sealed record ExplorerContextSnapshot(
    string FolderPath,
    IReadOnlyList<ExplorerSelectedItem> SelectedItems,
    bool SelectionTruncated);

internal interface IExplorerContextReader
{
    ExplorerContextSnapshot? ReadMostRecent();
}

internal sealed class WindowsExplorerContextReader : IExplorerContextReader
{
    private const uint NextWindow = 2;
    private const int MaximumSelectedItems = 20;

    public ExplorerContextSnapshot? ReadMostRecent()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return null;
        object? shellObject = null;
        object? windowsObject = null;
        try
        {
            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null) return null;
            dynamic shell = shellObject;
            windowsObject = shell.Windows();
            dynamic windows = windowsObject;
            var zOrder = ReadWindowZOrder();
            var candidates = new List<(int Rank, ExplorerContextSnapshot Context)>();
            var count = Convert.ToInt32(windows.Count, System.Globalization.CultureInfo.InvariantCulture);
            for (var index = 0; index < count; index++)
            {
                object? windowObject = null;
                try
                {
                    windowObject = windows.Item(index);
                    if (windowObject is null) continue;
                    dynamic window = windowObject;
                    var executable = Convert.ToString(window.FullName, System.Globalization.CultureInfo.InvariantCulture);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(executable), "explorer.exe")) continue;
                    var handle = new nint(Convert.ToInt64(window.HWND, System.Globalization.CultureInfo.InvariantCulture));
                    var context = ReadWindow(window);
                    if (context is null) continue;
                    candidates.Add((zOrder.TryGetValue(handle, out var rank) ? rank : int.MaxValue, context));
                }
                catch (COMException)
                {
                    // Explorer 창이 열리거나 닫히는 중이면 다음 창을 확인한다.
                }
                finally
                {
                    Release(windowObject);
                }
            }
            return candidates.OrderBy(candidate => candidate.Rank).Select(candidate => candidate.Context).FirstOrDefault();
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Release(windowsObject);
            Release(shellObject);
        }
    }

    private static ExplorerContextSnapshot? ReadWindow(dynamic window)
    {
        object? documentObject = null;
        object? folderObject = null;
        object? selfObject = null;
        object? selectedObject = null;
        try
        {
            documentObject = window.Document;
            if (documentObject is null) return null;
            dynamic document = documentObject;
            folderObject = document.Folder;
            if (folderObject is null) return null;
            dynamic folder = folderObject;
            selfObject = folder.Self;
            if (selfObject is null) return null;
            dynamic self = selfObject;
            var folderPath = SafeFileOperationService.NormalizeAbsolutePath(
                Convert.ToString(self.Path, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            if (folderPath is null || !Directory.Exists(folderPath)) return null;

            selectedObject = document.SelectedItems();
            dynamic selected = selectedObject;
            var selectedCount = Convert.ToInt32(selected.Count, System.Globalization.CultureInfo.InvariantCulture);
            var items = new List<ExplorerSelectedItem>();
            for (var index = 0; index < Math.Min(selectedCount, MaximumSelectedItems); index++)
            {
                object? itemObject = null;
                try
                {
                    itemObject = selected.Item(index);
                    if (itemObject is null) continue;
                    dynamic item = itemObject;
                    var itemPath = SafeFileOperationService.NormalizeAbsolutePath(
                        Convert.ToString(item.Path, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                    if (itemPath is null || (!File.Exists(itemPath) && !Directory.Exists(itemPath))) continue;
                    var name = Path.GetFileName(itemPath);
                    if (string.IsNullOrEmpty(name)) name = itemPath;
                    items.Add(new ExplorerSelectedItem(itemPath, name, Directory.Exists(itemPath)));
                }
                finally
                {
                    Release(itemObject);
                }
            }
            return new ExplorerContextSnapshot(folderPath, items, selectedCount > MaximumSelectedItems);
        }
        finally
        {
            Release(selectedObject);
            Release(selfObject);
            Release(folderObject);
            Release(documentObject);
        }
    }

    private static IReadOnlyDictionary<nint, int> ReadWindowZOrder()
    {
        var result = new Dictionary<nint, int>();
        var handle = NativeMethods.GetForegroundWindow();
        for (var rank = 0; handle != nint.Zero && rank < 512; rank++)
        {
            result.TryAdd(handle, rank);
            handle = NativeMethods.GetWindow(handle, NextWindow);
        }
        return result;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _ = Marshal.FinalReleaseComObject(value);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint windowHandle, uint command);
    }
}

internal sealed class ExplorerGetContextTool(IExplorerContextReader explorer) : IWindowsToolAdapter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ExplorerContextSnapshot> _prepared = new(StringComparer.Ordinal);

    public string Name => "explorer.get_context.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.ExplorerContext;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryReadReason(input, out var reason)) return null;
        var context = explorer.ReadMostRecent();
        if (context is null) return null;
        lock (_sync) _prepared[reason] = context;
        var selection = context.SelectedItems.Count == 0
            ? "선택한 항목 없음"
            : string.Join("\n", context.SelectedItems.Select(item => $"• {item.Path}"));
        return new WindowsToolApprovalPrompt(
            "파일 탐색기 문맥을 사용할까요?",
            $"현재 폴더와 선택 항목 {context.SelectedItems.Count}개를 모델에 전달합니다.",
            $"현재 폴더: {context.FolderPath}\n{selection}\n\n요청 이유: {reason}\n최대 20개 항목만 전달합니다.");
    }

    public Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadReason(input, out var reason))
            return Task.FromResult(Failure("파일 탐색기 문맥을 사용할 이유가 올바르지 않습니다."));
        ExplorerContextSnapshot? context;
        lock (_sync) _prepared.Remove(reason, out context);
        if (context is null) return Task.FromResult(Failure("승인한 파일 탐색기 문맥을 찾을 수 없어요."));
        var selected = context.SelectedItems.Select(item =>
            (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["path"] = item.Path,
                ["name"] = item.Name,
                ["isDirectory"] = item.IsDirectory,
            }).ToArray();
        return Task.FromResult(new WindowsToolExecutionResult(
            true,
            new Dictionary<string, object?>
            {
                ["folderPath"] = context.FolderPath,
                ["selectedItems"] = selected,
                ["selectionTruncated"] = context.SelectionTruncated,
            },
            ActivitySummary: $"파일 탐색기의 현재 폴더와 선택 항목 {selected.Length}개를 확인했어요."));
    }

    public void DiscardApproval(IReadOnlyDictionary<string, object?> input)
    {
        if (!TryReadReason(input, out var reason)) return;
        lock (_sync) _prepared.Remove(reason);
    }

    private static bool TryReadReason(IReadOnlyDictionary<string, object?> input, out string reason)
    {
        reason = string.Empty;
        return ToolInputReader.HasOnlyKeys(input, "reason") &&
               ToolInputReader.TryGetRequiredString(input, "reason", 160, out reason);
    }

    private static WindowsToolExecutionResult Failure(string message) =>
        new(false, new Dictionary<string, object?>(), message);
}
