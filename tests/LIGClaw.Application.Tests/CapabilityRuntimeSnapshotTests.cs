using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class CapabilityRuntimeSnapshotTests
{
    [Fact]
    public async Task BuildsFrozenBoundedProjectionAndFixedHashFixture()
    {
        var payload = Payload(
            "company.sample.status",
            "1.2.3",
            ["system.get_status.v1"],
            "R1",
            instructions: "현재 상태를 간결하게 설명합니다.");
        var sourceCandidate = Candidate(payload, catalogRevision: 7);
        var source = new RecordingSource([sourceCandidate]);
        var builder = new CapabilityRuntimeSnapshotBuilder(
            source,
            new FixedDependencies(("system.get_status.v1", "R1")));

        var snapshot = await builder.BuildAsync("현재 상태를 알려줘", CancellationToken.None);

        Assert.Equal(2, snapshot.SchemaVersion);
        var projected = Assert.Single(snapshot.Candidates);
        Assert.True(
            StringComparer.Ordinal.Equals(
                "sha256:4555f512a661bc53076cbbf3061c97c0d6dbe8f0e10d27be877867149ced0e03",
                sourceCandidate.ExactVersion.ContentHash.Value),
            sourceCandidate.ExactVersion.ContentHash.Value);
        Assert.Equal(sourceCandidate.ExactVersion, projected.ExactVersion);
        Assert.Equal(7, projected.CatalogRevision);
        Assert.Equal("현재 상태를 간결하게 설명합니다.", projected.Instructions);
        Assert.Equal(
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            projected.CanonicalInputSchemaJson);
        Assert.Equal(["system.get_status.v1"], projected.RequiredTools);
        Assert.Equal("cap_efda3ba7364eceba90e43816", projected.ModelToolName);
        Assert.True(
            StringComparer.Ordinal.Equals(
                "sha256:ad429989009d2eb15c6888498a967b7ce01119a66ed27a48cdccd162bf40ee80",
                snapshot.SnapshotHash.Value),
            snapshot.SnapshotHash.Value);
    }

    [Fact]
    public async Task SanitizesAndUnicodeBoundsUserRequestBeforeStrictSourcePort()
    {
        var source = new RecordingSource([]);
        var builder = new CapabilityRuntimeSnapshotBuilder(source, new FixedDependencies());
        var longRequest = $"  {string.Concat(Enumerable.Repeat("🙂", 200))}\r\nignored";

        var snapshot = await builder.BuildAsync(longRequest, CancellationToken.None);

        Assert.Empty(snapshot.Candidates);
        Assert.Equal(1, source.CallCount);
        Assert.Equal(256, source.LastQuery!.Length);
        Assert.DoesNotContain(source.LastQuery, character => char.IsControl(character));
        Assert.False(char.IsHighSurrogate(source.LastQuery[^1]));
        Assert.Equal(8, source.LastLimit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    [InlineData("두자")]
    public async Task BlankOrTooShortRequestUsesDeterministicEmptySnapshotWithoutFts(string? request)
    {
        var source = new RecordingSource([]);
        var builder = new CapabilityRuntimeSnapshotBuilder(source, new FixedDependencies());

        var snapshot = await builder.BuildAsync(request, CancellationToken.None);

        Assert.Same(CapabilityRuntimeSnapshot.Empty, snapshot);
        Assert.Equal(0, source.CallCount);
        Assert.True(
            StringComparer.Ordinal.Equals(
                "sha256:d5934746206037767ec6f085657325b28e7ea241c39a1dcf323bbdf113254758",
                snapshot.SnapshotHash.Value),
            snapshot.SnapshotHash.Value);
    }

    [Fact]
    public async Task RejectsSourceThatExceedsEightCandidates()
    {
        var candidates = Enumerable.Range(0, 9)
            .Select(index => Candidate(Payload($"company.sample.item{index}", "1.0.0"), 1))
            .ToArray();
        var builder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource(candidates),
            new FixedDependencies());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            builder.BuildAsync("sample request", CancellationToken.None));
    }

    [Fact]
    public async Task RechecksSecretAndExactContentHashBeforeProjection()
    {
        var secretPayload = Payload(
            "company.sample.secret",
            "1.0.0",
            instructions: "token=ordinary-secret-value");
        var secretBuilder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource([Candidate(secretPayload, 1)]),
            new FixedDependencies());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            secretBuilder.BuildAsync("secret request", CancellationToken.None));

        var safePayload = Payload("company.sample.tampered", "1.0.0");
        var wrongHash = CapabilityContentHash.Parse("sha256:" + new string('f', 64));
        var wrongExact = new CapabilityRuntimeSourceCandidate(
            new CapabilityVersionRef(
                safePayload.Manifest.Id,
                safePayload.Manifest.Version,
                wrongHash),
            1,
            safePayload);
        var hashBuilder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource([wrongExact]),
            new FixedDependencies());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            hashBuilder.BuildAsync("tampered request", CancellationToken.None));
    }

    [Fact]
    public async Task RechecksConnectionLiteralBeforeProjection()
    {
        var payload = Payload(
            "company.sample.connection-literal",
            "1.0.0",
            instructions: "Use https://runtime.internal.invalid/v1.");
        var builder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource([Candidate(payload, 1)]),
            new FixedDependencies());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            builder.BuildAsync("connection request", CancellationToken.None));

        Assert.DoesNotContain("runtime.internal.invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmitsCandidatesWhenToolInventoryOrCanonicalRiskIsNoLongerReady()
    {
        var valid = Candidate(Payload(
            "company.sample.valid",
            "1.0.0",
            ["tool.valid.v1"],
            "R1"), 1);
        var missing = Candidate(Payload(
            "company.sample.missing",
            "1.0.0",
            ["tool.missing.v1"],
            "R1"), 1);
        var underdeclared = Candidate(Payload(
            "company.sample.risk",
            "1.0.0",
            ["tool.risky.v1"],
            "R1"), 1);
        var builder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource([valid, missing, underdeclared]),
            new FixedDependencies(("tool.valid.v1", "R1"), ("tool.risky.v1", "R2")));

        var snapshot = await builder.BuildAsync("tool request", CancellationToken.None);

        Assert.Equal(valid.ExactVersion, Assert.Single(snapshot.Candidates).ExactVersion);
    }

    [Fact]
    public async Task ResolvesConnectionsForEachExactVersionAndOmitsTheNotReadyCandidate()
    {
        var ready = Candidate(ConnectedWorkflowPayload("company.sample.ready", "1.0.0"), 1);
        var notReady = Candidate(ConnectedWorkflowPayload("company.sample.not-ready", "1.0.0"), 1);
        var connections = new RecordingConnections((exactVersion, _) =>
            ConnectionResolution(
                exactVersion,
                isReady: exactVersion.Equals(ready.ExactVersion),
                bindingRevision: 1));
        var builder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource([ready, notReady]),
            new FixedDependencies(("connection.http.request.v1", "R3")),
            connections);

        var snapshot = await builder.BuildAsync("run connected workflow", CancellationToken.None);

        var projected = Assert.Single(snapshot.Candidates);
        Assert.Equal(ready.ExactVersion, projected.ExactVersion);
        Assert.Equal(connections.Resolutions[0].AggregateHash, projected.ConnectionBindingHash);
        Assert.Equal([ready.ExactVersion, notReady.ExactVersion], connections.ExactVersions);
        Assert.All(connections.Requirements, requirements =>
        {
            var requirement = Assert.Single(requirements);
            Assert.Equal("service", requirement.Slot);
            Assert.Equal("http", requirement.Kind);
        });
    }

    [Fact]
    public async Task MailApiRequirementReachesReadinessResolutionButIsOmittedWhenNotReady()
    {
        var payload = MailConnectedWorkflowPayload("company.sample.mail-runtime", "1.0.0");
        var sourceCandidate = Candidate(payload, 1);
        var connections = new RecordingConnections((exactVersion, _) => ConnectionResolution(
            exactVersion,
            isReady: false,
            bindingRevision: 1,
            slot: "mail",
            kind: CapabilityConnectionKindPolicy.MailApi));
        var builder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource([sourceCandidate]),
            new FixedDependencies(("mail.create_draft.v1", "R0"), ("mail.send.v1", "R3")),
            connections);

        var snapshot = await builder.BuildAsync("send connected mail", CancellationToken.None);

        Assert.Empty(snapshot.Candidates);
        var requirement = Assert.Single(Assert.Single(connections.Requirements));
        Assert.Equal("mail", requirement.Slot);
        Assert.Equal(CapabilityConnectionKindPolicy.MailApi, requirement.Kind);
    }

    [Fact]
    public async Task ChangedConnectionBindingChangesCandidateAndSnapshotHashes()
    {
        var sourceCandidate = Candidate(
            ConnectedWorkflowPayload("company.sample.rebound", "1.0.0"),
            1);
        var resolution = ConnectionResolution(
            sourceCandidate.ExactVersion,
            isReady: true,
            bindingRevision: 1);
        var connections = new RecordingConnections((_, _) => resolution);
        var builder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource([sourceCandidate]),
            new FixedDependencies(("connection.http.request.v1", "R3")),
            connections);

        var original = await builder.BuildAsync("run connected workflow", CancellationToken.None);
        resolution = ConnectionResolution(
            sourceCandidate.ExactVersion,
            isReady: true,
            bindingRevision: 2);
        var rebound = await builder.BuildAsync("run connected workflow", CancellationToken.None);

        Assert.Equal(
            Assert.Single(original.Candidates).ExactVersion,
            Assert.Single(rebound.Candidates).ExactVersion);
        Assert.NotEqual(
            Assert.Single(original.Candidates).ConnectionBindingHash,
            Assert.Single(rebound.Candidates).ConnectionBindingHash);
        Assert.NotEqual(original.SnapshotHash, rebound.SnapshotHash);
        Assert.Equal([sourceCandidate.ExactVersion, sourceCandidate.ExactVersion], connections.ExactVersions);
    }

    [Fact]
    public async Task ProjectsAnActivatedWorkflowWithoutSendingItsDefinition()
    {
        var prompt = Payload(
            "company.sample.workflow",
            "1.0.0",
            ["system.get_status.v1"],
            "R0");
        var workflow = prompt with
        {
            Manifest = prompt.Manifest with
            {
                Kind = CapabilityKind.Workflow,
                Entrypoint = "workflow.json",
            },
            CanonicalWorkflowJson =
                "{\"entryStepId\":\"status\",\"schemaVersion\":1,\"steps\":[{\"arguments\":{},\"id\":\"status\",\"next\":\"done\",\"tool\":\"system.get_status.v1\",\"type\":\"tool.call\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}",
        };
        var builder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource([Candidate(workflow, 3)]),
            new FixedDependencies(("system.get_status.v1", "R0")));

        var snapshot = await builder.BuildAsync("run workflow now", CancellationToken.None);

        var projected = Assert.Single(snapshot.Candidates);
        Assert.Equal(CapabilityKind.Workflow, projected.Kind);
        Assert.Equal(workflow.CanonicalInputSchemaJson, projected.CanonicalInputSchemaJson);
        Assert.Equal(workflow.Manifest.RequiredTools, projected.RequiredTools);
        Assert.DoesNotContain("entryStepId", projected.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AggregateTransportBudgetDropsOnlyCandidatesThatWouldExceedTheRpcEnvelopeReserve()
    {
        var candidates = Enumerable.Range(0, 8)
            .Select(index => Candidate(Payload(
                $"company.sample.large{index}",
                "1.0.0",
                instructions: new string('"', CapabilityDefinitionPolicy.MaximumInstructionsCharacters)), 1))
            .ToArray();
        var builder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource(candidates),
            new FixedDependencies());

        var snapshot = await builder.BuildAsync("large capability request", CancellationToken.None);

        Assert.InRange(snapshot.Candidates.Count, 1, 7);
        Assert.Equal(
            candidates.Take(snapshot.Candidates.Count).Select(candidate => candidate.ExactVersion),
            snapshot.Candidates.Select(candidate => candidate.ExactVersion));
        Assert.True(CapabilityRuntimeTransportPolicy.IsWithinBudget(snapshot));
        Assert.InRange(
            CapabilityRuntimeTransportPolicy.GetSerializedSizeBytes(snapshot),
            1,
            CapabilityRuntimeTransportPolicy.MaximumSerializedSnapshotBytes);
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"properties\":{}}")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"array\",\"properties\":{},\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":true}")]
    [InlineData("{\"type\":\"object\",\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"boolean\"}}},\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\",\"const\":9007199254740992}},\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\",\"type\":\"string\",\"maxLength\":10}},\"additionalProperties\":false}")]
    public async Task OmitsPersistedCandidateWhenInputSchemaIsOutsideTheBoundedPromptSubset(string inputSchema)
    {
        var payload = Payload("company.sample.schema", "1.0.0") with
        {
            CanonicalInputSchemaJson = inputSchema,
        };
        var builder = new CapabilityRuntimeSnapshotBuilder(
            new RecordingSource([Candidate(payload, 1)]),
            new FixedDependencies());

        var snapshot = await builder.BuildAsync("schema request", CancellationToken.None);

        Assert.Empty(snapshot.Candidates);
    }

    [Fact]
    public async Task CatalogRollbackRevisionChangesHashButNotExactModelToolName()
    {
        var payload = Payload("company.sample.rollback", "1.0.0");
        var source = new RecordingSource([Candidate(payload, 1)]);
        var builder = new CapabilityRuntimeSnapshotBuilder(source, new FixedDependencies());
        var original = await builder.BuildAsync("rollback request", CancellationToken.None);

        source.Candidates = [Candidate(payload, 3)];
        var rolledBack = await builder.BuildAsync("rollback request", CancellationToken.None);

        Assert.NotEqual(original.SnapshotHash, rolledBack.SnapshotHash);
        Assert.Equal(
            Assert.Single(original.Candidates).ModelToolName,
            Assert.Single(rolledBack.Candidates).ModelToolName);
        Assert.Equal(1, Assert.Single(original.Candidates).CatalogRevision);
        Assert.Equal(3, Assert.Single(rolledBack.Candidates).CatalogRevision);
    }

    [Fact]
    public void SnapshotDeepCopiesCandidateListsAndHashRemainsFrozen()
    {
        var intents = new List<string> { "first intent" };
        var tools = new List<string> { "tool.first.v1" };
        var exact = new CapabilityVersionRef(
            CapabilityId.Parse("company.sample.copy"),
            CapabilitySemanticVersion.Parse("1.0.0"),
            CapabilityContentHash.Parse("sha256:" + new string('a', 64)));
        var candidate = new CapabilityRuntimeCandidate(
            exact,
            1,
            CapabilityRuntimeHasher.ComputeModelToolName(exact),
            "Copy",
            "Copy test",
            CapabilityKind.Prompt,
            intents,
            "Instructions",
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            tools,
            "sha256:" + new string('b', 64),
            "R0");
        var snapshot = CapabilityRuntimeSnapshot.Create([candidate]);
        var frozenHash = snapshot.SnapshotHash;

        intents.Add("later intent");
        tools.Add("tool.later.v1");

        Assert.Equal(["first intent"], Assert.Single(snapshot.Candidates).IntentExamples);
        Assert.Equal(["tool.first.v1"], Assert.Single(snapshot.Candidates).RequiredTools);
        Assert.Equal(frozenHash, snapshot.SnapshotHash);
        Assert.Equal(frozenHash, CapabilityRuntimeHasher.ComputeSnapshotHash(snapshot.Candidates));
    }

    private static CapabilityVersionPayload Payload(
        string id,
        string version,
        IReadOnlyList<string>? requiredTools = null,
        string maximumRisk = "R0",
        string instructions = "Use the declared tools.")
    {
        var capabilityId = CapabilityId.Parse(id);
        var semanticVersion = CapabilitySemanticVersion.Parse(version);
        return new CapabilityVersionPayload(
            new CapabilityManifest(
                1,
                capabilityId,
                semanticVersion,
                "Status Helper",
                "Explains the current status.",
                CapabilityKind.Prompt,
                ["show current status"],
                requiredTools ?? [],
                [],
                maximumRisk,
                "instructions.md"),
            $"{{\"id\":\"{id}\",\"version\":\"{version}\"}}",
            instructions,
            "{\"additionalProperties\":false,\"properties\":{},\"type\":\"object\"}",
            null,
            null,
            "[]");
    }

    private static CapabilityVersionPayload ConnectedWorkflowPayload(string id, string version)
    {
        var payload = Payload(
            id,
            version,
            ["connection.http.request.v1"],
            "R3");
        return payload with
        {
            Manifest = payload.Manifest with
            {
                Kind = CapabilityKind.Workflow,
                RequiredConnections = [new CapabilityConnectionRequirement("service", "http")],
                Entrypoint = "workflow.json",
            },
            CanonicalWorkflowJson =
                "{\"entryStepId\":\"request\",\"schemaVersion\":1,\"steps\":[{\"arguments\":{\"connectionSlot\":\"service\",\"method\":\"GET\",\"pathTemplate\":\"health\",\"reason\":\"Check internal service health\"},\"id\":\"request\",\"next\":\"done\",\"tool\":\"connection.http.request.v1\",\"type\":\"tool.call\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}"
        };
    }

    private static CapabilityVersionPayload MailConnectedWorkflowPayload(string id, string version)
    {
        var payload = Payload(
            id,
            version,
            ["mail.create_draft.v1", "mail.send.v1"],
            "R3");
        return payload with
        {
            Manifest = payload.Manifest with
            {
                Kind = CapabilityKind.Workflow,
                RequiredConnections = [new CapabilityConnectionRequirement(
                    "mail",
                    CapabilityConnectionKindPolicy.MailApi)],
                Entrypoint = "workflow.json",
            },
            CanonicalWorkflowJson =
                "{\"schemaVersion\":1,\"entryStepId\":\"draft\",\"steps\":[{\"id\":\"draft\",\"type\":\"tool.call\",\"tool\":\"mail.create_draft.v1\",\"arguments\":{\"to\":[\"owner@example.com\"],\"cc\":[],\"bcc\":[],\"subject\":\"Status\",\"body\":\"Body\"},\"next\":\"send\"},{\"id\":\"send\",\"type\":\"tool.call\",\"tool\":\"mail.send.v1\",\"arguments\":{\"connectionSlot\":\"mail\",\"draftHash\":{\"$bind\":{\"source\":\"step\",\"stepId\":\"draft\",\"path\":\"/draftHash\"}},\"to\":[\"owner@example.com\"],\"cc\":[],\"bcc\":[],\"subject\":\"Status\",\"body\":\"Body\"},\"next\":\"done\"},{\"id\":\"done\",\"type\":\"output\",\"value\":{}}]}"
        };
    }

    private static ConnectionReadinessResolution ConnectionResolution(
        CapabilityVersionRef exactVersion,
        bool isReady,
        long bindingRevision,
        string slot = "service",
        string kind = CapabilityConnectionKindPolicy.Http)
    {
        var binding = new ResolvedConnectionBinding(
            exactVersion,
            slot,
            kind,
            ConnectionProfileId.Parse("service-profile"),
            bindingRevision,
            $"sha256:{new string(bindingRevision == 1 ? 'b' : 'c', 64)}",
            bindingRevision,
            0,
            $"sha256:{new string('d', 64)}",
            isReady,
            isReady ? null : "capability.connection.not_ready");
        return new ConnectionReadinessResolution(
            isReady,
            ConnectionReadinessHasher.Compute(exactVersion, [binding]),
            [binding]);
    }

    private static CapabilityRuntimeSourceCandidate Candidate(
        CapabilityVersionPayload payload,
        long catalogRevision) => new(
        new CapabilityVersionRef(
            payload.Manifest.Id,
            payload.Manifest.Version,
            CapabilityContentHasher.Compute(payload)),
        catalogRevision,
        payload);

    private sealed class RecordingSource(
        IReadOnlyList<CapabilityRuntimeSourceCandidate> candidates) : ICapabilityRuntimeCandidateSource
    {
        public IReadOnlyList<CapabilityRuntimeSourceCandidate> Candidates { get; set; } = candidates;
        public int CallCount { get; private set; }
        public string? LastQuery { get; private set; }
        public int LastLimit { get; private set; }

        public Task<IReadOnlyList<CapabilityRuntimeSourceCandidate>> LoadActiveRuntimeCandidatesAsync(
            string query,
            int limit,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastQuery = query;
            LastLimit = limit;
            return Task.FromResult(Candidates);
        }
    }

    private sealed class FixedDependencies(params (string Name, string Risk)[] tools) : ICapabilityDependencyCatalog
    {
        private readonly IReadOnlyDictionary<string, CapabilityToolDependency> _tools = tools.ToDictionary(
            tool => tool.Name,
            tool => new CapabilityToolDependency(tool.Name, tool.Risk),
            StringComparer.Ordinal);

        public bool TryGetTool(string name, out CapabilityToolDependency? dependency) =>
            _tools.TryGetValue(name, out dependency);
    }

    private sealed class RecordingConnections(
        Func<CapabilityVersionRef, IReadOnlyList<CapabilityConnectionRequirement>, ConnectionReadinessResolution>
            resolve) : ICapabilityConnectionReadinessResolver
    {
        public List<CapabilityVersionRef> ExactVersions { get; } = [];
        public List<IReadOnlyList<CapabilityConnectionRequirement>> Requirements { get; } = [];
        public List<ConnectionReadinessResolution> Resolutions { get; } = [];

        public Task<ConnectionReadinessResolution> ResolveAsync(
            CapabilityVersionRef exactVersion,
            IReadOnlyList<CapabilityConnectionRequirement> requirements,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExactVersions.Add(exactVersion);
            Requirements.Add(requirements.ToArray());
            var resolution = resolve(exactVersion, requirements);
            Resolutions.Add(resolution);
            return Task.FromResult(resolution);
        }
    }
}
