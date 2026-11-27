using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Application.Workflows;
using LIGClaw.Contracts.Generated;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class CapabilityCatalogServiceTests
{
    [Fact]
    public async Task RejectsSecretLiteralBeforeCallingRepository()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, ("system.get_status.v1", "R0"));
        var draft = Draft(CapabilityPolicyTests.Payload(instructions: "password=supersecret"));

        var result = await service.SubmitValidatedDraftAsync(draft, Now, CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Invalid, result.Outcome);
        Assert.Equal("capability.secret_literal.detected", result.ErrorCode);
        Assert.Equal(0, repository.CreateCalls);
    }

    [Fact]
    public async Task RejectsConnectionLiteralBeforeCallingRepository()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, ("system.get_status.v1", "R0"));
        var draft = Draft(CapabilityPolicyTests.Payload(
            instructions: "endpoint=https://catalog.internal.invalid/v1"));

        var result = await service.SubmitValidatedDraftAsync(draft, Now, CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Invalid, result.Outcome);
        Assert.Equal("capability.connection_literal.detected", result.ErrorCode);
        Assert.Equal(0, repository.CreateCalls);
    }

    [Fact]
    public async Task RejectsUnknownToolBeforeCallingRepository()
    {
        var repository = new RecordingRepository();
        var service = Service(repository);
        var draft = Draft(CapabilityPolicyTests.Payload(toolName: "unknown.tool.v1"));

        var result = await service.SubmitValidatedDraftAsync(draft, Now, CancellationToken.None);

        Assert.Equal("capability.tool.unknown", result.ErrorCode);
        Assert.Equal(0, repository.CreateCalls);
    }

    [Fact]
    public async Task RejectsUnderdeclaredRiskBeforeCallingRepository()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, ("system.get_status.v1", "R3"));
        var draft = Draft(CapabilityPolicyTests.Payload(maximumRisk: "R1"));

        var result = await service.SubmitValidatedDraftAsync(draft, Now, CancellationToken.None);

        Assert.Equal("capability.risk.underdeclared", result.ErrorCode);
        Assert.Equal(0, repository.CreateCalls);
    }

    [Fact]
    public async Task RejectsUnsupportedPromptConnectionBeforeCallingRepository()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, ("system.get_status.v1", "R0"));
        var payload = CapabilityPolicyTests.Payload(
            connections: [new CapabilityConnectionRequirement("mail", "http")]);

        var result = await service.SubmitValidatedDraftAsync(Draft(payload), Now, CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Unsupported, result.Outcome);
        Assert.Equal("capability.d4.connection_kind_unsupported", result.ErrorCode);
        Assert.Equal(0, repository.CreateCalls);
    }

    [Fact]
    public async Task MailApiPassesCatalogKindAdmissionWhileUnknownKindsRemainUnsupported()
    {
        var repository = new RecordingRepository();
        var service = new CapabilityCatalogService(
            repository,
            new FixedDependencyCatalog(("mail.create_draft.v1", "R0"), ("mail.send.v1", "R3")),
            MailWorkflowCatalog());
        var mailApi = MailWorkflowPayload();
        var unknown = mailApi with
        {
            Manifest = mailApi.Manifest with
            {
                RequiredConnections = [new("database", "sql")],
            },
        };

        var admitted = await service.SubmitValidatedDraftAsync(Draft(mailApi), Now, CancellationToken.None);
        var rejected = await service.SubmitValidatedDraftAsync(
            Draft(unknown) with { DraftId = "draft_2" },
            Now,
            CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Succeeded, admitted.Outcome);
        Assert.Equal(CapabilityMutationOutcome.Unsupported, rejected.Outcome);
        Assert.Equal("capability.d4.connection_kind_unsupported", rejected.ErrorCode);
        Assert.Equal(1, repository.CreateCalls);
    }

    [Fact]
    public async Task RejectsBuilderIngressUntilItSharesThePackageCanonicalValidator()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, ("system.get_status.v1", "R0"));
        var draft = Draft(CapabilityPolicyTests.Payload()) with { SourceKind = "builder" };

        var result = await service.SubmitValidatedDraftAsync(draft, Now, CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Unsupported, result.Outcome);
        Assert.Equal("capability.d1.local_import_only", result.ErrorCode);
        Assert.Equal(0, repository.CreateCalls);
    }

    [Fact]
    public async Task ValidSubmissionBindsComputedExactVersionAndTimestamp()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, ("system.get_status.v1", "R0"));
        var payload = CapabilityPolicyTests.Payload();
        var draft = Draft(payload);

        var result = await service.SubmitValidatedDraftAsync(draft, Now, CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, repository.CreateCalls);
        Assert.Same(draft, repository.CreatedDraft);
        Assert.Equal(Now, repository.CreatedAtUtc);
        Assert.Equal(payload.Manifest.Id, repository.CreatedDraft!.ExactVersion.Id);
        Assert.Equal(payload.Manifest.Version, repository.CreatedDraft.ExactVersion.Version);
        Assert.Equal(CapabilityContentHasher.Compute(payload), repository.CreatedDraft.ExactVersion.ContentHash);
    }

    [Fact]
    public async Task WorkflowSubmissionRunsTheSameStaticValidatorBeforePersistence()
    {
        var repository = new RecordingRepository();
        var tool = new WorkflowToolMetadata(
            "system.get_status.v1",
            "R0",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            "{\"additionalProperties\":false,\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"],\"type\":\"object\"}",
            true,
            WorkflowToolPreparationMode.None,
            true,
            WorkflowRetryClass.ReadOnly,
            WorkflowToolEffectClass.Observation);
        var service = new CapabilityCatalogService(
            repository,
            new FixedDependencyCatalog((tool.CanonicalName, tool.Risk)),
            new FixedWorkflowCatalog(tool));
        var payload = WorkflowPayload();

        var accepted = await service.SubmitValidatedDraftAsync(
            Draft(payload), Now, CancellationToken.None);
        var malformed = await service.SubmitValidatedDraftAsync(
            Draft(payload with { CanonicalWorkflowJson = "{}" }) with { DraftId = "draft_2" },
            Now,
            CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Succeeded, accepted.Outcome);
        Assert.Equal(CapabilityMutationOutcome.Invalid, malformed.Outcome);
        Assert.StartsWith("workflow.", malformed.ErrorCode, StringComparison.Ordinal);
        Assert.Equal(1, repository.CreateCalls);
    }

    [Fact]
    public async Task ActivationAndRollbackPreserveCasBindings()
    {
        var repository = new RecordingRepository();
        var service = Service(repository, ("system.get_status.v1", "R0"));
        var id = CapabilityId.Parse("company.sample.status");
        var active = new CapabilityVersionRef(
            id, CapabilitySemanticVersion.Parse("2.0.0"), CapabilityPolicyTests.Hash('b'));
        var target = new CapabilityVersionRef(
            CapabilityId.Parse("company.sample.status"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityPolicyTests.Hash('a'));
        var proposed = new CapabilityVersionRef(
            id, CapabilitySemanticVersion.Parse("3.0.0"), CapabilityPolicyTests.Hash('c'));
        var proposedPayload = CapabilityPolicyTests.Payload() with
        {
            Manifest = CapabilityPolicyTests.Payload().Manifest with
            {
                Version = proposed.Version,
            },
        };
        proposedPayload = proposedPayload with
        {
            CanonicalManifestJson = "{\"schemaVersion\":1,\"version\":\"3.0.0\"}",
        };
        proposed = proposed with { ContentHash = CapabilityContentHasher.Compute(proposedPayload) };
        repository.DraftToReturn = StoredDraft(Draft(proposedPayload), proposed);
        var targetPayload = CapabilityPolicyTests.Payload() with
        {
            Manifest = CapabilityPolicyTests.Payload().Manifest with { Version = target.Version },
            CanonicalManifestJson = "{\"schemaVersion\":1,\"version\":\"1.0.0\"}",
        };
        target = target with { ContentHash = CapabilityContentHasher.Compute(targetPayload) };
        repository.VersionToReturn = new CapabilityVersion(
            target,
            null,
            targetPayload,
            "local_import",
            CapabilityPolicyTests.Hash('d'),
            Now);
        var approval = new CapabilityApprovalCommand(
            "draft_1", proposed, 4, 8, active, "user.review", Now);
        var rollback = new CapabilityRollbackCommand(
            id, 9, active, target, "user.rollback", Now.AddMinutes(1));

        _ = await service.ApproveAndActivateAsync(approval, CancellationToken.None);
        _ = await service.RollbackAsync(rollback, CancellationToken.None);

        Assert.Equal(approval, repository.ApprovalCommand);
        Assert.Equal(rollback, repository.RollbackCommand);
        Assert.Equal(proposed, repository.ApprovalCommand!.ExpectedVersion);
        Assert.Equal(active, repository.ApprovalCommand.ExpectedActiveVersion);
        Assert.Equal(active, repository.RollbackCommand!.ExpectedActiveVersion);
        Assert.Equal(target, repository.RollbackCommand.TargetVersion);
    }

    [Fact]
    public async Task ActivationAndRollbackRecheckConnectionLiteralBeforeMutation()
    {
        var repository = new RecordingRepository();
        var payload = CapabilityPolicyTests.Payload(
            instructions: "profileId=desktop-runtime-profile");
        var exact = new CapabilityVersionRef(
            payload.Manifest.Id,
            payload.Manifest.Version,
            CapabilityContentHasher.Compute(payload));
        var validated = Draft(payload);
        repository.DraftToReturn = StoredDraft(validated, exact);
        repository.VersionToReturn = new CapabilityVersion(
            exact,
            null,
            payload,
            "local_import",
            CapabilityPolicyTests.Hash('e'),
            Now);
        var active = exact with
        {
            Version = CapabilitySemanticVersion.Parse("2.0.0"),
            ContentHash = CapabilityPolicyTests.Hash('d'),
        };
        var service = Service(repository, ("system.get_status.v1", "R0"));

        var activation = await service.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                validated.DraftId,
                exact,
                0,
                1,
                active,
                "user.review",
                Now),
            CancellationToken.None);
        var rollback = await service.RollbackAsync(
            new CapabilityRollbackCommand(
                exact.Id,
                2,
                active,
                exact,
                "user.rollback",
                Now.AddMinutes(1)),
            CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Invalid, activation.Outcome);
        Assert.Equal(CapabilityMutationOutcome.Invalid, rollback.Outcome);
        Assert.Equal("capability.connection_literal.detected", activation.ErrorCode);
        Assert.Equal("capability.connection_literal.detected", rollback.ErrorCode);
        Assert.Null(repository.ApprovalCommand);
        Assert.Null(repository.RollbackCommand);
    }

    [Fact]
    public async Task ActivationRejectsConnectionLiteralBeforeResolvingLocalBinding()
    {
        var repository = new RecordingRepository();
        var payload = ConnectedWorkflowPayload("2.0.0") with
        {
            Instructions = "endpoint=https://activation.internal.invalid/v1",
        };
        var exact = new CapabilityVersionRef(
            payload.Manifest.Id,
            payload.Manifest.Version,
            CapabilityContentHasher.Compute(payload));
        var validated = Draft(payload);
        repository.DraftToReturn = StoredDraft(validated, exact);
        var connections = new RecordingConnectionReadiness(isReady: true);
        var service = new CapabilityCatalogService(
            repository,
            new FixedDependencyCatalog(("connection.http.request.v1", "R3")),
            HttpWorkflowCatalog(),
            connections);

        var result = await service.ApproveAndActivateAsync(
            new CapabilityApprovalCommand(
                validated.DraftId,
                exact,
                0,
                0,
                null,
                "user.review",
                Now),
            CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.Invalid, result.Outcome);
        Assert.Equal("capability.connection_literal.detected", result.ErrorCode);
        Assert.Empty(connections.ExactVersions);
        Assert.Null(repository.ApprovalCommand);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationAndRollbackAllowOnlyExactVersionReadyConnections(bool isReady)
    {
        var repository = new RecordingRepository();
        var activationPayload = ConnectedWorkflowPayload("2.0.0");
        var activationExact = new CapabilityVersionRef(
            activationPayload.Manifest.Id,
            activationPayload.Manifest.Version,
            CapabilityContentHasher.Compute(activationPayload));
        var rollbackPayload = ConnectedWorkflowPayload("1.0.0");
        var rollbackExact = new CapabilityVersionRef(
            rollbackPayload.Manifest.Id,
            rollbackPayload.Manifest.Version,
            CapabilityContentHasher.Compute(rollbackPayload));
        repository.DraftToReturn = StoredDraft(Draft(activationPayload), activationExact);
        repository.VersionToReturn = new CapabilityVersion(
            rollbackExact,
            null,
            rollbackPayload,
            "local_import",
            CapabilityPolicyTests.Hash('e'),
            Now);
        var connections = new RecordingConnectionReadiness(isReady);
        var service = new CapabilityCatalogService(
            repository,
            new FixedDependencyCatalog(("connection.http.request.v1", "R3")),
            HttpWorkflowCatalog(),
            connections);
        var approval = new CapabilityApprovalCommand(
            "draft_1",
            activationExact,
            0,
            1,
            rollbackExact,
            "user.review",
            Now);
        var rollback = new CapabilityRollbackCommand(
            rollbackExact.Id,
            2,
            activationExact,
            rollbackExact,
            "user.rollback",
            Now.AddMinutes(1));

        var activationResult = await service.ApproveAndActivateAsync(approval, CancellationToken.None);
        var rollbackResult = await service.RollbackAsync(rollback, CancellationToken.None);

        Assert.Equal([activationExact, rollbackExact], connections.ExactVersions);
        Assert.All(connections.Requirements, requirements =>
        {
            var requirement = Assert.Single(requirements);
            Assert.Equal("service", requirement.Slot);
            Assert.Equal("http", requirement.Kind);
        });
        if (isReady)
        {
            Assert.Equal(CapabilityMutationOutcome.Succeeded, activationResult.Outcome);
            Assert.Equal(CapabilityMutationOutcome.Succeeded, rollbackResult.Outcome);
            Assert.Equal(approval, repository.ApprovalCommand);
            Assert.Equal(rollback, repository.RollbackCommand);
        }
        else
        {
            Assert.Equal(CapabilityMutationOutcome.NotReady, activationResult.Outcome);
            Assert.Equal(CapabilityMutationOutcome.NotReady, rollbackResult.Outcome);
            Assert.Equal("capability.connection.not_ready", activationResult.ErrorCode);
            Assert.Equal("capability.connection.not_ready", rollbackResult.ErrorCode);
            Assert.Null(repository.ApprovalCommand);
            Assert.Null(repository.RollbackCommand);
        }
    }

    [Fact]
    public async Task ApprovalFailsClosedWhenRequiredToolDisappearsAfterImport()
    {
        var repository = new RecordingRepository();
        var payload = CapabilityPolicyTests.Payload();
        var validated = Draft(payload);
        repository.DraftToReturn = StoredDraft(validated, validated.ExactVersion);
        var service = Service(repository);
        var command = new CapabilityApprovalCommand(
            validated.DraftId,
            validated.ExactVersion,
            0,
            0,
            null,
            "user.review",
            Now);

        var result = await service.ApproveAndActivateAsync(command, CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.NotReady, result.Outcome);
        Assert.Equal("capability.tool.unavailable", result.ErrorCode);
        Assert.Null(repository.ApprovalCommand);
    }

    [Fact]
    public async Task RollbackFailsClosedWhenCanonicalToolRiskHasIncreased()
    {
        var repository = new RecordingRepository();
        var payload = CapabilityPolicyTests.Payload(maximumRisk: "R0");
        var target = new CapabilityVersionRef(
            payload.Manifest.Id,
            payload.Manifest.Version,
            CapabilityContentHasher.Compute(payload));
        repository.VersionToReturn = new CapabilityVersion(
            target,
            null,
            payload,
            "local_import",
            CapabilityPolicyTests.Hash('e'),
            Now);
        var active = target with
        {
            Version = CapabilitySemanticVersion.Parse("2.0.0"),
            ContentHash = CapabilityPolicyTests.Hash('f'),
        };
        var service = Service(repository, ("system.get_status.v1", "R3"));

        var result = await service.RollbackAsync(
            new CapabilityRollbackCommand(
                target.Id,
                2,
                active,
                target,
                "user.rollback",
                Now),
            CancellationToken.None);

        Assert.Equal(CapabilityMutationOutcome.NotReady, result.Outcome);
        Assert.Equal("capability.risk.changed", result.ErrorCode);
        Assert.Null(repository.RollbackCommand);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-02T00:00:00Z");

    private static ValidatedCapabilityDraft Draft(
        CapabilityVersionPayload payload,
        CapabilityVersionRef? parent = null,
        CapabilityContentHash? contentHash = null) =>
        new(
            "draft_1",
            payload,
            contentHash ?? CapabilityContentHasher.Compute(payload),
            parent,
            "local_import",
            CapabilityPolicyTests.Hash('f'));

    private static CapabilityDraft StoredDraft(
        ValidatedCapabilityDraft draft,
        CapabilityVersionRef exactVersion) => new(
        draft.DraftId,
        exactVersion,
        draft.Parent,
        draft.Payload,
        CapabilityDraftState.ReadyForReview,
        0,
        draft.SourceKind,
        draft.SourcePackageHash,
            Now,
            Now);

    private static CapabilityVersionPayload WorkflowPayload()
    {
        var id = CapabilityId.Parse("company.sample.workflow");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        return new CapabilityVersionPayload(
            new CapabilityManifest(
                1,
                id,
                version,
                "Sample Workflow",
                "Runs one statically validated observation.",
                CapabilityKind.Workflow,
                ["run the sample workflow"],
                ["system.get_status.v1"],
                [],
                "R0",
                "workflow.json"),
            "{\"id\":\"company.sample.workflow\",\"version\":\"1.0.0\"}",
            "Run the declared workflow.",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            CanonicalWorkflowJson:
                "{\"schemaVersion\":1,\"entryStepId\":\"status\",\"steps\":[{\"id\":\"status\",\"type\":\"tool.call\",\"tool\":\"system.get_status.v1\",\"arguments\":{},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}");
    }

    private static CapabilityVersionPayload ConnectedWorkflowPayload(string version)
    {
        var id = CapabilityId.Parse("company.sample.http-workflow");
        var semanticVersion = CapabilitySemanticVersion.Parse(version);
        return new CapabilityVersionPayload(
            new CapabilityManifest(
                1,
                id,
                semanticVersion,
                "HTTP Workflow",
                "Runs one bounded registered HTTP GET.",
                CapabilityKind.Workflow,
                ["Check internal service health"],
                ["connection.http.request.v1"],
                [new CapabilityConnectionRequirement("service", "http")],
                "R3",
                "workflow.json"),
            $"{{\"id\":\"{id.Value}\",\"version\":\"{version}\"}}",
            "Run the declared HTTP workflow.",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            CanonicalWorkflowJson:
                "{\"entryStepId\":\"request\",\"schemaVersion\":1,\"steps\":[{\"arguments\":{\"connectionSlot\":\"service\",\"method\":\"GET\",\"pathTemplate\":\"health\",\"reason\":\"Check internal service health\"},\"id\":\"request\",\"next\":\"done\",\"tool\":\"connection.http.request.v1\",\"type\":\"tool.call\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}");
    }

    private static CapabilityVersionPayload MailWorkflowPayload()
    {
        var id = CapabilityId.Parse("company.sample.mail-workflow");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        return new CapabilityVersionPayload(
            new CapabilityManifest(
                1,
                id,
                version,
                "Mail Workflow",
                "Sends one previously validated mail draft.",
                CapabilityKind.Workflow,
                ["Send a validated mail draft"],
                ["mail.create_draft.v1", "mail.send.v1"],
                [new("mail", CapabilityConnectionKindPolicy.MailApi)],
                "R3",
                "workflow.json"),
            $"{{\"id\":\"{id.Value}\",\"version\":\"{version}\"}}",
            "Run the declared mail workflow.",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            CanonicalWorkflowJson:
                "{\"schemaVersion\":1,\"entryStepId\":\"draft\",\"steps\":[{\"id\":\"draft\",\"type\":\"tool.call\",\"tool\":\"mail.create_draft.v1\",\"arguments\":{\"to\":[\"owner@example.com\"],\"cc\":[],\"bcc\":[],\"subject\":\"Status\",\"body\":\"Body\"},\"next\":\"send\"},{\"id\":\"send\",\"type\":\"tool.call\",\"tool\":\"mail.send.v1\",\"arguments\":{\"connectionSlot\":\"mail\",\"draftHash\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/draftHash\"}},\"to\":[\"owner@example.com\"],\"cc\":[],\"bcc\":[],\"subject\":\"Status\",\"body\":\"Body\"},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}");
    }

    private static FixedWorkflowCatalog HttpWorkflowCatalog()
    {
        if (!ToolSchemaRegistry.TryGet("connection.http.request.v1", out var schemas) || schemas is null)
            throw new InvalidOperationException("HTTP workflow Tool schema가 등록되지 않았습니다.");
        return new FixedWorkflowCatalog(new WorkflowToolMetadata(
            "connection.http.request.v1",
            "R3",
            schemas.CanonicalInputSchemaJson,
            schemas.CanonicalResultSchemaJson,
            true,
            WorkflowToolPreparationMode.OneShot,
            true,
            WorkflowRetryClass.Never,
            WorkflowToolEffectClass.Effect));
    }

    private static FixedWorkflowCatalog MailWorkflowCatalog()
    {
        Assert.True(ToolSchemaRegistry.TryGet("mail.create_draft.v1", out var createDraftSchemas));
        Assert.True(ToolSchemaRegistry.TryGet("mail.send.v1", out var sendSchemas));
        return new FixedWorkflowCatalog(
            new WorkflowToolMetadata(
                "mail.create_draft.v1",
                "R0",
                createDraftSchemas!.CanonicalInputSchemaJson,
                createDraftSchemas.CanonicalResultSchemaJson,
                true,
                WorkflowToolPreparationMode.None,
                true,
                WorkflowRetryClass.ReadOnly,
                WorkflowToolEffectClass.Observation),
            new WorkflowToolMetadata(
                "mail.send.v1",
                "R3",
                sendSchemas!.CanonicalInputSchemaJson,
                sendSchemas.CanonicalResultSchemaJson,
                true,
                WorkflowToolPreparationMode.OneShot,
                true,
                WorkflowRetryClass.Never,
                WorkflowToolEffectClass.Effect));
    }

    private static CapabilityCatalogService Service(
        RecordingRepository repository,
        params (string Name, string Risk)[] tools) =>
        new(repository, new FixedDependencyCatalog(tools));

    private sealed class FixedDependencyCatalog(params (string Name, string Risk)[] tools) : ICapabilityDependencyCatalog
    {
        private readonly Dictionary<string, CapabilityToolDependency> _tools = tools.ToDictionary(
            tool => tool.Name,
            tool => new CapabilityToolDependency(tool.Name, tool.Risk),
            StringComparer.Ordinal);

        public bool TryGetTool(string name, out CapabilityToolDependency? dependency) =>
            _tools.TryGetValue(name, out dependency);
    }

    private sealed class FixedWorkflowCatalog(params WorkflowToolMetadata[] tools) : IWorkflowToolMetadataCatalog
    {
        private readonly IReadOnlyDictionary<string, WorkflowToolMetadata> _tools = tools.ToDictionary(
            tool => tool.CanonicalName,
            StringComparer.Ordinal);

        public bool TryGet(string canonicalName, out WorkflowToolMetadata? metadata) =>
            _tools.TryGetValue(canonicalName, out metadata);
    }

    private sealed class RecordingConnectionReadiness(bool isReady) : ICapabilityConnectionReadinessResolver
    {
        public List<CapabilityVersionRef> ExactVersions { get; } = [];
        public List<IReadOnlyList<CapabilityConnectionRequirement>> Requirements { get; } = [];

        public Task<ConnectionReadinessResolution> ResolveAsync(
            CapabilityVersionRef exactVersion,
            IReadOnlyList<CapabilityConnectionRequirement> requirements,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExactVersions.Add(exactVersion);
            Requirements.Add(requirements.ToArray());
            var binding = new ResolvedConnectionBinding(
                exactVersion,
                "service",
                "http",
                ConnectionProfileId.Parse("service-profile"),
                1,
                $"sha256:{new string('b', 64)}",
                1,
                0,
                $"sha256:{new string('c', 64)}",
                isReady,
                isReady ? null : "capability.connection.not_ready");
            return Task.FromResult(new ConnectionReadinessResolution(
                isReady,
                ConnectionReadinessHasher.Compute(exactVersion, [binding]),
                [binding]));
        }
    }

    private sealed class RecordingRepository : ICapabilityCatalogRepository
    {
        public int CreateCalls { get; private set; }
        public ValidatedCapabilityDraft? CreatedDraft { get; private set; }
        public DateTimeOffset? CreatedAtUtc { get; private set; }
        public CapabilityApprovalCommand? ApprovalCommand { get; private set; }
        public CapabilityRollbackCommand? RollbackCommand { get; private set; }
        public CapabilityDraft? DraftToReturn { get; set; }
        public CapabilityVersion? VersionToReturn { get; set; }

        public Task<CapabilityCatalogEntry?> GetCatalogAsync(CapabilityId id, CancellationToken cancellationToken) =>
            Task.FromResult<CapabilityCatalogEntry?>(null);

        public Task<CapabilityVersion?> GetVersionAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) => Task.FromResult(
            VersionToReturn?.ExactVersion.Equals(exactVersion) == true ? VersionToReturn : null);

        public Task<CapabilityDraft?> GetDraftAsync(string draftId, CancellationToken cancellationToken) =>
            Task.FromResult(DraftToReturn?.DraftId == draftId ? DraftToReturn : null);

        public Task<IReadOnlyList<CapabilityCatalogSummary>> ListAsync(
            string? query,
            int offset,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityCatalogSummary>>([]);

        public Task<CapabilityDraftCreateResult> CreateValidatedDraftAsync(
            ValidatedCapabilityDraft draft,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            CreatedDraft = draft;
            CreatedAtUtc = nowUtc;
            return Task.FromResult(new CapabilityDraftCreateResult(CapabilityMutationOutcome.Succeeded));
        }

        public Task<CapabilityActivationResult> ApproveAndActivateAsync(
            CapabilityApprovalCommand command,
            CancellationToken cancellationToken)
        {
            ApprovalCommand = command;
            return Task.FromResult(new CapabilityActivationResult(CapabilityMutationOutcome.Succeeded));
        }

        public Task<CapabilityActivationResult> RollbackAsync(
            CapabilityRollbackCommand command,
            CancellationToken cancellationToken)
        {
            RollbackCommand = command;
            return Task.FromResult(new CapabilityActivationResult(CapabilityMutationOutcome.Succeeded));
        }

        public Task<IReadOnlyList<CapabilityActivation>> ListActivationsAsync(
            CapabilityId id,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityActivation>>([]);
    }
}
