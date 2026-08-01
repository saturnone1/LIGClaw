using LIGClaw.Contracts.Generated;
using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class ConversationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LIGClaw.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PersistsAndReplaysConversationEventsIdempotently()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var startedAt = DateTimeOffset.Parse("2026-07-25T01:00:00Z");
        await store.StartRunAsync("conversation-1", "run-1", "오늘 할 일을 정리해 줘", startedAt);
        var started = Event(0, "run_started", startedAt);
        var first = Event(1, "text_delta", startedAt.AddSeconds(1), "첫 번째 ");
        var second = Event(2, "text_delta", startedAt.AddSeconds(2), "답변");
        var completed = Event(3, "run_completed", startedAt.AddSeconds(3));

        await store.AppendEventAsync(started);
        await store.AppendEventAsync(first);
        await store.AppendEventAsync(first);
        await store.AppendEventAsync(second);
        await store.AppendEventAsync(completed);

        var recent = Assert.Single(await store.GetRecentConversationsAsync());
        Assert.Equal("completed", recent.Status);
        Assert.Equal("오늘 할 일을 정리해 줘", recent.Title);
        var transcript = await store.GetTranscriptAsync("conversation-1");
        Assert.Contains("### 나", transcript, StringComparison.Ordinal);
        Assert.Contains("오늘 할 일을 정리해 줘", transcript, StringComparison.Ordinal);
        Assert.Contains("첫 번째 답변", transcript, StringComparison.Ordinal);
        Assert.Equal("첫 번째 답변", Assert.Single(await store.GetConversationTurnsAsync("conversation-1")).AssistantText);
    }

    [Fact]
    public async Task MultipleRunsRemainInOneConversationAndRestoreCompletedContext()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T03:00:00Z");
        await store.StartRunAsync("thread-1", "run-1", "내 이름은 태원이라고 기억해", now);
        await store.AppendEventAsync(new AgentEvent("thread-1", "run-1", 0, "text_delta", now.AddSeconds(1), "알겠습니다.", null));
        await store.AppendEventAsync(new AgentEvent("thread-1", "run-1", 1, "run_completed", now.AddSeconds(2), null, null));
        await store.StartRunAsync("thread-1", "run-2", "내 이름이 뭐야?", now.AddMinutes(1));
        await store.AppendEventAsync(new AgentEvent("thread-1", "run-2", 0, "text_delta", now.AddMinutes(1).AddSeconds(1), "태원입니다.", null));
        await store.AppendEventAsync(new AgentEvent("thread-1", "run-2", 1, "run_completed", now.AddMinutes(1).AddSeconds(2), null, null));

        var summary = Assert.Single(await store.GetRecentConversationsAsync());
        Assert.Equal("thread-1", summary.Id);
        Assert.Equal(2, summary.TurnCount);
        Assert.Equal(2, (await store.GetConversationTurnsAsync("thread-1")).Count);
        Assert.Equal(
            new[]
            {
                new ConversationContextMessage("user", "내 이름은 태원이라고 기억해"),
                new ConversationContextMessage("assistant", "알겠습니다."),
                new ConversationContextMessage("user", "내 이름이 뭐야?"),
                new ConversationContextMessage("assistant", "태원입니다."),
            },
            await store.GetConversationContextAsync("thread-1"));
    }

    [Fact]
    public async Task FullTextSearchFindsUserAndAssistantTextAfterIncrementalUpdates()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T03:30:00Z");
        await store.StartRunAsync("editor-thread", "editor-run", "선호 편집기를 알려줘", now);
        await store.AppendEventAsync(new AgentEvent(
            "editor-thread", "editor-run", 0, "text_delta", now.AddSeconds(1), "Visual Studio Code를 선호합니다.", null));
        await store.AppendEventAsync(new AgentEvent(
            "editor-thread", "editor-run", 1, "run_completed", now.AddSeconds(2), null, null));
        await store.StartRunAsync("storage-thread", "storage-run", "디스크 사용률 확인", now.AddMinutes(1));
        await store.AppendEventAsync(new AgentEvent(
            "storage-thread", "storage-run", 0, "text_delta", now.AddMinutes(1), "E 드라이브 사용률은 83%입니다.", null));

        var assistantMatch = Assert.Single(await store.SearchConversationsAsync("Studio Code"));
        Assert.Equal("editor-thread", assistantMatch.Id);
        var userMatch = Assert.Single(await store.SearchConversationsAsync("스크 사용"));
        Assert.Equal("storage-thread", userMatch.Id);
        var literalShortMatch = Assert.Single(await store.SearchConversationsAsync("%"));
        Assert.Equal("storage-thread", literalShortMatch.Id);
        Assert.Empty(await store.SearchConversationsAsync("존재하지 않는 문장"));
    }

    [Fact]
    public async Task FullTextSearchBoundsUntrustedQueries()
    {
        using var store = CreateStore();
        await store.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => store.SearchConversationsAsync("   "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SearchConversationsAsync(new string('가', 201)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SearchConversationsAsync("검색\0어"));
    }

    [Fact]
    public async Task FailedTurnRemainsVisibleButIsNotReplayedIntoModelContext()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T04:00:00Z");
        await store.StartRunAsync("thread-1", "run-ok", "완료 요청", now);
        await store.AppendEventAsync(new AgentEvent("thread-1", "run-ok", 0, "text_delta", now, "완료 답변", null));
        await store.AppendEventAsync(new AgentEvent("thread-1", "run-ok", 1, "run_completed", now, null, null));
        await store.StartRunAsync("thread-1", "run-failed", "실패 요청", now.AddMinutes(1));
        await store.AppendEventAsync(new AgentEvent("thread-1", "run-failed", 0, "text_delta", now, "부분 답변", null));
        await store.AppendEventAsync(new AgentEvent("thread-1", "run-failed", 1, "run_failed", now, null, "runtime.provider"));

        var transcript = await store.GetTranscriptAsync("thread-1");
        Assert.Contains("실패 요청", transcript, StringComparison.Ordinal);
        Assert.Contains("부분 답변", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(await store.GetConversationContextAsync("thread-1"), message => message.Content.Contains("실패", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LateFailureMarkForAnOldRunDoesNotOverwriteANewerActiveRun()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T04:30:00Z");
        await store.StartRunAsync("thread-1", "run-old", "이전 요청", now);
        await store.StartRunAsync("thread-1", "run-new", "빠른 후속 요청", now.AddSeconds(1));

        await store.MarkRunAsync("thread-1", "run-old", "failed", now.AddSeconds(2));
        await store.AppendEventAsync(new AgentEvent(
            "thread-1", "run-old", 0, "run_failed", now.AddSeconds(3), null, "runtime.provider"));

        var turns = await store.GetConversationTurnsAsync("thread-1");
        Assert.Equal("failed", Assert.Single(turns, turn => turn.RunId == "run-old").Status);
        Assert.Equal("running", Assert.Single(turns, turn => turn.RunId == "run-new").Status);
        Assert.Equal("running", Assert.Single(await store.GetRecentConversationsAsync()).Status);
    }

    [Fact]
    public async Task ContextKeepsNewestTwentyTurnsAndBoundsOversizedLegacyContent()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T05:00:00Z");
        for (var index = 0; index < 25; index++)
        {
            var input = index == 24 ? $"시작-{new string('가', 25_000)}-끝" : $"질문-{index}";
            var answer = index == 24 ? $"앞-{new string('나', 25_000)}-뒤" : $"답변-{index}";
            var runId = $"run-{index:00}";
            await store.StartRunAsync("bounded-thread", runId, input, now.AddMinutes(index));
            await store.AppendEventAsync(new AgentEvent(
                "bounded-thread", runId, 0, "text_delta", now.AddMinutes(index), answer, null));
            await store.AppendEventAsync(new AgentEvent(
                "bounded-thread", runId, 1, "run_completed", now.AddMinutes(index), null, null));
        }

        var context = await store.GetConversationContextAsync("bounded-thread");

        Assert.Equal(40, context.Count);
        Assert.Equal("질문-5", context[0].Content);
        Assert.StartsWith("시작-", context[^2].Content, StringComparison.Ordinal);
        Assert.EndsWith("-끝", context[^2].Content, StringComparison.Ordinal);
        Assert.Contains("중략", context[^1].Content, StringComparison.Ordinal);
        Assert.All(context, message => Assert.InRange(message.Content.Length, 1, 20_000));
        Assert.InRange(context.Sum(message => message.Content.Length), 1, 64_000);
    }

    [Fact]
    public async Task MigratesVersionFiveConversationIntoOneDurableRunWithoutLosingText()
    {
        var path = Path.Combine(_directory, "legacy.db");
        Directory.CreateDirectory(_directory);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_migrations(version INTEGER NOT NULL PRIMARY KEY, applied_at_utc TEXT NOT NULL);
                INSERT INTO schema_migrations VALUES (5, '2026-07-25T00:00:00.0000000+00:00');
                CREATE TABLE conversations(id TEXT NOT NULL PRIMARY KEY, user_input TEXT NOT NULL, status TEXT NOT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
                CREATE TABLE agent_events(id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, conversation_id TEXT NOT NULL, run_id TEXT NOT NULL, sequence INTEGER NOT NULL, type TEXT NOT NULL, timestamp_utc TEXT NOT NULL, text TEXT NULL, message TEXT NULL, UNIQUE(run_id, sequence));
                INSERT INTO conversations VALUES ('legacy-thread', '예전 질문', 'completed', '2026-07-25T01:00:00.0000000+00:00', '2026-07-25T01:01:00.0000000+00:00');
                INSERT INTO agent_events(conversation_id, run_id, sequence, type, timestamp_utc, text, message) VALUES
                    ('legacy-thread', 'legacy-run', 0, 'text_delta', '2026-07-25T01:00:01.0000000+00:00', '예전 ', NULL),
                    ('legacy-thread', 'legacy-run', 1, 'text_delta', '2026-07-25T01:00:02.0000000+00:00', '답변', NULL),
                    ('legacy-thread', 'legacy-run', 2, 'run_completed', '2026-07-25T01:00:03.0000000+00:00', NULL, NULL);
                """;
            _ = await command.ExecuteNonQueryAsync();
        }

        using var store = new ConversationStore(path);
        await store.InitializeAsync();

        var turn = Assert.Single(await store.GetConversationTurnsAsync("legacy-thread"));
        Assert.Equal("legacy-run", turn.RunId);
        Assert.Equal("예전 질문", turn.UserInput);
        Assert.Equal("예전 답변", turn.AssistantText);
        Assert.Equal(1, Assert.Single(await store.GetRecentConversationsAsync()).TurnCount);
    }

    [Fact]
    public async Task MarksRunsInterruptedWhenTheDesktopRestarts()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        await store.StartRunAsync("running", "run-running", "중단된 요청", DateTimeOffset.UtcNow);

        await store.MarkRunningConversationsInterruptedAsync();

        Assert.Equal("interrupted", Assert.Single(await store.GetRecentConversationsAsync()).Status);
    }

    [Fact]
    public async Task ReopensAnExistingSchemaWithoutLosingHistory()
    {
        var path = Path.Combine(_directory, "history.db");
        using (var first = new ConversationStore(path))
        {
            await first.InitializeAsync();
            await first.StartRunAsync("saved", "run-saved", "저장된 요청", DateTimeOffset.UtcNow);
        }

        using var reopened = new ConversationStore(path);
        await reopened.InitializeAsync();

        Assert.Equal("saved", Assert.Single(await reopened.GetRecentConversationsAsync()).Id);
    }

    [Fact]
    public async Task ConversationRepositoryReplaysRunsThroughTheSharedDatabaseBoundary()
    {
        var path = Path.Combine(_directory, "repository.db");
        using var database = new ConversationDatabase(path);
        await database.InitializeAsync();
        IConversationRepository repository = new ConversationRepository(database);
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z");

        await repository.StartRunAsync("conversation", "run", "질문", now);
        await repository.AppendEventAsync(new AgentEvent(
            "conversation", "run", 0, "text_delta", now.AddSeconds(1), "답변", null));
        await repository.AppendEventAsync(new AgentEvent(
            "conversation", "run", 1, "run_completed", now.AddSeconds(2), null, null));

        Assert.Contains("답변", await repository.GetTranscriptAsync("conversation"), StringComparison.Ordinal);
        Assert.Equal("completed", Assert.Single(await repository.GetRecentConversationsAsync()).Status);
    }

    [Fact]
    public async Task RejectsAFutureSchemaVersionWithoutKeepingTheDatabaseLocked()
    {
        var path = Path.Combine(_directory, "future.db");
        Directory.CreateDirectory(_directory);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE schema_migrations(version INTEGER NOT NULL PRIMARY KEY, applied_at_utc TEXT NOT NULL); " +
                "INSERT INTO schema_migrations VALUES (12, '2026-08-01T00:00:00.0000000+00:00');";
            _ = await command.ExecuteNonQueryAsync();
        }

        using (var store = new ConversationStore(path))
            await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync());

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task PersistsApprovalAndToolExecutionWithoutRawInput()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T02:00:00Z");
        await store.StartRunAsync("conversation-1", "run-1", "민감한 원문", now);
        await store.RecordApprovalAsync(
            new ToolApprovalAuditRecord(
                "conversation-1", "run-1", "call-1", "app.launch.v1", "R1", true, "once", "Calculator 앱을 엽니다.", now),
            CancellationToken.None);
        await store.RecordExecutionAsync(
            new ToolExecutionAuditRecord(
                "conversation-1", "run-1", "call-1", "app.launch.v1", "R1", "succeeded", "Calculator 앱을 실행했어요.", now),
            CancellationToken.None);

        var activity = Assert.Single(await store.GetRecentToolActivityAsync());
        Assert.Equal("app.launch.v1", activity.ToolName);
        Assert.True(activity.Approved);
        Assert.Equal("succeeded", activity.Status);
        Assert.DoesNotContain("민감한 원문", activity.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistsExpiresAndRevokesCapabilityGrantsWithoutScopeContents()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T02:00:00Z");
        var grant = new CapabilityGrant(
            "grant-1", "app.launch.v1", "R1", new string('A', 64), "계산기를 실행합니다.", now, now.AddDays(30));

        await store.SaveGrantAsync(grant, CancellationToken.None);

        Assert.Equal("grant-1", (await store.FindActiveGrantAsync(
            grant.ToolName, grant.Risk, grant.ScopeHash, now.AddDays(1), CancellationToken.None))?.Id);
        Assert.Equal("grant-1", Assert.Single(await store.GetActiveGrantsAsync(now.AddDays(1))).Id);
        Assert.Null(await store.FindActiveGrantAsync(
            grant.ToolName, grant.Risk, grant.ScopeHash, now.AddDays(31), CancellationToken.None));

        await store.RevokeGrantAsync(grant.Id, now.AddDays(2));

        Assert.Empty(await store.GetActiveGrantsAsync(now.AddDays(2)));
    }

    [Fact]
    public async Task PersistsAndCompletesAnUndoJournalEntry()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var undoId = await store.CreateUndoAsync(
            "move", "C:\\source.txt", "C:\\archive\\source.txt", "identity", CancellationToken.None);

        var pending = Assert.Single(await store.GetPendingUndoActivityAsync());
        Assert.Equal(undoId, pending.UndoId);
        Assert.Equal("source.txt", pending.DisplayName);
        Assert.NotNull(await store.GetPendingUndoAsync(undoId, CancellationToken.None));

        await store.MarkUndoneAsync(undoId, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(await store.GetPendingUndoActivityAsync());
        Assert.Null(await store.GetPendingUndoAsync(undoId, CancellationToken.None));
    }

    [Fact]
    public async Task UpsertsFiltersAndDeletesPersonalMemories()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var created = await store.UpsertAsync(
            new PersonalMemoryDraft(
                "alias", "주 프로젝트", "LIGClaw", "general", "conversation:test", now.AddDays(1)),
            now,
            CancellationToken.None);
        var updated = await store.UpsertAsync(
            new PersonalMemoryDraft(
                "alias", "주 프로젝트", "LIGClaw Desktop", "personal", "conversation:test-2", now.AddDays(1)),
            now.AddMinutes(1),
            CancellationToken.None);

        Assert.True(created.Created);
        Assert.False(updated.Created);
        Assert.Equal(created.Memory.Id, updated.Memory.Id);
        Assert.Equal("LIGClaw Desktop", Assert.Single(await store.ListAsync("프로젝트", 10, now, CancellationToken.None)).Value);
        Assert.Empty(await store.ListAsync(null, 10, now.AddDays(2), CancellationToken.None));
        Assert.Equal(created.Memory.Id, Assert.Single(await store.ListForManagementAsync(null, 0, 10, CancellationToken.None)).Id);
        Assert.NotNull(await store.GetForManagementAsync(created.Memory.Id, CancellationToken.None));
        Assert.True(await store.DeleteAsync(created.Memory.Id, CancellationToken.None));
        Assert.Null(await store.GetAsync(created.Memory.Id, now, CancellationToken.None));
    }

    [Fact]
    public async Task ManagementMemoryListSupportsStablePaging()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        for (var index = 0; index < 3; index++)
        {
            await store.UpsertAsync(
                new PersonalMemoryDraft("note", $"항목 {index}", $"값 {index}", "general", "test", null),
                now.AddMinutes(index),
                CancellationToken.None);
        }

        var first = await store.ListForManagementAsync(null, 0, 2, CancellationToken.None);
        var second = await store.ListForManagementAsync(null, 2, 2, CancellationToken.None);

        Assert.Equal(2, first.Count);
        Assert.Single(second);
        Assert.Equal(3, first.Concat(second).Select(memory => memory.Id).Distinct().Count());
    }

    [Fact]
    public async Task ReopensAndRecoversAnInterruptedRecurringScheduleOnce()
    {
        var path = Path.Combine(_directory, "scheduler.db");
        var createdAt = DateTimeOffset.Parse("2026-07-25T00:00:00Z");
        string jobId;
        using (var first = new ConversationStore(path))
        {
            await first.InitializeAsync();
            var job = await first.CreateAsync(
                ScheduleDraft("2026-07-26T09:00:00", ScheduleValues.Daily, ScheduleValues.RunOnceOnResume),
                createdAt,
                CancellationToken.None);
            jobId = job.Id;
            var claim = Assert.IsType<LIGClaw.Application.Scheduling.ScheduledJobClaim>(
                await first.TryClaimDueAsync(
                    DateTimeOffset.Parse("2026-07-26T00:00:00Z"),
                    DateTimeOffset.Parse("2026-07-26T00:02:00Z"),
                    CancellationToken.None));
            Assert.Equal(jobId, claim.Job.Id);
        }

        using var reopened = new ConversationStore(path);
        await reopened.InitializeAsync();
        var resumedAt = DateTimeOffset.Parse("2026-07-26T00:05:00Z");
        var recovery = await reopened.ReconcileOnStartupAsync(resumedAt, CancellationToken.None);

        Assert.Equal(1, recovery.ReadyToRun);
        var resumed = Assert.IsType<LIGClaw.Application.Scheduling.ScheduledJobClaim>(
            await reopened.TryClaimDueAsync(resumedAt, resumedAt.AddMinutes(2), CancellationToken.None));
        await reopened.CompleteRunAsync(resumed.RunId, resumedAt, true, null, CancellationToken.None);
        var jobAfterRun = Assert.IsType<ScheduledNotification>(await reopened.GetAsync(jobId, CancellationToken.None));
        Assert.Equal(ScheduleValues.Pending, jobAfterRun.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-07-27T00:00:00Z"), jobAfterRun.NextRunAtUtc);
    }

    [Fact]
    public async Task StartupAppliesSkipAndAskMisfirePoliciesDeterministically()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var createdAt = DateTimeOffset.Parse("2026-07-25T00:00:00Z");
        var skipped = await store.CreateAsync(
            ScheduleDraft("2026-07-26T09:00:00", ScheduleValues.Once, ScheduleValues.Skip),
            createdAt,
            CancellationToken.None);
        var asked = await store.CreateAsync(
            ScheduleDraft("2026-07-26T09:00:00", ScheduleValues.Once, ScheduleValues.Ask),
            createdAt,
            CancellationToken.None);

        var recovery = await store.ReconcileOnStartupAsync(
            DateTimeOffset.Parse("2026-07-26T00:10:00Z"), CancellationToken.None);

        Assert.Equal(1, recovery.Skipped);
        Assert.Equal(1, recovery.AwaitingDecision);
        Assert.Equal(ScheduleValues.Completed, (await store.GetAsync(skipped.Id, CancellationToken.None))!.Status);
        Assert.Equal(ScheduleValues.AwaitingDecision, (await store.GetAsync(asked.Id, CancellationToken.None))!.Status);
        Assert.True(await store.ResolveMisfireAsync(
            asked.Id, false, DateTimeOffset.Parse("2026-07-26T00:11:00Z"), CancellationToken.None));
        Assert.Equal(ScheduleValues.Completed, (await store.GetAsync(asked.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task RuntimeReconciliationAppliesEveryMisfirePolicyAfterResume()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var createdAt = DateTimeOffset.Parse("2026-07-25T00:00:00Z");
        var skipped = await store.CreateAsync(
            ScheduleDraft("2026-07-26T09:00:00", ScheduleValues.Once, ScheduleValues.Skip),
            createdAt,
            CancellationToken.None);
        var asked = await store.CreateAsync(
            ScheduleDraft("2026-07-26T09:00:00", ScheduleValues.Once, ScheduleValues.Ask),
            createdAt,
            CancellationToken.None);
        var ready = await store.CreateAsync(
            ScheduleDraft("2026-07-26T09:00:00", ScheduleValues.Once, ScheduleValues.RunOnceOnResume),
            createdAt,
            CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-07-26T00:10:00Z");

        var recovery = await store.ReconcileOverdueAsync(now, now.AddMinutes(-1), CancellationToken.None);

        Assert.Equal(new(1, 1, 1), recovery);
        Assert.Equal(ScheduleValues.Completed, (await store.GetAsync(skipped.Id, CancellationToken.None))!.Status);
        Assert.Equal(ScheduleValues.AwaitingDecision, (await store.GetAsync(asked.Id, CancellationToken.None))!.Status);
        Assert.Equal(ScheduleValues.Pending, (await store.GetAsync(ready.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task RuntimeReconciliationReclaimsAnExpiredLease()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var dueAt = DateTimeOffset.Parse("2026-07-26T00:00:00Z");
        var job = await store.CreateAsync(
            ScheduleDraft("2026-07-26T09:00:00", ScheduleValues.Once, ScheduleValues.RunOnceOnResume),
            dueAt.AddDays(-1),
            CancellationToken.None);
        var first = Assert.IsType<LIGClaw.Application.Scheduling.ScheduledJobClaim>(
            await store.TryClaimDueAsync(dueAt, dueAt.AddMinutes(2), CancellationToken.None));

        var recovery = await store.ReconcileOverdueAsync(
            dueAt.AddMinutes(3), dueAt.AddMinutes(2), CancellationToken.None);
        var reclaimed = Assert.IsType<LIGClaw.Application.Scheduling.ScheduledJobClaim>(
            await store.TryClaimDueAsync(dueAt.AddMinutes(3), dueAt.AddMinutes(5), CancellationToken.None));

        Assert.Equal(1, recovery.ReadyToRun);
        Assert.Equal(job.Id, reclaimed.Job.Id);
        Assert.NotEqual(first.RunId, reclaimed.RunId);
    }

    [Fact]
    public async Task ScheduleListSupportsStablePaging()
    {
        using var store = CreateStore();
        await store.InitializeAsync();
        var createdAt = DateTimeOffset.Parse("2026-07-25T00:00:00Z");
        for (var day = 26; day <= 28; day++)
        {
            await store.CreateAsync(
                ScheduleDraft($"2026-07-{day:00}T09:00:00", ScheduleValues.Once, ScheduleValues.Skip),
                createdAt,
                CancellationToken.None);
        }

        var first = await store.ListAsync(true, 0, 2, CancellationToken.None);
        var second = await store.ListAsync(true, 2, 2, CancellationToken.None);

        Assert.Equal(2, first.Count);
        Assert.Single(second);
        Assert.Equal(3, first.Concat(second).Select(job => job.Id).Distinct().Count());
    }

    private static NotificationScheduleDraft ScheduleDraft(string startLocal, string recurrence, string misfire)
    {
        Assert.True(NotificationSchedulePolicy.TryParseLocal(startLocal, out var parsed));
        return new NotificationScheduleDraft(
            "테스트 알림", "예약 실행 테스트", parsed, "Korea Standard Time", recurrence, 1, misfire, "test");
    }

    private ConversationStore CreateStore() => new(Path.Combine(_directory, "history.db"));

    private static AgentEvent Event(long sequence, string type, DateTimeOffset timestamp, string? text = null) =>
        new("conversation-1", "run-1", sequence, type, timestamp, text, null);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
