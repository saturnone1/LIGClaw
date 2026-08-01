using LIGClaw.Desktop.Infrastructure.Platform;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class SystemObservationToolTests
{
    [Fact]
    public async Task Default_Windows_readers_return_live_observation_snapshots()
    {
        var host = new WindowsToolHost(WindowsPlatformProfile.Classify(10, 0, Environment.OSVersion.Version.Build, isWorkstation: true));

        var storage = await host.ExecuteAsync(Invocation("system.get_storage_status.v1"));
        var power = await host.ExecuteAsync(Invocation("system.get_power_status.v1"));
        var resources = await host.ExecuteAsync(Invocation("system.get_resource_status.v1"));
        var network = await host.ExecuteAsync(Invocation("system.get_network_status.v1"));

        Assert.True(storage.Success, storage.Error);
        Assert.True(power.Success, power.Error);
        Assert.True(resources.Success, resources.Error);
        Assert.True(network.Success, network.Error);
        Assert.NotEmpty(Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(storage.Output["volumes"]));
        Assert.Contains(Assert.IsType<string>(power.Output["providerStatus"]), new[] { "available", "unavailable" });
        Assert.InRange(Assert.IsType<double>(resources.Output["cpuUsagePercent"]), 0, 100);
        Assert.IsType<bool>(network.Output["networkAvailable"]);
    }

    [Fact]
    public async Task Default_process_reader_returns_a_bounded_live_snapshot()
    {
        var reader = new WindowsProcessResourceStatusReader();

        var result = await reader.ReadAsync(3, CancellationToken.None);

        Assert.Equal("available", result.ProviderStatus);
        Assert.InRange(result.SampleDurationMilliseconds, 1, 5_000);
        Assert.InRange(result.TopCpuProcesses.Count, 0, 3);
        Assert.InRange(result.TopMemoryProcesses.Count, 0, 3);
        Assert.All(result.TopCpuProcesses, item => Assert.InRange(item.CpuUsagePercent, 0, 100));
    }

    [Fact]
    public void Network_details_reader_returns_current_addresses_without_prompting_for_wifi_in_tests()
    {
        var result = new WindowsNetworkDetailsReader(
            new FakeWifiSsidReader(new WifiSsidReadResult("not_applicable", new Dictionary<Guid, string>())))
            .Read();

        Assert.Equal("available", result.ProviderStatus);
        Assert.Equal("not_applicable", result.WifiSsidStatus);
        Assert.InRange(result.Adapters.Count, 0, 16);
        Assert.All(result.Adapters, adapter =>
        {
            Assert.InRange(adapter.Addresses.Count, 0, 8);
            Assert.InRange(adapter.DnsServers.Count, 0, 4);
            Assert.InRange(adapter.Gateways.Count, 0, 4);
        });
    }

    [Fact]
    public async Task Network_details_requires_context_approval_and_bounds_every_address_list()
    {
        var adapter = new NetworkDetailsAdapterSnapshot(
            "Wi-Fi",
            "wireless80211",
            Enumerable.Range(1, 12).Select(index => $"192.0.2.{index}/24").ToArray(),
            Enumerable.Range(1, 6).Select(index => $"2001:db8::{index}").ToArray(),
            Enumerable.Range(1, 6).Select(index => $"198.51.100.{index}").ToArray(),
            "Office WiFi");
        var snapshot = new NetworkDetailsSnapshot("available", "available", true, [adapter], false);
        var tool = new SystemGetNetworkDetailsTool(new FakeNetworkDetailsReader(snapshot));
        var host = Host(tool);
        var invocation = NetworkDetailsInvocation("연결 문제를 확인하기 위해");

        var approval = host.CreateApprovalPrompt(invocation);
        var result = await host.ExecuteAsync(invocation);

        Assert.True(approval.Success, approval.Error);
        Assert.Equal(TimeSpan.FromSeconds(60), tool.Timeout);
        var prompt = Assert.IsType<WindowsToolApprovalPrompt>(approval.Prompt);
        Assert.Equal("R1", prompt.Risk);
        Assert.Null(prompt.GrantScope);
        Assert.Contains("위치", prompt.Details, StringComparison.Ordinal);
        Assert.Contains("비밀번호", prompt.Details, StringComparison.Ordinal);
        Assert.True(result.Success, result.Error);
        Assert.Equal(true, result.Output["truncated"]);
        var output = Assert.Single(Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(result.Output["adapters"]));
        Assert.Equal("Office WiFi", output["wifiSsid"]);
        Assert.Equal(8, Assert.IsAssignableFrom<IEnumerable<string>>(output["addresses"]).Count());
        Assert.Equal(4, Assert.IsAssignableFrom<IEnumerable<string>>(output["dnsServers"]).Count());
        Assert.Equal(4, Assert.IsAssignableFrom<IEnumerable<string>>(output["gateways"]).Count());
        Assert.DoesNotContain("macAddress", output.Keys);
        Assert.DoesNotContain("bssid", output.Keys);
        Assert.DoesNotContain("credential", output.Keys);
        Assert.DoesNotContain("history", output.Keys);
    }

    [Fact]
    public async Task Network_details_isolates_wifi_location_permission_from_address_results()
    {
        var snapshot = new NetworkDetailsSnapshot(
            "available",
            "permission_required",
            true,
            [new("Ethernet", "ethernet", ["192.0.2.10/24"], ["192.0.2.53"], ["192.0.2.1"], null)],
            false);
        var host = Host(new SystemGetNetworkDetailsTool(new FakeNetworkDetailsReader(snapshot)));

        var result = await host.ExecuteAsync(NetworkDetailsInvocation("DNS 문제 확인"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("permission_required", result.Output["wifiSsidStatus"]);
        Assert.Single(Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(result.Output["adapters"]));
    }

    [Theory]
    [InlineData(19_045)]
    [InlineData(22_631)]
    public async Task Network_details_uses_the_common_adapter_on_Windows_10_and_11(int build)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, build, isWorkstation: true);
        var snapshot = new NetworkDetailsSnapshot("unavailable", "unavailable", false, [], false);
        var host = new WindowsToolHost(profile, [new SystemGetNetworkDetailsTool(new FakeNetworkDetailsReader(snapshot))]);

        var result = await host.ExecuteAsync(NetworkDetailsInvocation("연결 상태 확인"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("unavailable", result.Output["providerStatus"]);
    }

    [Fact]
    public void Wifi_ssid_decoder_accepts_only_bounded_valid_utf8_without_control_characters()
    {
        var valid = System.Text.Encoding.UTF8.GetBytes("Office WiFi");
        var invalidUtf8 = new byte[] { 0xC3, 0x28 };
        var control = System.Text.Encoding.UTF8.GetBytes("Office\nWiFi");

        Assert.Equal("Office WiFi", WindowsWifiSsidReader.DecodeSsid(valid, valid.Length));
        Assert.Null(WindowsWifiSsidReader.DecodeSsid(invalidUtf8, invalidUtf8.Length));
        Assert.Null(WindowsWifiSsidReader.DecodeSsid(control, control.Length));
        Assert.Null(WindowsWifiSsidReader.DecodeSsid(new byte[33], 33));
    }

    [Fact]
    public async Task Network_details_rejects_missing_or_extra_context_input()
    {
        var host = Host(new SystemGetNetworkDetailsTool(new FakeNetworkDetailsReader(
            new NetworkDetailsSnapshot("available", "not_applicable", true, [], false))));
        var invalid = NetworkDetailsInvocation("이유") with
        {
            Input = new Dictionary<string, object?> { ["reason"] = "이유", ["includeCredentials"] = true },
        };

        Assert.False(host.CreateApprovalPrompt(invalid).Success);
        Assert.False((await host.ExecuteAsync(invalid)).Success);
    }

    [Fact]
    public async Task Process_resource_status_requires_a_clear_one_time_context_approval()
    {
        var snapshot = new ProcessResourceStatusSnapshot(
            "available", 500, 2, 1,
            [new("browser", 2, 25, 1_000)],
            [new("browser", 2, 25, 1_000)],
            false);
        var reader = new FakeProcessResourceStatusReader(snapshot);
        var host = Host(new SystemGetProcessResourceStatusTool(reader));
        var invocation = ProcessInvocation(5, "PC가 느린 원인을 확인하기 위해");

        var approval = host.CreateApprovalPrompt(invocation);
        var result = await host.ExecuteAsync(invocation);

        Assert.True(approval.Success, approval.Error);
        var prompt = Assert.IsType<WindowsToolApprovalPrompt>(approval.Prompt);
        Assert.Equal("R1", prompt.Risk);
        Assert.Null(prompt.GrantScope);
        Assert.Contains("프로세스 이름", prompt.Details, StringComparison.Ordinal);
        Assert.Contains("PID", prompt.Details, StringComparison.Ordinal);
        Assert.True(result.Success, result.Error);
        Assert.Equal(5, reader.MaximumResults);
        var cpu = Assert.Single(Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(result.Output["topCpuProcesses"]));
        Assert.Equal("browser", cpu["processName"]);
        Assert.DoesNotContain("processId", cpu.Keys);
        Assert.DoesNotContain("executablePath", cpu.Keys);
        Assert.DoesNotContain("windowTitle", cpu.Keys);
        Assert.DoesNotContain("userName", cpu.Keys);
    }

    [Fact]
    public void Process_resource_snapshot_aggregates_names_ranks_both_resources_and_ignores_reused_process_ids()
    {
        var first = new RawProcessResourceSample[]
        {
            new(1, 100, "browser", 0, 100),
            new(2, 200, "Browser", 0, 200),
            new(3, 300, "editor", 0, 500),
            new(4, 400, "old", 0, 900),
        };
        var second = new RawProcessResourceSample[]
        {
            new(1, 100, "browser", TimeSpan.FromSeconds(1).Ticks, 100),
            new(2, 200, "Browser", TimeSpan.FromMilliseconds(500).Ticks, 200),
            new(3, 300, "editor", TimeSpan.FromMilliseconds(200).Ticks, 500),
            new(4, 401, "replacement", TimeSpan.FromSeconds(1).Ticks, 900),
        };

        var result = WindowsProcessResourceStatusReader.CreateSnapshot(first, second, TimeSpan.FromSeconds(1), 2, 1);

        Assert.Equal(3, result.ObservedProcessCount);
        Assert.Equal(2, result.ObservedGroupCount);
        Assert.True(result.Truncated);
        var cpu = Assert.Single(result.TopCpuProcesses);
        Assert.Equal("browser", cpu.ProcessName);
        Assert.Equal(2, cpu.InstanceCount);
        Assert.Equal(75, cpu.CpuUsagePercent);
        Assert.Equal("editor", Assert.Single(result.TopMemoryProcesses).ProcessName);
        Assert.DoesNotContain(result.TopCpuProcesses, item => item.ProcessName == "replacement");
    }

    [Fact]
    public void Process_resource_snapshot_rejects_a_stale_sample_after_sleep_or_stall()
    {
        var samples = new[] { new RawProcessResourceSample(1, 100, "app", 0, 100) };

        var result = WindowsProcessResourceStatusReader.CreateSnapshot(
            samples, samples, TimeSpan.FromSeconds(6), 4, 5);

        Assert.Equal("unavailable", result.ProviderStatus);
        Assert.Empty(result.TopCpuProcesses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task Process_resource_status_rejects_unbounded_result_requests(int maximumResults)
    {
        var reader = new FakeProcessResourceStatusReader(new ProcessResourceStatusSnapshot(
            "available", 500, 0, 0, [], [], false));
        var host = Host(new SystemGetProcessResourceStatusTool(reader));
        var invocation = ProcessInvocation(maximumResults, "확인 이유");

        Assert.False(host.CreateApprovalPrompt(invocation).Success);
        Assert.False((await host.ExecuteAsync(invocation)).Success);
        Assert.Null(reader.MaximumResults);
    }

    [Theory]
    [InlineData(19_045)]
    [InlineData(22_631)]
    public async Task Process_resource_status_uses_the_common_adapter_on_Windows_10_and_11(int build)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, build, isWorkstation: true);
        var snapshot = new ProcessResourceStatusSnapshot("unavailable", 0, 0, 0, [], [], false);
        var host = new WindowsToolHost(profile, [new SystemGetProcessResourceStatusTool(new FakeProcessResourceStatusReader(snapshot))]);

        var result = await host.ExecuteAsync(ProcessInvocation(3, "느린 원인 확인"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("unavailable", result.Output["providerStatus"]);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(result.Output["topCpuProcesses"]));
    }

    [Fact]
    public async Task Power_status_returns_bounded_battery_details_without_identifiers()
    {
        var snapshot = new PowerStatusSnapshot(
            "available", "battery", "present", "on", "discharging", "low", 18, 3_600);
        var host = Host(new SystemGetPowerStatusTool(new FakePowerStatusReader(snapshot)));

        var result = await host.ExecuteAsync(Invocation("system.get_power_status.v1"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("battery", result.Output["powerSource"]);
        Assert.Equal("on", result.Output["energySaverStatus"]);
        var battery = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Output["battery"]);
        Assert.Equal(18d, battery["percent"]);
        Assert.Equal("low", battery["safetyStatus"]);
        Assert.DoesNotContain("serialNumber", battery.Keys);
        Assert.DoesNotContain("deviceName", battery.Keys);
    }

    [Fact]
    public async Task Power_status_omits_battery_details_on_a_desktop_without_a_battery()
    {
        var snapshot = new PowerStatusSnapshot("available", "ac", "not_present", "off");
        var host = Host(new SystemGetPowerStatusTool(new FakePowerStatusReader(snapshot)));

        var result = await host.ExecuteAsync(Invocation("system.get_power_status.v1"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("not_present", result.Output["batteryPresence"]);
        Assert.DoesNotContain("battery", result.Output.Keys);
    }

    [Theory]
    [InlineData(19_045)]
    [InlineData(22_631)]
    public async Task Power_status_uses_the_common_adapter_on_Windows_10_and_11(int build)
    {
        var profile = WindowsPlatformProfile.Classify(10, 0, build, isWorkstation: true);
        var snapshot = new PowerStatusSnapshot("unavailable", "unknown", "unknown", "unknown");
        var host = new WindowsToolHost(profile, [new SystemGetPowerStatusTool(new FakePowerStatusReader(snapshot))]);

        var result = await host.ExecuteAsync(Invocation("system.get_power_status.v1"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("unavailable", result.Output["providerStatus"]);
        Assert.Equal("unknown", result.Output["batteryPresence"]);
        Assert.DoesNotContain("battery", result.Output.Keys);
    }

    [Theory]
    [InlineData(0, 10, 17, 1, "battery", "charging", "low", "on")]
    [InlineData(0, 4, 3, 0, "battery", "discharging", "critical", "off")]
    [InlineData(1, 1, 100, 0, "ac", "not_charging", "normal", "off")]
    public void Windows_10_and_11_power_flags_are_classified_without_OS_specific_branches(
        byte acLineStatus,
        byte batteryFlag,
        byte percent,
        byte saver,
        string source,
        string charging,
        string safety,
        string energySaver)
    {
        var result = WindowsPowerStatusReader.Classify(acLineStatus, batteryFlag, percent, saver, uint.MaxValue);

        Assert.Equal(source, result.PowerSource);
        Assert.Equal(charging, result.ChargingStatus);
        Assert.Equal(safety, result.SafetyStatus);
        Assert.Equal(energySaver, result.EnergySaverStatus);
        Assert.Null(result.EstimatedRuntimeSeconds);
    }

    [Theory]
    [InlineData(128, 255, "not_present")]
    [InlineData(255, 255, "unknown")]
    [InlineData(1, 254, "unknown")]
    public void Power_reader_distinguishes_no_battery_from_unknown_provider_values(
        byte batteryFlag,
        byte percent,
        string presence)
    {
        var result = WindowsPowerStatusReader.Classify(1, batteryFlag, percent, 0, uint.MaxValue);

        Assert.Equal(presence, result.BatteryPresence);
        Assert.Null(result.Percent);
    }

    [Fact]
    public async Task Storage_status_returns_capacity_without_physical_health_claims()
    {
        var host = Host(new SystemGetStorageStatusTool(new FakeStorageReader()));

        var result = await host.ExecuteAsync(Invocation("system.get_storage_status.v1"));

        Assert.True(result.Success);
        Assert.Equal(false, result.Output["physicalHealthAvailable"]);
        var volumes = Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(result.Output["volumes"]);
        var volume = Assert.Single(volumes);
        Assert.Equal("C:\\", volume["rootPath"]);
        Assert.Equal(1_000L, volume["totalBytes"]);
        Assert.Equal(250L, volume["freeBytes"]);
        Assert.Equal(75d, volume["usedPercent"]);
    }

    [Fact]
    public async Task Resource_status_returns_cpu_memory_and_uptime_snapshot()
    {
        var host = Host(new SystemGetResourceStatusTool(new FakeResourceReader()));

        var result = await host.ExecuteAsync(Invocation("system.get_resource_status.v1"));

        Assert.True(result.Success);
        Assert.Equal(7_200L, result.Output["uptimeSeconds"]);
        Assert.Equal(12, result.Output["logicalProcessorCount"]);
        Assert.Equal(37.5d, result.Output["cpuUsagePercent"]);
        Assert.Equal(16_000L, result.Output["memoryTotalBytes"]);
        Assert.Equal(4_000L, result.Output["memoryAvailableBytes"]);
        Assert.Equal(75d, result.Output["memoryUsedPercent"]);
    }

    [Fact]
    public async Task Network_status_omits_addresses_and_reports_adapter_state()
    {
        var host = Host(new SystemGetNetworkStatusTool(new FakeNetworkReader()));

        var result = await host.ExecuteAsync(Invocation("system.get_network_status.v1"));

        Assert.True(result.Success);
        Assert.Equal(true, result.Output["networkAvailable"]);
        var adapters = Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(result.Output["adapters"]);
        var adapter = Assert.Single(adapters);
        Assert.Equal("Wi-Fi", adapter["name"]);
        Assert.Equal("up", adapter["status"]);
        Assert.DoesNotContain("ipAddress", adapter.Keys);
        Assert.DoesNotContain("macAddress", adapter.Keys);
    }

    [Fact]
    public async Task Disk_health_returns_bounded_physical_and_bitlocker_status_without_recovery_material()
    {
        var host = Host(new SystemGetDiskHealthTool(new FakeDiskHealthReader()));

        var result = await host.ExecuteAsync(Invocation("system.get_disk_health.v1"));

        Assert.True(result.Success, result.Error);
        Assert.Equal("available", result.Output["physicalDiskProviderStatus"]);
        var disk = Assert.Single(Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(result.Output["physicalDisks"]));
        Assert.Equal("System SSD", disk["name"]);
        Assert.Equal("healthy", disk["health"]);
        Assert.DoesNotContain("serialNumber", disk.Keys);
        var volume = Assert.Single(Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(result.Output["bitLockerVolumes"]));
        Assert.Equal("protected", volume["protectionStatus"]);
        Assert.DoesNotContain("recoveryKey", volume.Keys);
    }

    [Fact]
    public void Disk_health_isolates_an_unavailable_bitlocker_provider()
    {
        var reader = new WindowsDiskHealthReader(new PartiallyUnavailableWmiClient());

        var result = reader.Read();

        Assert.Equal("available", result.PhysicalDiskProviderStatus);
        Assert.Single(result.PhysicalDisks);
        Assert.Equal("unavailable", result.BitLockerProviderStatus);
        Assert.Empty(result.BitLockerVolumes);
    }

    [Fact]
    public async Task Security_status_returns_each_provider_as_an_independent_bounded_component()
    {
        var snapshot = new SecurityStatusSnapshot(
            new DefenderStatusSnapshot("available", true, true, 2),
            new FirewallStatusSnapshot("available", true, true, false),
            new WindowsUpdateStatusSnapshot("unavailable"));
        var host = Host(new SystemGetSecurityStatusTool(new FakeSecurityStatusReader(snapshot)));

        var result = await host.ExecuteAsync(Invocation("system.get_security_status.v1"));

        Assert.True(result.Success, result.Error);
        var defender = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Output["defender"]);
        var firewall = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Output["firewall"]);
        var update = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result.Output["windowsUpdate"]);
        Assert.Equal(true, defender["realTimeProtectionEnabled"]);
        Assert.Equal(false, firewall["publicEnabled"]);
        Assert.Equal("unavailable", update["status"]);
    }

    [Fact]
    public async Task Observation_tools_reject_model_supplied_input()
    {
        var host = Host(new SystemGetStorageStatusTool(new FakeStorageReader()));
        var invocation = Invocation("system.get_storage_status.v1") with
        {
            Input = new Dictionary<string, object?> { ["path"] = "C:\\" },
        };

        var result = await host.ExecuteAsync(invocation);

        Assert.False(result.Success);
        Assert.Empty(result.Output);
    }

    private static WindowsToolHost Host(IWindowsToolAdapter adapter) =>
        new(WindowsPlatformProfile.Classify(10, 0, 26_100, isWorkstation: true), [adapter]);

    private static LIGClaw.Contracts.Generated.ToolInvokeParams Invocation(string name) => new(
        "tool-call",
        "conversation",
        "run",
        name,
        "R0",
        new Dictionary<string, object?>());

    private static LIGClaw.Contracts.Generated.ToolInvokeParams ProcessInvocation(int maximumResults, string reason) => new(
        "tool-call",
        "conversation",
        "run",
        "system.get_process_resource_status.v1",
        "R1",
        new Dictionary<string, object?> { ["maxResults"] = maximumResults, ["reason"] = reason });

    private static LIGClaw.Contracts.Generated.ToolInvokeParams NetworkDetailsInvocation(string reason) => new(
        "tool-call",
        "conversation",
        "run",
        "system.get_network_details.v1",
        "R1",
        new Dictionary<string, object?> { ["reason"] = reason });

    private sealed class FakeStorageReader : IStorageStatusReader
    {
        public IReadOnlyList<StorageVolumeSnapshot> Read() =>
            [new("C:\\", "fixed", "ready", "NTFS", 1_000, 250, 75)];
    }

    private sealed class FakePowerStatusReader(PowerStatusSnapshot snapshot) : IPowerStatusReader
    {
        public PowerStatusSnapshot Read() => snapshot;
    }

    private sealed class FakeResourceReader : IResourceStatusReader
    {
        public Task<ResourceStatusSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(
            new ResourceStatusSnapshot(7_200, 12, 37.5, 16_000, 4_000, 75));
    }

    private sealed class FakeProcessResourceStatusReader(ProcessResourceStatusSnapshot snapshot) : IProcessResourceStatusReader
    {
        public int? MaximumResults { get; private set; }

        public Task<ProcessResourceStatusSnapshot> ReadAsync(int maximumResults, CancellationToken cancellationToken)
        {
            MaximumResults = maximumResults;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeWifiSsidReader(WifiSsidReadResult result) : IWifiSsidReader
    {
        public WifiSsidReadResult Read() => result;
    }

    private sealed class FakeNetworkDetailsReader(NetworkDetailsSnapshot snapshot) : INetworkDetailsReader
    {
        public NetworkDetailsSnapshot Read() => snapshot;
    }

    private sealed class FakeNetworkReader : INetworkStatusReader
    {
        public bool IsNetworkAvailable => true;
        public IReadOnlyList<NetworkAdapterSnapshot> ReadAdapters() => [new("Wi-Fi", "wireless80211", "up", 866)];
    }

    private sealed class FakeDiskHealthReader : IDiskHealthReader
    {
        public DiskHealthSnapshot Read() => new(
            "available",
            [new PhysicalDiskHealthSnapshot("System SSD", "ssd", "healthy", "ok", 1_000_000)],
            "available",
            [new BitLockerVolumeSnapshot("C:", "protected", "fully_encrypted")]);
    }

    private sealed class FakeSecurityStatusReader(SecurityStatusSnapshot snapshot) : ISecurityStatusReader
    {
        public SecurityStatusSnapshot Read() => snapshot;
    }

    private sealed class PartiallyUnavailableWmiClient : IWmiQueryClient
    {
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(
            string namespacePath,
            string query,
            IReadOnlyList<string> properties)
        {
            if (namespacePath.Contains("MicrosoftVolumeEncryption", StringComparison.Ordinal))
                throw new UnauthorizedAccessException();
            return
            [
                new Dictionary<string, object?>
                {
                    ["FriendlyName"] = "Disk 0",
                    ["MediaType"] = 4,
                    ["HealthStatus"] = 0,
                    ["OperationalStatus"] = new ushort[] { 2 },
                    ["Size"] = 1_000L,
                },
            ];
        }
    }
}
