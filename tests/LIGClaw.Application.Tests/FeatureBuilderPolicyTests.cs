using LIGClaw.Domain;

namespace LIGClaw.Application.Tests;

public sealed class FeatureBuilderPolicyTests
{
    [Fact]
    public void RequirementEnforcesCharacterUtf8AndSecretBoundaries()
    {
        Assert.True(FeatureBuilderPolicy.ValidateRequirement(new string('가', 8_192)).IsValid);
        Assert.Equal(
            "feature_builder.requirement.out_of_bounds",
            FeatureBuilderPolicy.ValidateRequirement(new string('a', 8_193)).ErrorCode);
        Assert.Equal(
            "feature_builder.requirement.out_of_bounds",
            FeatureBuilderPolicy.ValidateRequirement("ok\ud800").ErrorCode);
        Assert.Equal(
            "feature_builder.requirement.secret_detected",
            FeatureBuilderPolicy.ValidateRequirement("메일 token=ordinary-secret-value 사용").ErrorCode);
        var placeholder = FeatureBuilderPolicy.ValidateRequirement(
            "메일 token=${secret:mail-token} placeholder를 사용");
        Assert.True(placeholder.IsValid, placeholder.ErrorCode);
    }

    [Fact]
    public void QuestionsAreOneToThreeAndHaveBoundedPromptAndChoices()
    {
        Assert.True(FeatureBuilderPolicy.ValidateQuestions([
            TextQuestion("q_target"),
            ChoiceQuestion("q_rule"),
            TextQuestion("q_name"),
        ]).IsValid);
        Assert.Equal(
            "feature_builder.questions.count_invalid",
            FeatureBuilderPolicy.ValidateQuestions([]).ErrorCode);
        Assert.Equal(
            "feature_builder.questions.count_invalid",
            FeatureBuilderPolicy.ValidateQuestions([
                TextQuestion("q_a"), TextQuestion("q_b"), TextQuestion("q_c"), TextQuestion("q_d"),
            ]).ErrorCode);
        Assert.Equal(
            "feature_builder.question.prompt_out_of_bounds",
            FeatureBuilderPolicy.ValidateQuestions([
                TextQuestion("q_target") with { Prompt = new string('x', 513) },
            ]).ErrorCode);
        Assert.Equal(
            "feature_builder.question.choices_invalid",
            FeatureBuilderPolicy.ValidateQuestions([
                ChoiceQuestion("q_rule") with { Choices = ["same", "SAME"] },
            ]).ErrorCode);
    }

    [Theory]
    [InlineData("q_value", "API 키 값을 입력해 주세요.")]
    [InlineData("q_value", "Paste the access token value.")]
    [InlineData("q_password", "로그인 설정을 선택하세요.")]
    public void QuestionsCannotSolicitSecretsThroughAnAlternateId(string id, string prompt)
    {
        var result = FeatureBuilderPolicy.ValidateQuestions([
            new FeatureBuilderQuestion(id, prompt, FeatureBuilderAnswerKind.Text),
        ]);

        Assert.Equal("feature_builder.question.secret_input_forbidden", result.ErrorCode);
    }

    [Fact]
    public void SecretSolicitationInAChoiceIsAlsoRejected()
    {
        var result = FeatureBuilderPolicy.ValidateQuestions([
            new FeatureBuilderQuestion(
                "q_auth",
                "다음 단계를 선택하세요.",
                FeatureBuilderAnswerKind.SingleChoice,
                ["연결 화면 열기", "API 키 값을 입력"]),
        ]);

        Assert.Equal("feature_builder.question.secret_input_forbidden", result.ErrorCode);
    }

    [Fact]
    public void SubmittedAnswersMustExactlyMatchQuestionsAndStayAtSixteen()
    {
        var existing = Enumerable.Range(0, 15)
            .Select(index => new FeatureBuilderAnswer($"q_old_{index}", "answer"))
            .ToArray();
        var question = TextQuestion("q_final");

        Assert.True(FeatureBuilderPolicy.ValidateSubmittedAnswers(
            existing,
            [question],
            [new FeatureBuilderAnswer("q_final", "last")]).IsValid);
        Assert.Equal(
            "feature_builder.answers.count_invalid",
            FeatureBuilderPolicy.ValidateSubmittedAnswers(
                existing.Append(new FeatureBuilderAnswer("q_extra", "answer")).ToArray(),
                [question],
                [new FeatureBuilderAnswer("q_final", "last")]).ErrorCode);
        Assert.Equal(
            "feature_builder.answer.question_mismatch",
            FeatureBuilderPolicy.ValidateSubmittedAnswers(
                [],
                [question],
                [new FeatureBuilderAnswer("q_other", "last")]).ErrorCode);
    }

