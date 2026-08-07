using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using LIGClaw.Desktop.Infrastructure.Platform;

namespace LIGClaw.Desktop.Infrastructure.Tools;

internal sealed record RawProcessResourceSample(
    int ProcessId,
    long StartedAtUtcTicks,
    string ProcessName,
    long ProcessorTimeTicks,
    long WorkingSetBytes);

internal sealed record ProcessResourceGroupSnapshot(
    string ProcessName,
    int InstanceCount,
    double CpuUsagePercent,
    long WorkingSetBytes);

internal sealed record ProcessResourceStatusSnapshot(
    string ProviderStatus,
    int SampleDurationMilliseconds,
    int ObservedProcessCount,
    int ObservedGroupCount,
    IReadOnlyList<ProcessResourceGroupSnapshot> TopCpuProcesses,
    IReadOnlyList<ProcessResourceGroupSnapshot> TopMemoryProcesses,
    bool Truncated);

internal interface IProcessResourceStatusReader
{
    Task<ProcessResourceStatusSnapshot> ReadAsync(int maximumResults, CancellationToken cancellationToken);
}

internal sealed class WindowsProcessResourceStatusReader : IProcessResourceStatusReader
{
    private static readonly TimeSpan SampleDuration = TimeSpan.FromMilliseconds(500);

    public async Task<ProcessResourceStatusSnapshot> ReadAsync(
        int maximumResults,
        CancellationToken cancellationToken)
    {
        try
        {
            var first = Capture();
            var stopwatch = Stopwatch.StartNew();
            await Task.Delay(SampleDuration, cancellationToken).ConfigureAwait(false);
            var second = Capture();
            stopwatch.Stop();
            return CreateSnapshot(
                first,
                second,
                stopwatch.Elapsed,
                Math.Max(1, Environment.ProcessorCount),
                maximumResults);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return Unavailable();
        }
    }

    internal static ProcessResourceStatusSnapshot CreateSnapshot(
        IReadOnlyList<RawProcessResourceSample> first,
        IReadOnlyList<RawProcessResourceSample> second,
        TimeSpan elapsed,
        int logicalProcessorCount,
        int maximumResults)
    {
        if (elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(5))
            return Unavailable();
        var safeMaximum = Math.Clamp(maximumResults, 1, 10);
        var safeProcessors = Math.Max(1, logicalProcessorCount);
        var elapsedSeconds = Math.Max(0.001, elapsed.TotalSeconds);
        var initial = first
            .GroupBy(sample => (sample.ProcessId, sample.StartedAtUtcTicks))
            .ToDictionary(group => group.Key, group => group.Last());
        var observations = second
            .Where(sample => initial.ContainsKey((sample.ProcessId, sample.StartedAtUtcTicks)))
            .Select(sample =>
            {
                var before = initial[(sample.ProcessId, sample.StartedAtUtcTicks)];
                var processorDelta = Math.Max(0, sample.ProcessorTimeTicks - before.ProcessorTimeTicks);
                var cpu = Math.Clamp(
                    processorDelta / (double)TimeSpan.TicksPerSecond / elapsedSeconds / safeProcessors * 100,
                    0,
                    100);
                return new ProcessResourceGroupSnapshot(
                    sample.ProcessName,
                    1,
                    cpu,
                    Math.Max(0, sample.WorkingSetBytes));
            })
            .ToArray();
        var groups = observations
            .GroupBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcessResourceGroupSnapshot(
                group.First().ProcessName,
                group.Count(),
                Math.Round(Math.Clamp(group.Sum(item => item.CpuUsagePercent), 0, 100), 1),
                group.Aggregate(0L, (total, item) => SaturatingAdd(total, item.WorkingSetBytes))))
            .ToArray();
        var topCpu = groups
            .OrderByDescending(item => item.CpuUsagePercent)
            .ThenByDescending(item => item.WorkingSetBytes)
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(safeMaximum)
            .ToArray();
        var topMemory = groups
            .OrderByDescending(item => item.WorkingSetBytes)
            .ThenByDescending(item => item.CpuUsagePercent)
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(safeMaximum)
            .ToArray();
        return new ProcessResourceStatusSnapshot(
            "available",
            (int)Math.Clamp(Math.Round(elapsed.TotalMilliseconds), 0, 5_000),
            observations.Length,
            groups.Length,
            topCpu,
            topMemory,
            groups.Length > safeMaximum);
    }

    private static IReadOnlyList<RawProcessResourceSample> Capture()
    {
        var samples = new List<RawProcessResourceSample>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName.Trim();
                    if (name.Length is 0 or > 128) continue;
                    samples.Add(new RawProcessResourceSample(
                        process.Id,
                        process.StartTime.ToUniversalTime().Ticks,
                        name,
                        Math.Max(0, process.TotalProcessorTime.Ticks),
                        Math.Max(0, process.WorkingSet64)));
                }
                catch (Exception exception) when (exception is
                    Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
                {
                    // A protected or exited process is isolated from the rest of the snapshot.
                }
            }
        }
        return samples;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private static ProcessResourceStatusSnapshot Unavailable() => new(
        "unavailable", 0, 0, 0, [], [], false);
}

