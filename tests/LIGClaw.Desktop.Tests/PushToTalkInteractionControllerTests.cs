using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class PushToTalkInteractionControllerTests
{
    [Fact]
    public async Task ReleaseDuringDelayedStartWaitsAndStopsExactlyOnce()
    {
        var recognizer = new FakeRecognizer { DelayStart = true };
        var controller = new PushToTalkInteractionController(recognizer);

        var press = controller.PressAsync();
        var release = controller.ReleaseAsync();
        recognizer.CompleteStart(new(PushToTalkStatus.Listening));

        Assert.Equal(PushToTalkStatus.NoSpeech, (await press).Status);
        Assert.Equal(PushToTalkStatus.Recognized, (await release)!.Status);
        Assert.Equal(1, recognizer.StopCount);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public async Task DuplicateReleaseDoesNotStopTwice()
    {
        var recognizer = new FakeRecognizer();
        var controller = new PushToTalkInteractionController(recognizer);

        await controller.PressAsync();
        var first = controller.ReleaseAsync();
        var duplicate = await controller.ReleaseAsync();

        Assert.Null(duplicate);
        Assert.Equal(PushToTalkStatus.Recognized, (await first)!.Status);
        Assert.Equal(1, recognizer.StopCount);
    }

    [Fact]
    public async Task FailedStartResetsStateAndAllowsRetry()
    {
        var recognizer = new FakeRecognizer
        {
            StartResults = new Queue<PushToTalkResult>(
            [new(PushToTalkStatus.PermissionDenied), new(PushToTalkStatus.Listening)]),
        };
        var controller = new PushToTalkInteractionController(recognizer);

        Assert.Equal(PushToTalkStatus.PermissionDenied, (await controller.PressAsync()).Status);
        Assert.False(controller.IsActive);
        Assert.Equal(PushToTalkStatus.Listening, (await controller.PressAsync()).Status);
        Assert.True(controller.IsActive);
    }

    [Fact]
    public async Task CancelClearsAnActiveSession()
    {
        var recognizer = new FakeRecognizer();
        var controller = new PushToTalkInteractionController(recognizer);

        await controller.PressAsync();
        await controller.CancelAsync();

        Assert.Equal(1, recognizer.CancelCount);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public async Task CancelDuringDelayedStartWaitsAndPreventsListeningState()
    {
        var recognizer = new FakeRecognizer { DelayStart = true };
        var controller = new PushToTalkInteractionController(recognizer);

        var press = controller.PressAsync();
        var cancel = controller.CancelAsync();
        Assert.True(controller.IsEnding);
        recognizer.CompleteStart(new(PushToTalkStatus.Listening));

        Assert.Equal(PushToTalkStatus.NoSpeech, (await press).Status);
        await cancel;
        Assert.Equal(1, recognizer.CancelCount);
        Assert.False(controller.IsActive);
    }

    private sealed class FakeRecognizer : IPushToTalkRecognizer
    {
        private readonly TaskCompletionSource<PushToTalkResult> _start =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool DelayStart { get; init; }
        internal Queue<PushToTalkResult> StartResults { get; init; } = [];
        internal int StopCount { get; private set; }
        internal int CancelCount { get; private set; }

        public Task<PushToTalkResult> StartAsync(CancellationToken cancellationToken = default)
        {
            if (DelayStart) return _start.Task;
            return Task.FromResult(StartResults.TryDequeue(out var result)
                ? result
                : new PushToTalkResult(PushToTalkStatus.Listening));
        }

        internal void CompleteStart(PushToTalkResult result) => _start.SetResult(result);

        public Task<PushToTalkResult> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.FromResult(new PushToTalkResult(PushToTalkStatus.Recognized, "테스트 입력"));
        }

        public Task CancelAsync()
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
