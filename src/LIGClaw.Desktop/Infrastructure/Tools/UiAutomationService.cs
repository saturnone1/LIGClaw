using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record UiAutomationElementSummary(
    string ElementId,
    string Name,
    string AutomationId,
    string ControlType,
    bool IsEnabled,
    bool IsOffscreen,
    bool SupportsInvoke,
    bool SupportsValue,
    bool SupportsTextInput);

internal sealed record UiAutomationInspection(
    string WindowId,
    string Title,
    string ProcessName,
    IReadOnlyList<UiAutomationElementSummary> Elements,
    bool Truncated);

internal sealed record UiAutomationWindowTarget(
    string WindowId,
    int ProcessId,
    long ProcessStartedAtUtcTicks,
    string Title,
    string ProcessName)
{
    public string Identity => string.Join('\n',
        WindowId,
        ProcessId.ToString(CultureInfo.InvariantCulture),
        ProcessStartedAtUtcTicks.ToString(CultureInfo.InvariantCulture),
        ProcessName,
        Title);
}

internal sealed record UiAutomationElementTarget(
    string ElementId,
    string WindowId,
    int ProcessId,
    long ProcessStartedAtUtcTicks,
    string WindowTitle,
    string ProcessName,
    string RuntimeId,
    string Name,
    string AutomationId,
    string ControlType,
    bool IsPassword,
    bool IsKeyboardFocusable,
    bool SupportsInvoke,
    bool SupportsValue)
{
    public string Identity => string.Join('\n',
        WindowId,
        ProcessId.ToString(CultureInfo.InvariantCulture),
        ProcessStartedAtUtcTicks.ToString(CultureInfo.InvariantCulture),
        RuntimeId,
        AutomationId,
        ControlType,
        Name);
}

internal enum UiAutomationActionStatus
{
    Succeeded,
    TargetUnavailable,
    IdentityChanged,
    Unsupported,
    PasswordField,
    FocusFailed,
    UserInputDetected,
    Failed,
}

internal interface IUiAutomationService
{
    Task<UiAutomationWindowTarget?> ResolveWindowAsync(string windowId, CancellationToken cancellationToken);
    Task<UiAutomationInspection?> InspectAsync(
        string windowId,
        string? query,
        int maximumElements,
        CancellationToken cancellationToken);
    Task<UiAutomationElementTarget?> ResolveAsync(string elementId, CancellationToken cancellationToken);
    Task<UiAutomationActionStatus> InvokeAsync(
        UiAutomationElementTarget expected,
        CancellationToken cancellationToken);
    Task<UiAutomationActionStatus> SetValueAsync(
        UiAutomationElementTarget expected,
        string value,
        CancellationToken cancellationToken);
    Task<UiAutomationActionStatus> SendTextAsync(
        UiAutomationElementTarget expected,
        string text,
        CancellationToken cancellationToken);
}

internal sealed class WindowsUiAutomationService : IUiAutomationService
{
    private const int MaximumVisitedElements = 1_500;
    private const int MaximumCachedElements = 500;
    private const int RestoreWindow = 9;
    private const uint KeyboardInput = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private static readonly TimeSpan HandleLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NativeCallTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, CachedElement> _elements = new(StringComparer.Ordinal);