internal sealed class SystemGetProcessResourceStatusTool(IProcessResourceStatusReader? reader = null) : IWindowsToolAdapter
{
    private readonly IProcessResourceStatusReader _reader = reader ?? new WindowsProcessResourceStatusReader();

    public string Name => "system.get_process_resource_status.v1";
    public string Risk => "R1";
    public WindowsCapability RequiredCapabilities => WindowsCapability.Win32DesktopShell;
    public int Priority => 0;

    public WindowsToolApprovalPrompt? CreateApprovalPrompt(IReadOnlyDictionary<string, object?> input)
    {
        var parameters = Read(input);
        return parameters is null
            ? null
            : new WindowsToolApprovalPrompt(
                "실행 중인 앱의 사용량을 확인할까요?",
                $"CPU와 메모리를 많이 쓰는 앱을 각각 최대 {parameters.MaximumResults}개 확인합니다.",
                $"요청 이유: {parameters.Reason}\n\n프로세스 이름과 순간 CPU·메모리 사용량이 모델에 전달됩니다. PID, 실행 파일 경로, 창 제목과 사용자 이름은 확인하지 않습니다.",
                Risk: "R1");
    }

    public async Task<WindowsToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken)
    {
        var parameters = Read(input);
        if (parameters is null)
            return Failure("프로세스 사용량 조회 조건이 올바르지 않습니다.");
        var snapshot = await _reader.ReadAsync(parameters.MaximumResults, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, object?> output = new Dictionary<string, object?>
        {
            ["providerStatus"] = snapshot.ProviderStatus,
            ["sampleDurationMilliseconds"] = snapshot.SampleDurationMilliseconds,
            ["observedProcessCount"] = snapshot.ObservedProcessCount,
            ["observedGroupCount"] = snapshot.ObservedGroupCount,
            ["topCpuProcesses"] = snapshot.TopCpuProcesses.Select(ToOutput).ToArray(),
            ["topMemoryProcesses"] = snapshot.TopMemoryProcesses.Select(ToOutput).ToArray(),
            ["truncated"] = snapshot.Truncated,
        };
        return new WindowsToolExecutionResult(
            true,
            output,
            ActivitySummary: snapshot.ProviderStatus == "available"
                ? "CPU와 메모리를 많이 쓰는 앱을 확인했어요."
                : "프로세스 사용량 공급자를 사용할 수 없어요.");
    }

    private static ProcessResourceInput? Read(IReadOnlyDictionary<string, object?> input)
    {
        if (!ToolInputReader.HasOnlyKeys(input, "maxResults", "reason") ||
            !ToolInputReader.TryGetRequiredString(input, "reason", 160, out var reason) ||
            !input.TryGetValue("maxResults", out var rawMaximum)) return null;
        var maximum = rawMaximum switch
        {
            int value => value,
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var value) => value,
            _ => -1,
        };
        return maximum is < 1 or > 10 ? null : new ProcessResourceInput(maximum, reason);
    }

    private static IReadOnlyDictionary<string, object?> ToOutput(ProcessResourceGroupSnapshot item) =>
        new Dictionary<string, object?>
        {
            ["processName"] = item.ProcessName,
            ["instanceCount"] = item.InstanceCount,
            ["cpuUsagePercent"] = item.CpuUsagePercent,
            ["workingSetBytes"] = item.WorkingSetBytes,
        };

    private static WindowsToolExecutionResult Failure(string error) =>
        new(false, new Dictionary<string, object?>(), error);

    private sealed record ProcessResourceInput(int MaximumResults, string Reason);
}
