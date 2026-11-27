using LIGClaw.Application.Capabilities;

namespace LIGClaw.Application.Tests;

public sealed class CapabilityRuntimeInputSchemaPolicyTests
{
    private const string StringArraySchema =
        "{\"additionalProperties\":false,\"properties\":{\"values\":{\"items\":{\"maxLength\":16,\"type\":\"string\"},\"maxItems\":4,\"type\":\"array\",\"uniqueItems\":UNIQUE_ITEMS}},\"type\":\"object\"}";

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("1", false)]
    [InlineData("\"true\"", false)]
    public void UniqueItemsMustBeBooleanWhenDeclaredOnAStringArray(string keywordJson, bool expected)
    {
        var schema = StringArraySchema.Replace("UNIQUE_ITEMS", keywordJson, StringComparison.Ordinal);

        Assert.Equal(expected, CapabilityRuntimeInputSchemaPolicy.HasBoundedObjectRoot(schema));
    }

    [Theory]
    [InlineData("{\"maxLength\":16,\"type\":\"string\"}", true)]
    [InlineData("{\"type\":\"integer\"}", false)]
    [InlineData("{\"type\":\"boolean\"}", false)]
    public void UniqueItemsTrueIsRestrictedToStringItemSchemas(string itemSchemaJson, bool expected)
    {
        var schema =
            $"{{\"additionalProperties\":false,\"properties\":{{\"values\":{{\"items\":{itemSchemaJson},\"maxItems\":4,\"type\":\"array\",\"uniqueItems\":true}}}},\"type\":\"object\"}}";

        Assert.Equal(expected, CapabilityRuntimeInputSchemaPolicy.HasBoundedObjectRoot(schema));
    }

    [Fact]
    public void UniqueItemsFalseAllowsNonStringItemSchemas()
    {
        const string schema =
            "{\"additionalProperties\":false,\"properties\":{\"values\":{\"items\":{\"type\":\"integer\"},\"maxItems\":4,\"type\":\"array\",\"uniqueItems\":false}},\"type\":\"object\"}";

        Assert.True(CapabilityRuntimeInputSchemaPolicy.HasBoundedObjectRoot(schema));
    }

    [Theory]
    [InlineData("{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\",\"uniqueItems\":false}")]
    [InlineData("{\"additionalProperties\":false,\"properties\":{\"value\":{\"maxLength\":16,\"type\":\"string\",\"uniqueItems\":false}},\"type\":\"object\"}")]
    [InlineData("{\"additionalProperties\":false,\"properties\":{\"value\":{\"type\":\"boolean\",\"uniqueItems\":true}},\"type\":\"object\"}")]
    public void UniqueItemsIsRejectedOutsideArraySchemas(string schema)
    {
        Assert.False(CapabilityRuntimeInputSchemaPolicy.HasBoundedObjectRoot(schema));
    }
}