    public async Task<UiAutomationWindowTarget?> ResolveWindowAsync(
        string windowId,
        CancellationToken cancellationToken)
    {
        if (!TryParseWindowId(windowId, out var window)) return null;
        return await Task.Run(() => ReadWindowIdentity(windowId, window), cancellationToken)
            .WaitAsync(NativeCallTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UiAutomationInspection?> InspectAsync(
        string windowId,
        string? query,
        int maximumElements,
        CancellationToken cancellationToken)
    {
        if (maximumElements is < 1 or > 50 || !TryParseWindowId(windowId, out var window)) return null;
        return await Task.Run(() => InspectCore(windowId, window, query, maximumElements), cancellationToken)
            .WaitAsync(NativeCallTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UiAutomationElementTarget?> ResolveAsync(
        string elementId,
        CancellationToken cancellationToken)
    {
        if (!_elements.TryGetValue(elementId, out var cached) || cached.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            _elements.TryRemove(elementId, out _);
            return null;
        }
        return await Task.Run(() => ResolveCore(cached.Target)?.Target, cancellationToken)
            .WaitAsync(NativeCallTimeout, cancellationToken).ConfigureAwait(false);
    }

    public Task<UiAutomationActionStatus> InvokeAsync(
        UiAutomationElementTarget expected,
        CancellationToken cancellationToken) =>
        RunActionAsync(expected, static (service, resolved, token) => service.InvokeCoreAsync(resolved, token), cancellationToken);

    public Task<UiAutomationActionStatus> SetValueAsync(
        UiAutomationElementTarget expected,
        string value,
        CancellationToken cancellationToken) =>
        RunActionAsync(expected, (service, resolved, token) => service.SetValueCoreAsync(resolved, value, token), cancellationToken);

    public Task<UiAutomationActionStatus> SendTextAsync(
        UiAutomationElementTarget expected,
        string text,
        CancellationToken cancellationToken) =>
        RunActionAsync(expected, (service, resolved, token) => service.SendTextCoreAsync(resolved, text, token), cancellationToken);

    private async Task<UiAutomationActionStatus> RunActionAsync(
        UiAutomationElementTarget expected,
        Func<WindowsUiAutomationService, ResolvedElement, CancellationToken, Task<UiAutomationActionStatus>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await Task.Run(() => ResolveCore(expected), cancellationToken)
                .WaitAsync(NativeCallTimeout, cancellationToken).ConfigureAwait(false);
            if (resolved is null) return UiAutomationActionStatus.TargetUnavailable;
            if (!StringComparer.Ordinal.Equals(resolved.Target.Identity, expected.Identity))
                return UiAutomationActionStatus.IdentityChanged;
            return await action(this, resolved, cancellationToken)
                .WaitAsync(NativeCallTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return UiAutomationActionStatus.Failed;
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return UiAutomationActionStatus.TargetUnavailable;
        }
    }

    private UiAutomationInspection? InspectCore(
        string windowId,
        IntPtr window,
        string? query,
        int maximumElements)
    {
        var identity = ReadWindowIdentity(windowId, window);
        if (identity is null) return null;
        var root = AutomationElement.FromHandle(window);
        var normalizedQuery = query?.Trim();
        var results = new List<UiAutomationElementSummary>();
        var truncated = false;
        foreach (var element in EnumerateElements(root, MaximumVisitedElements))
        {
            var properties = ReadProperties(element);
            if (properties is null || !Matches(properties, normalizedQuery)) continue;
            if (results.Count >= maximumElements)
            {
                truncated = true;
                break;
            }
            var elementId = Guid.NewGuid().ToString("N");
            var target = new UiAutomationElementTarget(
                elementId,
                windowId,
                identity.ProcessId,
                identity.ProcessStartedAtUtcTicks,
                identity.Title,
                identity.ProcessName,
                properties.RuntimeId,
                properties.Name,
                properties.AutomationId,
                properties.ControlType,
                properties.IsPassword,
                properties.IsKeyboardFocusable,
                properties.SupportsInvoke,
                properties.SupportsValue);
            _elements[elementId] = new CachedElement(target, DateTimeOffset.UtcNow.Add(HandleLifetime));
            results.Add(new UiAutomationElementSummary(
                elementId,
                properties.Name,
                properties.AutomationId,
                properties.ControlType,
                properties.IsEnabled,
                properties.IsOffscreen,
                properties.SupportsInvoke,
                properties.SupportsValue && !properties.IsPassword,
                properties.IsKeyboardFocusable && !properties.IsPassword && properties.ControlType == "Edit"));
        }
        TrimCache();
        return new UiAutomationInspection(windowId, identity.Title, identity.ProcessName, results, truncated);
    }

    private ResolvedElement? ResolveCore(UiAutomationElementTarget expected)
    {
        if (!TryParseWindowId(expected.WindowId, out var window)) return null;
        var identity = ReadWindowIdentity(expected.WindowId, window);
        if (identity is null || identity.ProcessId != expected.ProcessId ||
            identity.ProcessStartedAtUtcTicks != expected.ProcessStartedAtUtcTicks) return null;
        var root = AutomationElement.FromHandle(window);
        foreach (var element in EnumerateElements(root, MaximumVisitedElements))
        {
            var properties = ReadProperties(element);
            if (properties is null || !StringComparer.Ordinal.Equals(properties.RuntimeId, expected.RuntimeId)) continue;
            var current = expected with
            {
                WindowTitle = identity.Title,
                ProcessName = identity.ProcessName,
                Name = properties.Name,
                AutomationId = properties.AutomationId,
                ControlType = properties.ControlType,
                IsPassword = properties.IsPassword,
                IsKeyboardFocusable = properties.IsKeyboardFocusable,
                SupportsInvoke = properties.SupportsInvoke,
                SupportsValue = properties.SupportsValue,
            };
            return new ResolvedElement(current, element, window);
        }
        return null;
    }

    private async Task<UiAutomationActionStatus> InvokeCoreAsync(
        ResolvedElement resolved,
        CancellationToken cancellationToken)
    {
        if (!resolved.Target.SupportsInvoke ||
            !resolved.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            return UiAutomationActionStatus.Unsupported;
        if (!await FocusWindowAsync(resolved, focusElement: false, cancellationToken).ConfigureAwait(false))
            return UiAutomationActionStatus.FocusFailed;
        await Task.Run(() => ((InvokePattern)pattern).Invoke(), cancellationToken).ConfigureAwait(false);
        return UiAutomationActionStatus.Succeeded;
    }

    private async Task<UiAutomationActionStatus> SetValueCoreAsync(
        ResolvedElement resolved,
        string value,
        CancellationToken cancellationToken)
    {
        if (resolved.Target.IsPassword) return UiAutomationActionStatus.PasswordField;
        if (!resolved.Target.SupportsValue ||
            !resolved.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ||
            ((ValuePattern)pattern).Current.IsReadOnly) return UiAutomationActionStatus.Unsupported;
        if (!await FocusWindowAsync(resolved, focusElement: true, cancellationToken).ConfigureAwait(false))
            return UiAutomationActionStatus.FocusFailed;
        await Task.Run(() => ((ValuePattern)pattern).SetValue(value), cancellationToken).ConfigureAwait(false);
        return UiAutomationActionStatus.Succeeded;
    }

    private async Task<UiAutomationActionStatus> SendTextCoreAsync(
        ResolvedElement resolved,
        string text,
        CancellationToken cancellationToken)
    {
        if (resolved.Target.IsPassword) return UiAutomationActionStatus.PasswordField;
        if (!resolved.Target.IsKeyboardFocusable || resolved.Target.ControlType != "Edit")
            return UiAutomationActionStatus.Unsupported;
        var inputTick = GetLastInputTick();
        if (inputTick is null) return UiAutomationActionStatus.Failed;
        if (!await FocusWindowAsync(resolved, focusElement: true, cancellationToken).ConfigureAwait(false))
            return UiAutomationActionStatus.FocusFailed;
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        var currentInputTick = GetLastInputTick();
        if (currentInputTick is null) return UiAutomationActionStatus.Failed;
        if (inputTick != currentInputTick) return UiAutomationActionStatus.UserInputDetected;
        if (GetForegroundWindow() != resolved.Window || !HasFocusWithin(resolved.Element, resolved.Target.RuntimeId))
            return UiAutomationActionStatus.FocusFailed;
        return SendUnicodeText(text)
            ? UiAutomationActionStatus.Succeeded
            : UiAutomationActionStatus.Failed;
    }

    private static async Task<bool> FocusWindowAsync(
        ResolvedElement resolved,
        bool focusElement,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(resolved.Window)) return false;
        if (IsIconic(resolved.Window)) _ = ShowWindow(resolved.Window, RestoreWindow);
        if (GetForegroundWindow() != resolved.Window && !SetForegroundWindow(resolved.Window)) return false;
        await Task.Delay(75, cancellationToken).ConfigureAwait(false);
        if (GetForegroundWindow() != resolved.Window) return false;
        if (!focusElement) return true;
        if (!resolved.Target.IsKeyboardFocusable) return false;
        resolved.Element.SetFocus();
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        return GetForegroundWindow() == resolved.Window &&
               HasFocusWithin(resolved.Element, resolved.Target.RuntimeId);
    }

    private static UiAutomationWindowTarget? ReadWindowIdentity(string windowId, IntPtr window)
    {
        if (!IsWindow(window)) return null;
        _ = GetWindowThreadProcessId(window, out var rawProcessId);
        if (rawProcessId == 0 || rawProcessId == Environment.ProcessId) return null;
        try
        {
            using var process = Process.GetProcessById(checked((int)rawProcessId));
            var root = AutomationElement.FromHandle(window);
            return new UiAutomationWindowTarget(
                windowId,
                process.Id,
                process.StartTime.ToUniversalTime().Ticks,
                Limit(root.Current.Name, 512),
                Limit(process.ProcessName, 128));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception || IsAutomationFailure(exception))
        {
            return null;
        }
    }

    private static IEnumerable<AutomationElement> EnumerateElements(AutomationElement root, int maximumVisited)
    {
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        var first = TryGetFirstChild(walker, root);
        if (first is not null) queue.Enqueue(first);
        var visited = 0;
        while (queue.Count > 0 && visited++ < maximumVisited)
        {
            var element = queue.Dequeue();
            yield return element;
            var child = TryGetFirstChild(walker, element);
            if (child is not null) queue.Enqueue(child);
            var sibling = TryGetNextSibling(walker, element);
            if (sibling is not null) queue.Enqueue(sibling);
        }
    }

    private static AutomationElement? TryGetFirstChild(TreeWalker walker, AutomationElement element)
    {
        try { return walker.GetFirstChild(element); }
        catch (Exception exception) when (IsAutomationFailure(exception)) { return null; }
    }

    private static AutomationElement? TryGetNextSibling(TreeWalker walker, AutomationElement element)
    {
        try { return walker.GetNextSibling(element); }
        catch (Exception exception) when (IsAutomationFailure(exception)) { return null; }
    }

    private static ElementProperties? ReadProperties(AutomationElement element)
    {
        try
        {
            var controlType = element.Current.ControlType?.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal) ?? "Custom";
            var supportsInvoke = element.TryGetCurrentPattern(InvokePattern.Pattern, out _);
            var supportsValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
            return new ElementProperties(
                RuntimeId(element.GetRuntimeId()),
                Limit(element.Current.Name ?? string.Empty, 512),
                Limit(element.Current.AutomationId ?? string.Empty, 256),
                Limit(controlType, 64),
                element.Current.IsEnabled,
                element.Current.IsOffscreen,
                element.Current.IsPassword,
                element.Current.IsKeyboardFocusable,
                supportsInvoke,
                supportsValue);
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return null;
        }
    }

    private static bool Matches(ElementProperties properties, string? query)
    {
        var visibleMetadata = properties.Name.Length > 0 || properties.AutomationId.Length > 0 ||
                              properties.SupportsInvoke || properties.SupportsValue;
        if (!visibleMetadata) return false;
        if (string.IsNullOrEmpty(query)) return true;
        return properties.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               properties.AutomationId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               properties.ControlType.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void TrimCache()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _elements.Where(entry => entry.Value.ExpiresAtUtc <= now))
            _elements.TryRemove(entry.Key, out _);
        if (_elements.Count <= MaximumCachedElements) return;
        foreach (var entry in _elements.OrderBy(entry => entry.Value.ExpiresAtUtc).Take(_elements.Count - MaximumCachedElements))
            _elements.TryRemove(entry.Key, out _);
    }

    private static bool HasSameRuntimeId(AutomationElement? element, string expected)
    {
        if (element is null) return false;
        try { return StringComparer.Ordinal.Equals(RuntimeId(element.GetRuntimeId()), expected); }
        catch (Exception exception) when (IsAutomationFailure(exception)) { return false; }
    }

    private static bool HasFocusWithin(AutomationElement target, string targetRuntimeId)
    {
        AutomationElement? current;
        try { current = AutomationElement.FocusedElement; }
        catch (Exception exception) when (IsAutomationFailure(exception)) { return false; }
        var walker = TreeWalker.ControlViewWalker;
        for (var depth = 0; current is not null && depth < 32; depth++)
        {
            if (HasSameRuntimeId(current, targetRuntimeId)) return true;
            try { current = walker.GetParent(current); }
            catch (Exception exception) when (IsAutomationFailure(exception)) { return false; }
        }
        try { return HasSameRuntimeId(target, targetRuntimeId) && target.Current.HasKeyboardFocus; }
        catch (Exception exception) when (IsAutomationFailure(exception)) { return false; }
    }

    private static string RuntimeId(IReadOnlyList<int> values) => string.Join('.', values);

    private static bool SendUnicodeText(string text)
    {
        var inputs = new NativeInput[text.Length * 2];
        for (var index = 0; index < text.Length; index++)
        {
            inputs[index * 2] = NativeInput.Keyboard(text[index], KeyEventUnicode);
            inputs[index * 2 + 1] = NativeInput.Keyboard(text[index], KeyEventUnicode | KeyEventKeyUp);
        }
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == inputs.Length;
    }

    private static uint? GetLastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Tick : null;
    }

    private static bool TryParseWindowId(string value, out IntPtr window)
    {
        window = IntPtr.Zero;
        if (value.Length is < 3 or > 18 || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
            return false;
        window = new IntPtr(parsed);
        return true;
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static bool IsAutomationFailure(Exception exception) =>
        exception is ElementNotAvailableException or NoClickablePointException or COMException or InvalidOperationException;

    private sealed record CachedElement(UiAutomationElementTarget Target, DateTimeOffset ExpiresAtUtc);
    private sealed record ResolvedElement(UiAutomationElementTarget Target, AutomationElement Element, IntPtr Window);
    private sealed record ElementProperties(
        string RuntimeId,
        string Name,
        string AutomationId,
        string ControlType,
        bool IsEnabled,
        bool IsOffscreen,
        bool IsPassword,
        bool IsKeyboardFocusable,
        bool SupportsInvoke,
        bool SupportsValue);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Tick;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;

        public static NativeInput Keyboard(char character, uint flags) => new()
        {
            Type = KeyboardInput,
            Data = new NativeInputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    Scan = character,
                    Flags = flags,
                },
            },
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public KeyboardInputData Keyboard;
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public HardwareInputData Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInputData
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
