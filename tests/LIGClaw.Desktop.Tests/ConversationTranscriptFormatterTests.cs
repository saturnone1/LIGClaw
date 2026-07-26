using System.Text;
using LIGClaw.Desktop.Infrastructure.Persistence;

namespace LIGClaw.Desktop.Tests;

public sealed class ConversationTranscriptFormatterTests
{
    [Fact]
    public void SeparatesRolesAndKeepsUserMarkdownInsideAQuote()
    {
        var transcript = ConversationTranscriptFormatter.Format(
        [
            new ConversationTurn("run-1", "# 사용자 제목\n두 번째 줄", "첫 답변", "completed", DateTimeOffset.UtcNow),
            new ConversationTurn("run-2", "후속 질문", "부분 답변", "failed", DateTimeOffset.UtcNow),
        ]);

        Assert.Contains("### 나\r\n> # 사용자 제목\r\n> 두 번째 줄", transcript, StringComparison.Ordinal);
        Assert.Contains("### LIGClaw\r\n첫 답변", transcript, StringComparison.Ordinal);
        Assert.Contains("---", transcript, StringComparison.Ordinal);
        Assert.Contains("요청을 처리하지 못했습니다.", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendsAStreamingTurnWithoutDiscardingPriorTranscript()
    {
        var transcript = new StringBuilder("### 나\r\n> 이전 질문\r\n\r\n### LIGClaw\r\n이전 답변");

        ConversationTranscriptFormatter.AppendTurnStart(transcript, "후속 질문");
        transcript.Append("후속 답변");

        Assert.Contains("이전 답변", transcript.ToString(), StringComparison.Ordinal);
        Assert.Contains("후속 질문", transcript.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("후속 답변", transcript.ToString(), StringComparison.Ordinal);
    }
}
