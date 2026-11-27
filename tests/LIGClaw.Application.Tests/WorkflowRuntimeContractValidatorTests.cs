using System.Security.Cryptography;
using System.Text;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class WorkflowRuntimeContractValidatorTests
{
    [Fact]
    public void CanonicalHashIsStableAcrossObjectInsertionOrderAndBounded()
    {
        var left = new WorkflowObjectValue(new Dictionary<string, WorkflowValue>(StringComparer.Ordinal)
        {
            ["second"] = new WorkflowNumberValue("2", true),
            ["first"] = new WorkflowStringValue("one"),
        });
        var right = new WorkflowObjectValue(new Dictionary<string, WorkflowValue>(StringComparer.Ordinal)
        {
            ["first"] = new WorkflowStringValue("one"),
            ["second"] = new WorkflowNumberValue("2", true),
        });

        Assert.True(WorkflowRuntimeContractValidator.TryComputeCanonicalHash(left, 1_024, out var leftHash));
        Assert.True(WorkflowRuntimeContractValidator.TryComputeCanonicalHash(right, 1_024, out var rightHash));
        Assert.Equal(leftHash, rightHash);
        Assert.False(WorkflowRuntimeContractValidator.TryComputeCanonicalHash(left, 2, out _));
    }

    [Fact]
    public void CanonicalHashRejectsAggregateOverflowAndMalformedPublicValuesWithoutThrowing()
    {
        var aggregate = new WorkflowArrayValue(
            Enumerable.Repeat<WorkflowValue>(new WorkflowStringValue(new string('x', 4_096)), 17).ToArray());
        var malformedNumber = new WorkflowNumberValue("1.0", true);
        var nullText = new WorkflowStringValue(null!);

        Assert.False(WorkflowRuntimeContractValidator.TryComputeCanonicalHash(aggregate, 65_536, out _));
        Assert.False(WorkflowRuntimeContractValidator.TryComputeCanonicalHash(malformedNumber, 128, out _));
        Assert.False(WorkflowRuntimeContractValidator.TryComputeCanonicalHash(nullText, 128, out _));
    }

    [Theory]
    [InlineData("date-time", "2026-08-02T13:14:15Z", true)]
    [InlineData("date-time", "2026-08-02T13:14:15.1234567+09:00", true)]
    [InlineData("date-time", "2026-08-02 13:14:15+09:00", false)]
    [InlineData("date-time", "2026-08-02T13:14+09:00", false)]
    [InlineData("date-time", "2026-08-02T13:14:15+0900", false)]
    [InlineData("email", "user@example.test", true)]
    [InlineData("email", "user name@example.test", false)]
    [InlineData("email", "user@example..test", false)]
    [InlineData("email", "user@-example.test", false)]
    public void ContractFormatsUseStrictBoundedSyntax(string format, string text, bool expected)
    {
        var schema =
            $"{{\"additionalProperties\":false,\"properties\":{{\"value\":{{\"format\":\"{format}\",\"type\":\"string\"}}}},\"required\":[\"value\"],\"type\":\"object\"}}";
        var hash = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema))).ToLowerInvariant()}";
        var value = new WorkflowObjectValue(new Dictionary<string, WorkflowValue>
        {
            ["value"] = new WorkflowStringValue(text),
        });

        Assert.Equal(expected, WorkflowRuntimeContractValidator.ValidateObject(schema, hash, value));
    }

    [Fact]
    public void UniqueStringArraysAllowDistinctValuesAndRejectDuplicates()
    {
        const string schema =
            "{\"additionalProperties\":false,\"properties\":{\"values\":{\"items\":{\"type\":\"string\"},\"maxItems\":4,\"type\":\"array\",\"uniqueItems\":true}},\"required\":[\"values\"],\"type\":\"object\"}";

        Assert.True(Validate(schema, StringArray("first", "second")));
        Assert.False(Validate(schema, StringArray("same", "same")));
    }

    [Fact]
    public void UniqueItemsFalseAllowsDuplicateValues()
    {
        const string schema =
            "{\"additionalProperties\":false,\"properties\":{\"values\":{\"items\":{\"type\":\"string\"},\"maxItems\":4,\"type\":\"array\",\"uniqueItems\":false}},\"required\":[\"values\"],\"type\":\"object\"}";

        Assert.True(Validate(schema, StringArray("same", "same")));
    }

    [Theory]
    [InlineData("{\"items\":{\"type\":\"string\"},\"maxItems\":4,\"type\":\"array\",\"uniqueItems\":1}")]
    [InlineData("{\"items\":{\"type\":\"integer\"},\"maxItems\":4,\"type\":\"array\",\"uniqueItems\":true}")]
    [InlineData("{\"maxLength\":16,\"type\":\"string\",\"uniqueItems\":true}")]
    public void RejectsUnsupportedUniqueItemsSchemaShapes(string propertySchema)
    {
        var schema =
            $"{{\"additionalProperties\":false,\"properties\":{{\"values\":{propertySchema}}},\"required\":[\"values\"],\"type\":\"object\"}}";

        Assert.False(Validate(schema, StringArray("value")));
    }

    [Fact]
    public void GeneratedMailDraftInputSchemaParsesAndValidatesAtRuntime()
    {
        Assert.True(ToolSchemaRegistry.TryGet("mail.create_draft.v1", out var schemas));
        var value = new WorkflowObjectValue(new Dictionary<string, WorkflowValue>(StringComparer.Ordinal)
        {
            ["to"] = new WorkflowArrayValue(
                [new WorkflowStringValue("first@example.test"), new WorkflowStringValue("second@example.test")]),
            ["cc"] = new WorkflowArrayValue([]),
            ["bcc"] = new WorkflowArrayValue([]),
            ["subject"] = new WorkflowStringValue("Bounded draft"),
            ["body"] = new WorkflowStringValue("Draft body"),
        });

        Assert.True(Validate(schemas!.CanonicalInputSchemaJson, value));
    }

    private static WorkflowObjectValue StringArray(params string[] values) =>
        new(new Dictionary<string, WorkflowValue>(StringComparer.Ordinal)
        {
            ["values"] = new WorkflowArrayValue(values.Select(value => new WorkflowStringValue(value)).ToArray()),
        });

    private static bool Validate(string schema, WorkflowObjectValue value) =>
        WorkflowRuntimeContractValidator.ValidateObject(schema, Hash(schema), value);

    private static string Hash(string schema) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema))).ToLowerInvariant()}";
}
