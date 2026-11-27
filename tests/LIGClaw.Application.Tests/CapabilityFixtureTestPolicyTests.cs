using LIGClaw.Application.Capabilities;

namespace LIGClaw.Application.Tests;

public sealed class CapabilityFixtureTestPolicyTests
{
    [Fact]
    public void AcceptsBoundedCanonicalFixtureCasesAndProjectsInputs()
    {
        const string tests =
            "[{\"expected\":\"accepted\",\"input\":{\"count\":2,\"text\":\"sample\"},\"name\":\"accept sample\"}," +
            "{\"expected\":\"rejected\",\"input\":{},\"name\":\"reject empty\"}]";

        var result = CapabilityFixtureTestPolicy.Validate(tests);

        Assert.True(result.IsValid, result.ErrorCode);
        Assert.Null(result.ErrorCode);
        Assert.Collection(
            result.Cases,
            item =>
            {
                Assert.Equal("accept sample", item.Name);
                Assert.Equal("accepted", item.Expected);
                Assert.Equal("{\"count\":2,\"text\":\"sample\"}", item.CanonicalInputJson);
            },
            item =>
            {
                Assert.Equal("reject empty", item.Name);
                Assert.Equal("rejected", item.Expected);
                Assert.Equal("{}", item.CanonicalInputJson);
            });
    }

    [Theory]
    [InlineData("{}", "capability.tests.root_invalid")]
    [InlineData("null", "capability.tests.root_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{},\"Name\":\"wrong case\"}]", "capability.tests.case_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{},\"name\":\"one\",\"name\":\"two\"}]", "capability.tests.case_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{},\"name\":\"one\",\"Name\":\"two\"}]", "capability.tests.case_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{\"value\":1,\"value\":2},\"name\":\"nested duplicate\"}]", "capability.tests.case_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{\"value\":1,\"Value\":2},\"name\":\"nested case collision\"}]", "capability.tests.case_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{},\"name\":\" blank edges \"}]", "capability.tests.case_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{},\"name\":\"line\\nbreak\"}]", "capability.tests.case_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":{},\"name\":\"line\\u2028break\"}]", "capability.tests.case_invalid")]
    [InlineData("[{\"expected\":\"unknown\",\"input\":{},\"name\":\"bad expected\"}]", "capability.tests.case_invalid")]
    [InlineData("[{\"expected\":\"accepted\",\"input\":[],\"name\":\"bad input\"}]", "capability.tests.case_invalid")]
    [InlineData("[", "capability.tests.json_invalid")]
    public void RejectsInvalidRootsFieldsCaseDuplicatesAndSyntax(string tests, string expectedCode)
    {
        var result = CapabilityFixtureTestPolicy.Validate(tests);

        Assert.False(result.IsValid);
        Assert.Empty(result.Cases);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Fact]
    public void RejectsCaseInsensitiveDuplicateCaseNames()
    {
        const string tests =
            "[{\"expected\":\"accepted\",\"input\":{},\"name\":\"Same case\"}," +
            "{\"expected\":\"rejected\",\"input\":{},\"name\":\"same CASE\"}]";

        var result = CapabilityFixtureTestPolicy.Validate(tests);

        Assert.False(result.IsValid);
        Assert.Equal("capability.tests.case_invalid", result.ErrorCode);
    }

    [Fact]
    public void RejectsSeventeenCases()
    {
        var tests = "[" + string.Join(
            ',',
            Enumerable.Range(1, CapabilityFixtureTestPolicy.MaximumCases + 1)
                .Select(index =>
                    $"{{\"expected\":\"accepted\",\"input\":{{}},\"name\":\"case {index}\"}}")) + "]";

        var result = CapabilityFixtureTestPolicy.Validate(tests);

        Assert.False(result.IsValid);
        Assert.Equal("capability.tests.root_invalid", result.ErrorCode);
    }

