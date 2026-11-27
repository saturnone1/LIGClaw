using LIGClaw.Contracts.Generated;
using LIGClaw.Contracts.Protocol;
using LIGClaw.Desktop.Infrastructure.Sidecar;

namespace LIGClaw.Desktop.Tests;

public sealed class SidecarHandshakeTests
{
    private static readonly IReadOnlyList<string> CompatibleCapabilities =
    [
        "health.ping",
        "conversation.start",
        "conversation.cancel",
        "agent.events",
        "provider.configure",
        "provider.test",
        "mcp.configure",
        "mcp.status",
        "mcp.call",
        "tool.invoke",
        "tool.result",
        "capability.runtime.snapshot.v2",
        "capability.proposal.generate",
        "workflow.invoke",
        "workflow.result",
        "workflow.runtime.v1",
        "runtime.cline.0.0.65",
    ];

    [Fact]
    public void AcceptsSidecarThatAdvertisesEveryRequiredCapability()
    {
        var result = CompatibleResult(CompatibleCapabilities);

        SidecarSupervisor.ValidateHandshake(result);
    }

    [Theory]
    [InlineData("agent.events")]
    [InlineData("capability.runtime.snapshot.v2")]
    [InlineData("capability.proposal.generate")]
    [InlineData("workflow.runtime.v1")]
    [InlineData("runtime.cline.0.0.65")]
    public void RejectsSidecarThatOmitsARequiredCapability(string missingCapability)
    {
        var capabilities = CompatibleCapabilities
            .Where(capability => !StringComparer.Ordinal.Equals(capability, missingCapability))
            .ToArray();

        var exception = Assert.Throws<InvalidDataException>(
            () => SidecarSupervisor.ValidateHandshake(CompatibleResult(capabilities)));

        Assert.Equal($"Sidecar is missing required capabilities: {missingCapability}.", exception.Message);
    }

    [Fact]
    public void CapabilityNegotiationUsesOrdinalNames()
    {
        var capabilities = CompatibleCapabilities
            .Select(capability => StringComparer.Ordinal.Equals(capability, "agent.events")
                ? "AGENT.EVENTS"
                : capability)
            .ToArray();

        var exception = Assert.Throws<InvalidDataException>(
            () => SidecarSupervisor.ValidateHandshake(CompatibleResult(capabilities)));

        Assert.Contains("agent.events", exception.Message, StringComparison.Ordinal);
    }

    private static InitializeResult CompatibleResult(IReadOnlyList<string> capabilities) =>
        new(
            ProtocolConstants.ProtocolVersion,
            "test-sidecar",
            ProtocolConstants.ContractHash,
            capabilities);
}
