using LIGClaw.Desktop.Infrastructure.Workflows;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class WorkflowToolValueConverterTests
{
    [Fact]
    public void RoundTripsSupportedImmutableToolValues()
    {
        var value = new WorkflowObjectValue(new Dictionary<string, WorkflowValue>(StringComparer.Ordinal)
        {
            ["enabled"] = new WorkflowBooleanValue(true),
            ["items"] = new WorkflowArrayValue([
                new WorkflowStringValue("text"),
                new WorkflowNumberValue("42", true),
                WorkflowNullValue.Instance,
            ]),
        });

        Assert.True(WorkflowToolValueConverter.TryToToolInput(value, out var input));
        Assert.True(WorkflowToolValueConverter.TryFromToolOutput(input, out var roundTrip));
        Assert.True(Assert.IsType<WorkflowBooleanValue>(roundTrip!.Properties["enabled"]).Value);
        var items = Assert.IsType<WorkflowArrayValue>(roundTrip.Properties["items"]);
        Assert.Equal("text", Assert.IsType<WorkflowStringValue>(items.Items[0]).Value);
        Assert.Equal("42", Assert.IsType<WorkflowNumberValue>(items.Items[1]).CanonicalValue);
        Assert.IsType<WorkflowNullValue>(items.Items[2]);
    }

    [Fact]
    public void RejectsCyclesUnsupportedObjectsAndUnsafeIntegers()
    {
        var cyclic = new Dictionary<string, object?>(StringComparer.Ordinal);
        cyclic["self"] = cyclic;

        Assert.False(WorkflowToolValueConverter.TryFromToolOutput(cyclic, out _));
        Assert.False(WorkflowToolValueConverter.TryFromToolOutput(
            new Dictionary<string, object?> { ["value"] = new object() },
            out _));
        Assert.False(WorkflowToolValueConverter.TryFromToolOutput(
            new Dictionary<string, object?> { ["value"] = 9_007_199_254_740_992L },
            out _));
    }

    [Fact]
    public void ConvertsDesktopTimestampsToRoundTripContractStrings()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-02T13:14:15.1234567+09:00");

        Assert.True(WorkflowToolValueConverter.TryFromToolOutput(
            new Dictionary<string, object?> { ["localTime"] = timestamp },
            out var converted));

        Assert.Equal(
            "2026-08-02T13:14:15.1234567+09:00",
            Assert.IsType<WorkflowStringValue>(converted!.Properties["localTime"]).Value);
    }
}
