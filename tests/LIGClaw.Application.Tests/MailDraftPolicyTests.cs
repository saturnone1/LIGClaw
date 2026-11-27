using LIGClaw.Application.Mail;

namespace LIGClaw.Application.Tests;

public sealed class MailDraftPolicyTests
{
    [Fact]
    public void ValidatesPlainTextDraftWithoutTransportOrSecretFields()
    {
        var result = MailDraftPolicy.Validate(ValidInput(
            to: ["owner@example.com"],
            cc: ["reviewer@example.com"],
            bcc: ["archive@example.com"],
            subject: "주간 보고서 초안",
            body: "검토 부탁드립니다."));

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorCode);
        var draft = Assert.IsType<MailDraft>(result.Draft);
        Assert.Equal(["owner@example.com"], draft.To);
        Assert.Equal(["reviewer@example.com"], draft.Cc);
        Assert.Equal(["archive@example.com"], draft.Bcc);
        Assert.Equal("주간 보고서 초안", draft.Subject);
        Assert.Equal("검토 부탁드립니다.", draft.Body);
        Assert.Matches("^sha256:[0-9a-f]{64}$", draft.CanonicalHash);
        Assert.Equal(draft.CanonicalHash, MailDraftPolicy.ComputeCanonicalHash(draft));

        Assert.Equal(
            ["Bcc", "Body", "CanonicalHash", "Cc", "Subject", "To"],
            typeof(MailDraft).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RequiresToAndLimitsAllRecipientCategoriesToSixteen()
    {
        var missingTo = MailDraftPolicy.Validate(ValidInput(
            to: [],
            cc: ["cc@example.com"]));
        var sixteen = MailDraftPolicy.Validate(ValidInput(
            to: ["to@example.com"],
            cc: Enumerable.Range(0, 15).Select(index => $"cc{index}@example.com").ToArray()));
        var seventeen = MailDraftPolicy.Validate(ValidInput(
            to: ["to@example.com"],
            cc: Enumerable.Range(0, 16).Select(index => $"cc{index}@example.com").ToArray()));

        Assert.False(missingTo.IsValid);
        Assert.Equal(MailDraftPolicy.ToRequired, missingTo.ErrorCode);
        Assert.True(sixteen.IsValid);
        Assert.False(seventeen.IsValid);
        Assert.Equal(MailDraftPolicy.RecipientCountInvalid, seventeen.ErrorCode);
    }

    [Fact]
    public void AcceptsExactly254AsciiAddressCharactersAndRejects255()
    {
        var local = new string('a', 64);
        var domain254 = $"{new string('b', 63)}.{new string('c', 63)}.{new string('d', 61)}";
        var domain255 = $"{new string('b', 63)}.{new string('c', 63)}.{new string('d', 62)}";
        var exact = $"{local}@{domain254}";
        var tooLong = $"{local}@{domain255}";

        Assert.Equal(254, exact.Length);
        Assert.Equal(255, tooLong.Length);
        Assert.True(MailDraftPolicy.Validate(ValidInput(to: [exact])).IsValid);
        Assert.Equal(
            MailDraftPolicy.RecipientInvalid,
            MailDraftPolicy.Validate(ValidInput(to: [tooLong])).ErrorCode);
    }

    [Theory]
    [InlineData("plain-address")]
    [InlineData("two@@example.com")]
    [InlineData(".user@example.com")]
    [InlineData("user.@example.com")]
    [InlineData("user@example..com")]
    [InlineData("user@-example.com")]
    [InlineData("user@example-.com")]
    [InlineData("user@example.com.")]
    [InlineData("us er@example.com")]
    [InlineData("사용자@example.com")]
    public void RejectsNonCanonicalAsciiEmailAddresses(string address)
    {
        var result = MailDraftPolicy.Validate(ValidInput(to: [address]));

        Assert.False(result.IsValid);
        Assert.Equal(MailDraftPolicy.RecipientInvalid, result.ErrorCode);
        Assert.Null(result.Draft);
    }

    [Fact]
    public void RejectsCaseInsensitiveDuplicatesWithinAndAcrossCategories()
    {
        var withinTo = MailDraftPolicy.Validate(ValidInput(
            to: ["User@example.com", "user@EXAMPLE.com"]));
        var acrossCategories = MailDraftPolicy.Validate(ValidInput(
            to: ["User@example.com"],
            bcc: ["user@EXAMPLE.COM"]));

        Assert.Equal(MailDraftPolicy.RecipientDuplicate, withinTo.ErrorCode);
        Assert.Equal(MailDraftPolicy.RecipientDuplicate, acrossCategories.ErrorCode);
    }

    [Fact]
    public void EnforcesSubjectScalarAndUtf8BoundsExactly()
    {
        var exact = string.Concat(Enumerable.Repeat("😀", MailDraftPolicy.MaximumSubjectScalars));
        var tooMany = exact + "😀";

        var accepted = MailDraftPolicy.Validate(ValidInput(subject: exact));
        var rejected = MailDraftPolicy.Validate(ValidInput(subject: tooMany));

        Assert.True(accepted.IsValid);
        Assert.Equal(MailDraftPolicy.MaximumSubjectUtf8Bytes, System.Text.Encoding.UTF8.GetByteCount(exact));
        Assert.False(rejected.IsValid);
        Assert.Equal(MailDraftPolicy.SubjectInvalid, rejected.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("subject\nnext")]
    [InlineData("subject\rnext")]
    [InlineData("subject\u0001")]
    [InlineData("subject\u2028next")]
    public void RejectsUnsafeOrMalformedSubjects(string subject)
    {
        var result = MailDraftPolicy.Validate(ValidInput(subject: subject));

        Assert.False(result.IsValid);
        Assert.Equal(MailDraftPolicy.SubjectInvalid, result.ErrorCode);
    }

    [Fact]
    public void EnforcesBodyScalarAndUtf8BoundsExactly()
    {
        var exactScalarsAndBytes = new string('한', MailDraftPolicy.MaximumBodyScalars);
        var tooManyScalars = exactScalarsAndBytes + "한";
        var exactBytes = string.Concat(Enumerable.Repeat("😀", MailDraftPolicy.MaximumBodyUtf8Bytes / 4));
        var tooManyBytes = exactBytes + "😀";

        Assert.Equal(MailDraftPolicy.MaximumBodyUtf8Bytes,
            System.Text.Encoding.UTF8.GetByteCount(exactScalarsAndBytes));
        Assert.True(MailDraftPolicy.Validate(ValidInput(body: exactScalarsAndBytes)).IsValid);
        Assert.Equal(
            MailDraftPolicy.BodyInvalid,
            MailDraftPolicy.Validate(ValidInput(body: tooManyScalars)).ErrorCode);
        Assert.Equal(MailDraftPolicy.MaximumBodyUtf8Bytes,
            System.Text.Encoding.UTF8.GetByteCount(exactBytes));
        Assert.True(MailDraftPolicy.Validate(ValidInput(body: exactBytes)).IsValid);
        Assert.Equal(
            MailDraftPolicy.BodyInvalid,
            MailDraftPolicy.Validate(ValidInput(body: tooManyBytes)).ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    [InlineData("body\u0000")]
    [InlineData("body\u000b")]
    public void RejectsUnsafeOrMalformedBodies(string body)
    {
        var result = MailDraftPolicy.Validate(ValidInput(body: body));

        Assert.False(result.IsValid);
        Assert.Equal(MailDraftPolicy.BodyInvalid, result.ErrorCode);
    }

    [Fact]
    public void RejectsUnpairedSurrogatesInSubjectAndBody()
    {
        var highSurrogate = new string((char)0xd800, 1);
        var lowSurrogate = new string((char)0xdfff, 1);

        Assert.Equal(
            MailDraftPolicy.SubjectInvalid,
            MailDraftPolicy.Validate(ValidInput(subject: highSurrogate)).ErrorCode);
        Assert.Equal(
            MailDraftPolicy.BodyInvalid,
            MailDraftPolicy.Validate(ValidInput(body: lowSurrogate)).ErrorCode);
    }

    [Fact]
    public void AllowsOnlyTabAndNormalizedLfAmongBodyControls()
    {
        var result = MailDraftPolicy.Validate(ValidInput(body: "첫 줄\t값\r\n둘째 줄\r셋째 줄"));

        Assert.True(result.IsValid);
        Assert.Equal("첫 줄\t값\n둘째 줄\n셋째 줄", result.Draft!.Body);
    }

    [Fact]
    public void NormalizationMakesHashDeterministicButExactSubjectStillMatters()
    {
        var crlf = Valid(ValidInput(subject: "Status", body: "one\r\ntwo\rthree"));
        var lf = Valid(ValidInput(subject: "Status", body: "one\ntwo\nthree"));
        var differentSubject = Valid(ValidInput(subject: "status", body: "one\ntwo\nthree"));

        Assert.Equal("one\ntwo\nthree", crlf.Body);
        Assert.Equal(crlf.CanonicalHash, lf.CanonicalHash);
        Assert.NotEqual(crlf.CanonicalHash, differentSubject.CanonicalHash);
    }

    [Fact]
    public void HashBindsRecipientCategoryOrderAndLengthFramedFieldBoundaries()
    {
        var baseline = Valid(ValidInput(
            to: ["a@example.com", "b@example.com"],
            cc: ["c@example.com"],
            subject: "a",
            body: "bc"));
        var reordered = Valid(ValidInput(
            to: ["b@example.com", "a@example.com"],
            cc: ["c@example.com"],
            subject: "a",
            body: "bc"));
        var recategorized = Valid(ValidInput(
            to: ["a@example.com"],
            cc: ["b@example.com", "c@example.com"],
            subject: "a",
            body: "bc"));
        var shiftedTextBoundary = Valid(ValidInput(
            to: ["a@example.com", "b@example.com"],
            cc: ["c@example.com"],
            subject: "ab",
            body: "c"));

        Assert.NotEqual(baseline.CanonicalHash, reordered.CanonicalHash);
        Assert.NotEqual(baseline.CanonicalHash, recategorized.CanonicalHash);
        Assert.NotEqual(baseline.CanonicalHash, shiftedTextBoundary.CanonicalHash);
    }

    [Fact]
    public void ExistingDraftHashRemainsStableAfterSharingTheAddressPolicy()
    {
        var draft = Valid(ValidInput(
            to: ["owner@example.com"],
            cc: ["reviewer@example.com"],
            bcc: [],
            subject: "Status",
            body: "one\r\ntwo"));

        Assert.Equal("one\ntwo", draft.Body);
        Assert.Equal(
            "sha256:1d8da0dd934a06f2136724846cf95c0ee3facb4b361d46333a15293f674ea056",
            draft.CanonicalHash);
    }

    [Fact]
    public void InputAndValidatedCollectionsAreDefensiveImmutableCopies()
    {
        var to = new List<string> { "first@example.com" };
        var cc = new List<string> { "cc@example.com" };
        var input = ValidInput(to: to, cc: cc);
        to[0] = "mutated@example.com";
        cc.Add("later@example.com");

        Assert.Equal(["first@example.com"], input.To);
        Assert.Equal(["cc@example.com"], input.Cc);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)input.To).Add("blocked@example.com"));

        var draft = Valid(input);
        Assert.Equal(["first@example.com"], draft.To);
        Assert.Equal(["cc@example.com"], draft.Cc);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)draft.To)[0] = "blocked@example.com");
    }

    [Fact]
    public void ErrorsAndToStringNeverEchoRawDraftContent()
    {
        const string marker = "raw-secret-marker";
        var invalidInput = ValidInput(
            to: [$"{marker}@example.com"],
            subject: $"{marker}\ninvalid",
            body: marker);
        var invalid = MailDraftPolicy.Validate(invalidInput);

        Assert.False(invalid.IsValid);
        Assert.Equal(MailDraftPolicy.SubjectInvalid, invalid.ErrorCode);
        Assert.DoesNotContain(marker, invalidInput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, invalid.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, invalid.ErrorCode, StringComparison.Ordinal);

        var valid = Valid(ValidInput(
            to: [$"{marker}@example.com"],
            subject: marker,
            body: marker));
        Assert.DoesNotContain(marker, valid.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NullInputAndNullTextReturnOnlyFixedErrors()
    {
        var nullInput = MailDraftPolicy.Validate(null);
        var nullSubject = MailDraftPolicy.Validate(ValidInput(subject: null));
        var nullBody = MailDraftPolicy.Validate(ValidInput(body: null));

        Assert.Equal(MailDraftPolicy.InputInvalid, nullInput.ErrorCode);
        Assert.Equal(MailDraftPolicy.SubjectInvalid, nullSubject.ErrorCode);
        Assert.Equal(MailDraftPolicy.BodyInvalid, nullBody.ErrorCode);
        Assert.All(new[] { nullInput, nullSubject, nullBody }, result => Assert.Null(result.Draft));
    }

    private static MailDraftInput ValidInput(
        IReadOnlyList<string>? to = null,
        IReadOnlyList<string>? cc = null,
        IReadOnlyList<string>? bcc = null,
        string? subject = "업무 안내",
        string? body = "확인 부탁드립니다.") =>
        new(to ?? ["to@example.com"], cc, bcc, subject, body);

    private static MailDraft Valid(MailDraftInput input)
    {
        var result = MailDraftPolicy.Validate(input);
        Assert.True(result.IsValid, result.ErrorCode);
        return Assert.IsType<MailDraft>(result.Draft);
    }
}