    [Fact]
    public void AnswersEnforceTotalUtf8AndChoiceMembership()
    {
        var oversizedHistory = Enumerable.Range(0, 4)
            .Select(index => new FeatureBuilderAnswer($"q_old_{index}", new string('가', 8_000)))
            .ToArray();

        Assert.Equal(
            "feature_builder.answers.total_out_of_bounds",
            FeatureBuilderPolicy.ValidateSubmittedAnswers(
                oversizedHistory,
                [TextQuestion("q_final")],
                [new FeatureBuilderAnswer("q_final", "last")]).ErrorCode);
        Assert.Equal(
            "feature_builder.answer.choice_invalid",
            FeatureBuilderPolicy.ValidateSubmittedAnswers(
                [],
                [ChoiceQuestion("q_rule")],
                [new FeatureBuilderAnswer("q_rule", "unknown")]).ErrorCode);
    }

    [Fact]
    public void DirectSecretLikeAnswersAreRejectedButExactPlaceholdersAreAllowed()
    {
        var question = TextQuestion("q_notes");

        Assert.Equal(
            "feature_builder.answer.secret_detected",
            FeatureBuilderPolicy.ValidateSubmittedAnswers(
                [],
                [question],
                [new FeatureBuilderAnswer("q_notes", "Authorization: Bearer abcdefghijklmnop")]).ErrorCode);
        Assert.True(FeatureBuilderPolicy.ValidateSubmittedAnswers(
            [],
            [question],
            [new FeatureBuilderAnswer("q_notes", "token=${secret:mail-token}")]).IsValid);
        Assert.Equal(
            "feature_builder.answer.secret_detected",
            FeatureBuilderPolicy.ValidateSubmittedAnswers(
                [],
                [question],
                [new FeatureBuilderAnswer("q_notes", "token=${secret:mail-token}suffix")]).ErrorCode);
    }

    [Theory]
    [InlineData("API key is hunter2")]
    [InlineData("비밀번호는 secret-value")]
    [InlineData("sk_abcdefghijklmnop")]
    public void NaturalOrPrefixedSecretAnswersAreRejected(string value)
    {
        var result = FeatureBuilderPolicy.ValidateSubmittedAnswers(
            [],
            [TextQuestion("q_notes")],
            [new FeatureBuilderAnswer("q_notes", value)]);

        Assert.Equal("feature_builder.answer.secret_detected", result.ErrorCode);
    }

    [Fact]
    public void MissingSecretStatusIsNotMistakenForASecretValue()
    {
        Assert.True(FeatureBuilderPolicy.ValidateSubmittedAnswers(
            [],
            [TextQuestion("q_notes")],
            [new FeatureBuilderAnswer("q_notes", "API key is missing")]).IsValid);
    }

    [Fact]
    public void IdentityAndStateTransitionsAreFixed()
    {
        Assert.True(FeatureBuilderPolicy.IsSessionId("fbs_" + new string('a', 32)));
        Assert.True(FeatureBuilderPolicy.IsGenerationId("fbgen_" + new string('b', 32)));
        Assert.True(FeatureBuilderPolicy.IsProposalId("proposal_" + new string('c', 48)));
        Assert.True(FeatureBuilderPolicy.IsTestRunId("fbtest_" + new string('d', 32)));
        Assert.False(FeatureBuilderPolicy.IsGenerationId("fbgen_" + new string('G', 32)));

        Assert.True(FeatureBuilderPolicy.CanTransition(
            FeatureBuilderSessionState.Collecting,
            FeatureBuilderSessionState.Generating));
        Assert.True(FeatureBuilderPolicy.CanTransition(
            FeatureBuilderSessionState.Generating,
            FeatureBuilderSessionState.ProposalReady));
        Assert.False(FeatureBuilderPolicy.CanTransition(
            FeatureBuilderSessionState.ProposalReady,
            FeatureBuilderSessionState.Generating));
        Assert.True(FeatureBuilderPolicy.CanTransition(
            FeatureBuilderProposalState.Pending,
            FeatureBuilderProposalState.Applied));
        Assert.False(FeatureBuilderPolicy.CanTransition(
            FeatureBuilderProposalState.Applied,
            FeatureBuilderProposalState.Pending));
        Assert.True(FeatureBuilderPolicy.CanTransition(
            FeatureBuilderTestRunState.Running,
            FeatureBuilderTestRunState.Passed));
        Assert.False(FeatureBuilderPolicy.CanTransition(
            FeatureBuilderTestRunState.Passed,
            FeatureBuilderTestRunState.Running));
    }

    [Fact]
    public void QuestionsAndSessionsDefensivelyCopyCollections()
    {
        var choices = new List<string> { "name", "extension" };
        var question = new FeatureBuilderQuestion(
            "q_rule",
            "정리 규칙을 선택하세요.",
            FeatureBuilderAnswerKind.SingleChoice,
            choices);
        choices[0] = "tampered";

        Assert.Equal("name", question.Choices[0]);
    }

    private static FeatureBuilderQuestion TextQuestion(string id) =>
        new(id, "대상 값을 알려 주세요.", FeatureBuilderAnswerKind.Text);

    private static FeatureBuilderQuestion ChoiceQuestion(string id) =>
        new(id, "정리 규칙을 선택하세요.", FeatureBuilderAnswerKind.SingleChoice, ["name", "extension"]);
}
