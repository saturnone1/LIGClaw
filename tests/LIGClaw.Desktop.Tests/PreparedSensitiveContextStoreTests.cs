using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop.Tests;

public sealed class PreparedSensitiveContextStoreTests
{
    [Fact]
    public void Prepared_context_is_bound_to_identity_and_transfers_ownership_once()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        using var store = Store(() => now);
        var identity = Identity();
        var prepared = store.Prepare(identity, Draft([1, 2, 3], [65, 66]));

        var wrongIdentity = store.Take(identity with { RunId = "other-run" }, prepared.Token!);
        var taken = store.Take(identity, prepared.Token!);
        var repeated = store.Take(identity, prepared.Token!);

        Assert.False(wrongIdentity.Success);
        Assert.True(taken.Success, taken.Error);
        using var context = Assert.IsType<PreparedSensitiveContext>(taken.Context);
        Assert.Equal(new byte[] { 1, 2, 3 }, context.EncodedImage.ToArray());
        Assert.Equal(new byte[] { 65, 66 }, context.OcrTextUtf8.ToArray());
        Assert.False(repeated.Success);
    }

    [Fact]
    public void Replacing_or_discarding_a_context_zeroes_owned_buffers()
    {
        var firstImage = new byte[] { 1, 2, 3 };
        var firstText = new byte[] { 65, 66 };
        var secondImage = new byte[] { 4, 5, 6 };
        var secondText = new byte[] { 67, 68 };
        using var store = Store();
        var identity = Identity();
        _ = store.Prepare(identity, Draft(firstImage, firstText));

        var second = store.Prepare(identity, Draft(secondImage, secondText));

        AssertZeroed(firstImage);
        AssertZeroed(firstText);
        Assert.True(store.Discard(identity, second.Token!));
        AssertZeroed(secondImage);
        AssertZeroed(secondText);
    }

    [Fact]
    public void Expired_context_is_unavailable_and_zeroed()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var image = new byte[] { 1, 2, 3 };
        var text = new byte[] { 65, 66 };
        using var store = Store(() => now);
        var identity = Identity();
        var prepared = store.Prepare(identity, Draft(image, text));
        now = now.Add(PreparedSensitiveContextStore.Lifetime);

        var result = store.Take(identity, prepared.Token!);

        Assert.False(result.Success);
        Assert.Contains("만료", result.Error, StringComparison.Ordinal);
        AssertZeroed(image);
        AssertZeroed(text);
    }

    [Theory]
    [InlineData(4_097, 1, 1, 0)]
    [InlineData(4_000, 4_001, 1, 0)]
    [InlineData(1, 1, 8 * 1024 * 1024 + 1, 0)]
    [InlineData(1, 1, 1, 32 * 1024 + 1)]
    public void Oversized_context_is_rejected_and_input_buffers_are_zeroed(
        int width,
        int height,
        int imageBytes,
        int textBytes)
    {
        var image = Enumerable.Repeat((byte)1, imageBytes).ToArray();
        var text = Enumerable.Repeat((byte)2, textBytes).ToArray();
        using var store = Store();
        var draft = new PreparedSensitiveContextDraft("window", width, height, image, text, 0);

        var result = store.Prepare(Identity(), draft);

        Assert.False(result.Success);
        AssertZeroed(image);
        AssertZeroed(text);
    }

    [Fact]
    public void Disposing_the_store_zeroes_the_current_context()
    {
        var image = new byte[] { 1, 2, 3 };
        var text = new byte[] { 65, 66 };
        var store = Store();
        _ = store.Prepare(Identity(), Draft(image, text));

        store.Dispose();

        AssertZeroed(image);
        AssertZeroed(text);
        Assert.Throws<ObjectDisposedException>(() => store.DiscardExpired());
    }

    [Fact]
    public void Invalid_utf8_ocr_text_is_rejected_and_zeroed()
    {
        var image = new byte[] { 1, 2, 3 };
        var text = new byte[] { 0xC3, 0x28 };
        using var store = Store();

        var result = store.Prepare(Identity(), Draft(image, text));

        Assert.False(result.Success);
        AssertZeroed(image);
        AssertZeroed(text);
    }

    private static PreparedSensitiveContextStore Store(Func<DateTimeOffset>? now = null) =>
        new(now, () => Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static PreparedSensitiveContextIdentity Identity() =>
        new("conversation", "run", "tool-call");

    private static PreparedSensitiveContextDraft Draft(byte[] image, byte[] text) =>
        new("window", 1920, 1080, image, text, 0);

    private static void AssertZeroed(byte[] buffer) =>
        Assert.True(buffer.All(value => value == 0));
}
