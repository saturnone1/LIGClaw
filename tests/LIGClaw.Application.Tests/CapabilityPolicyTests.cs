using LIGClaw.Application.Capabilities;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class CapabilityPolicyTests
{
    [Theory]
    [InlineData("company.mail.send")]
    [InlineData("com.example.mail-send")]
    [InlineData("a.0")]
    public void AcceptsBoundedReverseDnsCapabilityIds(string value)
    {
        Assert.True(CapabilityId.TryParse(value, out var parsed));
        Assert.Equal(value, parsed!.Value);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("Company.mail")]
    [InlineData("company..mail")]
    [InlineData("company.-mail")]
    [InlineData("company.mail-")]
    [InlineData("company.mail_send")]
    public void RejectsNonCanonicalCapabilityIds(string value)
    {
        Assert.False(CapabilityId.TryParse(value, out _));
    }

    [Fact]
    public void EnforcesCapabilityIdSegmentAndTotalBounds()
    {
        Assert.False(CapabilityId.TryParse($"company.{new string('a', 33)}", out _));
        Assert.False(CapabilityId.TryParse(string.Join('.', Enumerable.Repeat("aaaaaaaaaaaaaaaa", 9)), out _));
        Assert.False(CapabilityId.TryParse($"a.{new string('b', 127)}", out _));
    }

    [Fact]
    public void ImplementsSemVerPrereleasePrecedence()
    {
        var ordered = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
        }.Select(CapabilitySemanticVersion.Parse).ToArray();

        for (var index = 0; index < ordered.Length - 1; index++)
            Assert.True(ordered[index].CompareTo(ordered[index + 1]) < 0);
        Assert.Equal(0, CapabilitySemanticVersion.Parse("1.0.0+build.1")
            .CompareTo(CapabilitySemanticVersion.Parse("1.0.0+build.2")));
    }

    [Theory]
    [InlineData("01.0.0")]
    [InlineData("1.00.0")]
    [InlineData("1.0.00")]
    [InlineData("1.0.0-alpha.01")]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    public void RejectsNonCanonicalSemVer(string value)
    {
        Assert.False(CapabilitySemanticVersion.TryParse(value, out _));
    }

    [Fact]
    public void SemVerAndExactReferencesUseValueEquality()
    {
        var leftVersion = CapabilitySemanticVersion.Parse("1.2.3-rc.1+build.7");
        var rightVersion = CapabilitySemanticVersion.Parse("1.2.3-rc.1+build.7");
        var hash = Hash('a');
        var id = CapabilityId.Parse("company.sample");

        Assert.Equal(leftVersion, rightVersion);
        Assert.Equal(leftVersion.GetHashCode(), rightVersion.GetHashCode());
        Assert.Equal(
            new CapabilityVersionRef(id, leftVersion, hash),
            new CapabilityVersionRef(CapabilityId.Parse("company.sample"), rightVersion, Hash('a')));
        Assert.NotEqual(
            CapabilitySemanticVersion.Parse("1.2.3+build.1"),
            CapabilitySemanticVersion.Parse("1.2.3+build.2"));
    }

    [Fact]
    public void CapabilityValueCollectionsCannotBeMutatedThroughCallerOwnedArrays()
    {
        var intentExamples = new[] { "처음 의도" };
        var requiredTools = new[] { "system.get_status.v1" };
        var prereleaseSource = CapabilitySemanticVersion.Parse("1.0.0-alpha.1");
        var manifest = Payload().Manifest with
        {
            IntentExamples = intentExamples,
            RequiredTools = requiredTools,
        };

        intentExamples[0] = "변조된 의도";
        requiredTools[0] = "tampered.tool.v1";

        Assert.Equal("처음 의도", Assert.Single(manifest.IntentExamples));
        Assert.Equal("system.get_status.v1", Assert.Single(manifest.RequiredTools));
        Assert.False(prereleaseSource.Prerelease is string[]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)prereleaseSource.Prerelease)[0] = "tampered");
    }

    [Fact]
    public void AcceptsOnlyLowercasePrefixedSha256Hashes()
    {
        Assert.True(CapabilityContentHash.TryParse("sha256:" + new string('0', 64), out _));
        Assert.False(CapabilityContentHash.TryParse(new string('0', 64), out _));
        Assert.False(CapabilityContentHash.TryParse("sha256:" + new string('A', 64), out _));
        Assert.False(CapabilityContentHash.TryParse("sha256:" + new string('0', 63), out _));
    }

    [Fact]
    public void DefinitionPolicyEnforcesBoundsAndPromptOnlyPayloadShape()
    {
        var valid = Payload();
        Assert.True(CapabilityDefinitionPolicy.Validate(valid).IsValid);

        Assert.False(CapabilityDefinitionPolicy.Validate(valid with
        {
            Manifest = valid.Manifest with { DisplayName = new string('x', 81) },
        }).IsValid);
        Assert.False(CapabilityDefinitionPolicy.Validate(valid with
        {
            Instructions = new string('x', CapabilityDefinitionPolicy.MaximumInstructionsCharacters + 1),
        }).IsValid);
        Assert.Equal(
            "capability.prompt.executable_not_allowed",
            CapabilityDefinitionPolicy.Validate(valid with { CanonicalWorkflowJson = "{}" }).ErrorCode);

        var workflow = valid with
        {
            Manifest = valid.Manifest with
            {
                Kind = CapabilityKind.Workflow,
                Entrypoint = "workflow.json",
            },
            CanonicalWorkflowJson = "{}",
        };
        Assert.True(CapabilityDefinitionPolicy.Validate(workflow).IsValid);
        Assert.Equal(
            "capability.workflow.entrypoint_invalid",
            CapabilityDefinitionPolicy.Validate(workflow with { CanonicalWorkflowJson = null }).ErrorCode);
        Assert.Equal(
            "capability.workflow.entrypoint_invalid",
            CapabilityDefinitionPolicy.Validate(workflow with
            {
                Manifest = workflow.Manifest with { Entrypoint = "instructions.md" },
            }).ErrorCode);
    }

    [Fact]
    public void LifecycleAllowsOnlyForwardReviewTransitions()
    {
        Assert.True(CapabilityLifecyclePolicy.CanTransition(CapabilityDraftState.Draft, CapabilityDraftState.Validating));
        Assert.True(CapabilityLifecyclePolicy.CanTransition(CapabilityDraftState.Validating, CapabilityDraftState.ReadyForReview));
        Assert.True(CapabilityLifecyclePolicy.CanTransition(CapabilityDraftState.ReadyForReview, CapabilityDraftState.Applied));
        Assert.True(CapabilityLifecyclePolicy.CanTransition(CapabilityDraftState.ReadyForReview, CapabilityDraftState.Stale));
        Assert.False(CapabilityLifecyclePolicy.CanTransition(CapabilityDraftState.Draft, CapabilityDraftState.Applied));
        Assert.False(CapabilityLifecyclePolicy.CanTransition(CapabilityDraftState.Applied, CapabilityDraftState.ReadyForReview));
        Assert.False(CapabilityLifecyclePolicy.CanTransition(CapabilityDraftState.Stale, CapabilityDraftState.Applied));
    }

    [Fact]
    public void ContentHashNormalizesInstructionNewlinesButBindsContent()
    {
        var windows = Payload() with { Instructions = "첫 줄\r\n둘째 줄\r끝" };
        var unix = windows with { Instructions = "첫 줄\n둘째 줄\n끝" };
        var changed = unix with { Instructions = "첫 줄\n둘째 줄\n다름" };

        Assert.Equal(CapabilityContentHasher.Compute(windows), CapabilityContentHasher.Compute(unix));
        Assert.NotEqual(CapabilityContentHasher.Compute(unix), CapabilityContentHasher.Compute(changed));
    }

    [Theory]
    [InlineData("apiKey", "const", "correct-horse-battery-staple")]
    [InlineData("client_secret", "const", "ordinary-looking-secret")]
    [InlineData("accessToken", "enum", "token-without-a-known-prefix")]
    public void IngressPolicyRejectsLiteralsBoundToSensitiveSchemaFields(
        string fieldName,
        string keyword,
        string literal)
    {
        var value = keyword == "enum" ? $"[\"{literal}\"]" : $"\"{literal}\"";
        var schema =
            $"{{\"type\":\"object\",\"properties\":{{\"{fieldName}\":{{\"type\":\"string\",\"{keyword}\":{value}}}}}}}";

        Assert.True(CapabilityIngressPolicy.ContainsSecretLiteral(Payload() with
        {
            CanonicalInputSchemaJson = schema,
        }));
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"properties\":{\"token\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"enum\":[\"ordinary-looking-secret\"]}}}}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"apiKey\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"const\":\"ordinary-looking-secret\"}}}}}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"authorization\":{\"type\":\"string\",\"const\":\"ordinary-looking-secret\"}}}")]
    public void IngressPolicyPropagatesSensitiveContextThroughNestedSchemas(string schema)
    {
        Assert.True(CapabilityIngressPolicy.ContainsSecretLiteral(Payload() with
        {
            CanonicalInputSchemaJson = schema,
        }));
    }

    [Fact]
    public void IngressPolicyAllowsSensitiveSchemaShapeOnlyWithRedactedFixtureValues()
    {
        var shapeOnly = Payload() with
        {
            CanonicalInputSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"apiKey\":{\"type\":\"string\",\"maxLength\":128}}}",
        };
        var placeholder = Payload() with
        {
            CanonicalSettingsSchemaJson =
                "{\"type\":\"object\",\"properties\":{\"clientSecret\":{\"type\":\"string\",\"const\":\"${fixture:mail-secret}\"}}}",
        };

        Assert.False(CapabilityIngressPolicy.ContainsSecretLiteral(shapeOnly));
        Assert.False(CapabilityIngressPolicy.ContainsSecretLiteral(placeholder));
    }

    [Theory]
    [InlineData("token=${fixture:mail-secret}hunter2")]
    [InlineData("password=${fixture:mail-secret}ordinary-secret")]
    public void IngressPolicyDoesNotLetAPlaceholderPrefixHideTrailingSecretText(string instructions)
    {
        Assert.True(CapabilityIngressPolicy.ContainsSecretLiteral(Payload(instructions: instructions)));
    }

    [Fact]
    public void IngressPolicyRejectsConnectionDetailsAcrossEveryProjectedPayloadSurface()
    {
        var baseline = Payload();
        var cases = new[]
        {
            baseline with
            {
                Manifest = baseline.Manifest with { DisplayName = "https://display.internal.invalid/v1" },
            },
            baseline with
            {
                Manifest = baseline.Manifest with { Description = "profileId=desktop-mail-prod" },
            },
            baseline with
            {
                Manifest = baseline.Manifest with { IntentExamples = ["auth=bearer"] },
            },
            baseline with { Instructions = "Use https://instructions.internal.invalid/v1." },
            baseline with
            {
                CanonicalManifestJson =
                    "{\"description\":\"endpoint=internal-mail.example.invalid\"}",
            },
            baseline with
            {
                CanonicalInputSchemaJson =
                    "{\"type\":\"object\",\"properties\":{\"auth\":{\"type\":\"string\",\"enum\":[\"bearer\"]}}}",
            },
        };

        Assert.All(cases, payload =>
        {
            Assert.True(CapabilityIngressPolicy.ContainsConnectionLiteral(payload));
            Assert.Equal(
                CapabilityIngressViolation.ConnectionLiteral,
                CapabilityIngressPolicy.GetViolation(payload));
        });
    }

    [Fact]
    public void IngressPolicyAllowsLogicalConnectionAndRelativeTemplateMetadata()
    {
        var baseline = Payload();
        var payload = baseline with
        {
            Manifest = baseline.Manifest with
            {
                Kind = CapabilityKind.Workflow,
                DisplayName = "Internal status workflow",
                Description = "Uses one Desktop-registered logical connection.",
                IntentExamples = ["Check the internal service status"],
                RequiredTools = ["connection.http.request.v1"],
                RequiredConnections = [new CapabilityConnectionRequirement("service", "http")],
                MaximumRisk = "R3",
                Entrypoint = "workflow.json",
            },
            CanonicalManifestJson =
                "{\"description\":\"Uses one registered logical connection.\",\"requiredConnections\":[{\"kind\":\"http\",\"slot\":\"service\"}],\"requiredTools\":[\"connection.http.request.v1\"]}",
            Instructions = "Use only the declared logical connection and bounded relative route.",
            CanonicalInputSchemaJson =
                "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"additionalProperties\":false,\"properties\":{\"userId\":{\"maxLength\":64,\"type\":\"string\"}},\"type\":\"object\"}",
            CanonicalWorkflowJson =
                "{\"entryStepId\":\"request\",\"schemaVersion\":1,\"steps\":[{\"arguments\":{\"connectionSlot\":\"service\",\"method\":\"GET\",\"pathParameters\":[{\"name\":\"userId\",\"value\":\"sample-user\"}],\"pathTemplate\":\"users/{userId}/status\",\"queryParameters\":[{\"name\":\"view\",\"value\":\"summary\"}],\"reason\":\"Check service status\"},\"id\":\"request\",\"next\":\"done\",\"tool\":\"connection.http.request.v1\",\"type\":\"tool.call\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}",
        };

        Assert.False(CapabilityIngressPolicy.ContainsConnectionLiteral(payload));
        Assert.Equal(CapabilityIngressViolation.None, CapabilityIngressPolicy.GetViolation(payload));
    }

    [Fact]
    public void IngressPolicyRejectsSplitEndpointCredentialTargetAndSchemaComponentSet()
    {
        var baseline = Payload();
        var cases = new[]
        {
            baseline with
            {
                Instructions = "Use host mail.internal.invalid on port 8443 over HTTPS.",
            },
            baseline with
            {
                Instructions = "scheme=https host=mail.internal.invalid port=8443",
            },
            baseline with
            {
                Instructions =
                    "Use credential target LIGClaw/Connections/boundary-mail-profile/BearerToken.",
            },
            baseline with
            {
                CanonicalInputSchemaJson =
                    "{\"type\":\"object\",\"properties\":{\"host\":{\"type\":\"string\",\"const\":\"mail.internal.invalid\"},\"port\":{\"type\":\"integer\",\"default\":8443},\"scheme\":{\"type\":\"string\",\"const\":\"https\"}}}",
            },
        };

        Assert.All(cases, payload => Assert.Equal(
            CapabilityIngressViolation.ConnectionLiteral,
            CapabilityIngressPolicy.GetViolation(payload)));
    }

    [Fact]
    public void IngressPolicyKeepsSecretViolationPriorityForMixedUnsafePayloads()
    {
        var payload = Payload(
            instructions: "endpoint=https://internal.invalid token=ordinary-secret-value");

        Assert.True(CapabilityIngressPolicy.ContainsSecretLiteral(payload));
        Assert.True(CapabilityIngressPolicy.ContainsConnectionLiteral(payload));
        Assert.Equal(CapabilityIngressViolation.SecretLiteral, CapabilityIngressPolicy.GetViolation(payload));
    }

    internal static CapabilityVersionPayload Payload(
        string toolName = "system.get_status.v1",
        string maximumRisk = "R0",
        string instructions = "등록된 도구로 현재 상태를 설명합니다.",
        IReadOnlyList<CapabilityConnectionRequirement>? connections = null) =>
        new(
            new CapabilityManifest(
                1,
                CapabilityId.Parse("company.sample.status"),
                CapabilitySemanticVersion.Parse("1.0.0"),
                "상태 설명",
                "현재 상태를 안전하게 설명합니다.",
                CapabilityKind.Prompt,
                ["현재 상태를 알려줘"],
                [toolName],
                connections ?? [],
                maximumRisk,
                "instructions.md"),
            "{\"schemaVersion\":1}",
            instructions,
            "{\"type\":\"object\"}",
            "{\"type\":\"object\"}",
            null,
            "[]");

    internal static CapabilityContentHash Hash(char value) =>
        CapabilityContentHash.Parse("sha256:" + new string(value, 64));
}
