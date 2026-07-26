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
        var resources = await host.ExecuteAsync(Invocation("system.get_resource_status.v1"));
        var network = await host.ExecuteAsync(Invocation("system.get_network_status.v1"));

        Assert.True(storage.Success, storage.Error);
        Assert.True(resources.Success, resources.Error);
        Assert.True(network.Success, network.Error);
        Assert.NotEmpty(Assert.IsAssignableFrom<IEnumerable<IReadOnlyDictionary<string, object?>>>(storage.Output["volumes"]));
        Assert.InRange(Assert.IsType<double>(resources.Output["cpuUsagePercent"]), 0, 100);
        Assert.IsType<bool>(network.Output["networkAvailable"]);
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

    private sealed class FakeStorageReader : IStorageStatusReader
    {
        public IReadOnlyList<StorageVolumeSnapshot> Read() =>
            [new("C:\\", "fixed", "ready", "NTFS", 1_000, 250, 75)];
    }

    private sealed class FakeResourceReader : IResourceStatusReader
    {
        public Task<ResourceStatusSnapshot> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(
            new ResourceStatusSnapshot(7_200, 12, 37.5, 16_000, 4_000, 75));
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
