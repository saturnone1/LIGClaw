using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class AtomicSettingsMutationTests
{
    [Fact]
    public void CredentialFailureRestoresEveryMetadataValue()
    {
        var metadata = new Dictionary<string, string?>
        {
            ["url"] = "old-url",
            ["model"] = "old-model",
        };
        var credential = "old-secret";

        Assert.Throws<InvalidOperationException>(() => AtomicSettingsMutation.Execute(
            () =>
            {
                metadata["url"] = "new-url";
                metadata["model"] = "new-model";
                credential = "new-secret";
                throw new InvalidOperationException("credential write failed");
            },
            () => metadata["url"] = "old-url",
            () => metadata["model"] = "old-model",
            () => credential = "old-secret"));

        Assert.Equal("old-url", metadata["url"]);
        Assert.Equal("old-model", metadata["model"]);
        Assert.Equal("old-secret", credential);
    }

    [Fact]
    public void AttemptsAllRollbackStepsAndReportsApplyAndRollbackFailures()
    {
        var finalRollbackRan = false;

        var exception = Assert.Throws<AggregateException>(() => AtomicSettingsMutation.Execute(
            () => throw new InvalidOperationException("save failed"),
            () => throw new IOException("first rollback failed"),
            () => finalRollbackRan = true));

        Assert.True(finalRollbackRan);
        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.IsType<InvalidOperationException>(exception.InnerExceptions[0]);
        Assert.IsType<IOException>(exception.InnerExceptions[1]);
    }
}
