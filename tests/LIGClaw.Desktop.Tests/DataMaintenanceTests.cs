using LIGClaw.Desktop.Infrastructure.Persistence;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop.Tests;

public sealed class DataMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LIGClaw.Tests", Guid.NewGuid().ToString("N"));
    public DataMaintenanceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Online_backup_is_integrity_checked_and_restored_only_on_next_initialization()
    {
        var sourcePath = Path.Combine(_root, "source.db");
        var backupPath = Path.Combine(_root, "backup.db");
        using (var source = new ConversationStore(sourcePath))
        {
            await source.InitializeAsync();
            await source.StartRunAsync("backup-conversation", "backup-run", "백업할 대화", DateTimeOffset.UtcNow);
            await source.CreateBackupAsync(backupPath);
        }
        Assert.True(File.Exists(backupPath));

        var targetPath = Path.Combine(_root, "target.db");
        using (var target = new ConversationStore(targetPath))
        {
            await target.InitializeAsync();
            Assert.Empty(await target.GetRecentConversationsAsync());
            await target.StageRestoreAsync(backupPath);
            Assert.Empty(await target.GetRecentConversationsAsync());
        }
        using var reopened = new ConversationStore(targetPath);
        await reopened.InitializeAsync();
        Assert.Equal("backup-conversation", Assert.Single(await reopened.GetRecentConversationsAsync()).Id);
        Assert.NotEmpty(Directory.GetFiles(_root, "target.db.before-restore-*.bak"));
    }

    [Fact]
    public async Task Cleanup_removes_only_old_operational_history_and_keeps_conversations()
    {
        using var store = new ConversationStore(Path.Combine(_root, "cleanup.db"));
        await store.InitializeAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-500);
        await store.StartRunAsync("kept-conversation", "run", "보존할 대화", old);
        await store.RecordExecutionAsync(new ToolExecutionAuditRecord("kept-conversation", "run", "tool", "fixture.v1", "R0", "succeeded", "오래된 실행", old), CancellationToken.None);

        var removed = await store.CleanupOperationalHistoryAsync(DateTimeOffset.UtcNow.AddDays(-365));

        Assert.Equal(1, removed);
        Assert.Empty(await store.GetRecentToolActivityAsync());
        Assert.Equal("kept-conversation", Assert.Single(await store.GetRecentConversationsAsync()).Id);
    }

    [Fact]
    public async Task Restore_rejects_a_non_sqlite_file_without_staging_it()
    {
        var invalid = Path.Combine(_root, "invalid.db");
        await File.WriteAllTextAsync(invalid, "not a database");
        var targetPath = Path.Combine(_root, "target-invalid.db");
        using var store = new ConversationStore(targetPath);
        await store.InitializeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => store.StageRestoreAsync(invalid));
        Assert.False(File.Exists(targetPath + ".restore.pending"));
    }

    [Fact]
    public async Task StartupRejectsATamperedPendingRestoreWithoutReplacingLiveData()
    {
        var backupPath = Path.Combine(_root, "valid-backup.db");
        using (var source = new ConversationStore(Path.Combine(_root, "valid-source.db")))
        {
            await source.InitializeAsync();
            await source.CreateBackupAsync(backupPath);
        }
        var targetPath = Path.Combine(_root, "tamper-target.db");
        using (var target = new ConversationStore(targetPath))
        {
            await target.InitializeAsync();
            await target.StartRunAsync("live-conversation", "run", "현재 데이터", DateTimeOffset.UtcNow);
            await target.StageRestoreAsync(backupPath);
        }
        await File.WriteAllTextAsync(targetPath + ".restore.pending", "tampered");

        using (var reopened = new ConversationStore(targetPath))
            await Assert.ThrowsAnyAsync<Exception>(() => reopened.InitializeAsync());

        File.Delete(targetPath + ".restore.pending");
        using var preserved = new ConversationStore(targetPath);
        await preserved.InitializeAsync();
        Assert.Equal("live-conversation", Assert.Single(await preserved.GetRecentConversationsAsync()).Id);
    }

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { } }
}