    [Fact]
    public void AcceptsBoundedWorkflowSemanticV2AndProjectsCanonicalValues()
    {
        const string tests =
            """
            {
              "schemaVersion": 2,
              "cases": [
                {
                  "name": "metadata detail branch",
                  "input": { "path": "C:\\fixture\\report.txt", "includeDetail": true },
                  "toolResults": [
                    {
                      "stepId": "metadata",
                      "tool": "file.get_metadata.v1",
                      "arguments": { "path": "C:\\fixture\\report.txt" },
                      "output": {
                        "path": "C:\\fixture\\report.txt",
                        "name": "report.txt",
                        "isDirectory": false,
                        "sizeBytes": 42,
                        "lastWriteTimeUtc": "2026-08-02T00:00:00Z",
                        "readOnly": true
                      }
                    }
                  ],
                  "expected": {
                    "status": "completed",
                    "output": { "name": "report.txt" },
                    "visitedSteps": ["metadata", "choose", "detail", "done"]
                  }
                }
              ]
            }
            """;

        var result = CapabilityFixtureTestPolicy.Validate(tests);

        Assert.True(result.IsValid, result.ErrorCode);
        Assert.Empty(result.Cases);
        Assert.Equal(1, result.CaseCount);
        var testCase = Assert.Single(result.WorkflowSemanticCases);
        Assert.Equal("metadata detail branch", testCase.Name);
        Assert.Equal(
            "{ \"path\": \"C:\\\\fixture\\\\report.txt\", \"includeDetail\": true }",
            testCase.CanonicalInputJson);
        var toolResult = Assert.Single(testCase.ToolResults);
        Assert.Equal("metadata", toolResult.StepId);
        Assert.Equal("file.get_metadata.v1", toolResult.ToolName);
        Assert.Equal("completed", testCase.Expected.Status);
        Assert.Equal(["metadata", "choose", "detail", "done"], testCase.Expected.VisitedSteps);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"cases\":[]}", "capability.tests.root_invalid")]
    [InlineData("{\"schemaVersion\":2,\"cases\":[]}", "capability.tests.root_invalid")]
    [InlineData("{\"schemaVersion\":2,\"cases\":[{\"name\":\"bad\",\"input\":{},\"toolResults\":[],\"expected\":{\"status\":\"completed\",\"output\":null,\"visitedSteps\":[\"done\"]}}]}", "capability.tests.semantic_case_invalid")]
    [InlineData("{\"schemaVersion\":2,\"cases\":[{\"name\":\"bad\",\"input\":{},\"toolResults\":[],\"expected\":{\"status\":\"failed\",\"output\":{},\"visitedSteps\":[\"done\"]}}]}", "capability.tests.semantic_case_invalid")]
    [InlineData("{\"schemaVersion\":2,\"cases\":[{\"name\":\"bad\",\"input\":{},\"toolResults\":[],\"expected\":{\"status\":\"completed\",\"output\":{},\"visitedSteps\":[\"done\",\"done\"]}}]}", "capability.tests.semantic_case_invalid")]
    public void RejectsInvalidWorkflowSemanticV2Shapes(string tests, string expectedCode)
    {
        var result = CapabilityFixtureTestPolicy.Validate(tests);

        Assert.False(result.IsValid);
        Assert.Empty(result.WorkflowSemanticCases);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Fact]
    public void RejectsWorkflowSemanticV2BeyondToolAndTraceBounds()
    {
        var toolResults = Enumerable.Range(0, CapabilityFixtureTestPolicy.MaximumSemanticToolResults + 1)
            .Select(index => new
            {
                stepId = $"tool_{index}",
                tool = "file.get_metadata.v1",
                arguments = new { path = "C:\\fixture\\report.txt" },
                output = new
                {
                    path = "C:\\fixture\\report.txt",
                    name = "report.txt",
                    isDirectory = false,
                    sizeBytes = 42,
                    lastWriteTimeUtc = "2026-08-02T00:00:00Z",
                    readOnly = true,
                },
            })
            .ToArray();
        var tests = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            cases = new[]
            {
                new
                {
                    name = "too many tools",
                    input = new { },
                    toolResults,
                    expected = new
                    {
                        status = "completed",
                        output = new { },
                        visitedSteps = toolResults.Select(result => result.stepId).ToArray(),
                    },
                },
            },
        });

        var visitedSteps = Enumerable.Range(0, CapabilityFixtureTestPolicy.MaximumSemanticVisitedSteps + 1)
            .Select(index => $"step_{index}")
            .ToArray();
        var excessiveTrace = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            cases = new[]
            {
                new
                {
                    name = "too many visited steps",
                    input = new { },
                    toolResults = Array.Empty<object>(),
                    expected = new
                    {
                        status = "completed",
                        output = new { },
                        visitedSteps,
                    },
                },
            },
        });

        Assert.Equal(
            "capability.tests.semantic_case_invalid",
            CapabilityFixtureTestPolicy.Validate(tests).ErrorCode);
        Assert.Equal(
            "capability.tests.semantic_case_invalid",
            CapabilityFixtureTestPolicy.Validate(excessiveTrace).ErrorCode);
    }

    [Fact]
    public void RejectsWorkflowSemanticV2OversizedInputAndOutputValues()
    {
        var oversizedInput = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            cases = new[]
            {
                new
                {
                    name = "oversized input",
                    input = new { value = new string('x', (32 * 1024) + 1) },
                    toolResults = Array.Empty<object>(),
                    expected = new
                    {
                        status = "completed",
                        output = new { },
                        visitedSteps = new[] { "done" },
                    },
                },
            },
        });
        var oversizedOutput = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            cases = new[]
            {
                new
                {
                    name = "oversized output",
                    input = new { },
                    toolResults = Array.Empty<object>(),
                    expected = new
                    {
                        status = "completed",
                        output = new { value = new string('x', (100 * 1024) + 1) },
                        visitedSteps = new[] { "done" },
                    },
                },
            },
        });

        Assert.Equal(
            "capability.tests.semantic_case_invalid",
            CapabilityFixtureTestPolicy.Validate(oversizedInput).ErrorCode);
        Assert.Equal(
            "capability.tests.semantic_case_invalid",
            CapabilityFixtureTestPolicy.Validate(oversizedOutput).ErrorCode);
    }
}
