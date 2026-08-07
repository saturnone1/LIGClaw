using System.Globalization;
using System.Windows;
using LIGClaw.Application.Scheduling;
using LIGClaw.Domain;

namespace LIGClaw.Desktop;

public partial class AgentJobHistoryWindow : Window
{
    internal AgentJobHistoryWindow(ScheduledAgentJob job, IReadOnlyList<AgentJobRunRecord> runs)
    {
        InitializeComponent();
        HeadingText.Text = job.Title;
        SummaryText.Text = $"상태 {job.Status} · 실행 이력 {runs.Count}개 · 결과는 최대 {job.ResultMaxCharacters:N0}자로 제한됩니다.";
        RunList.ItemsSource = runs.Select(run => new RunItem(
            $"{StatusText(run.Status)} · 시도 {run.Attempt}",
            $"예약 {run.ScheduledAtUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.CurrentCulture)} · 시작 {run.StartedAtUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm:ss", CultureInfo.CurrentCulture)}",
            run.ResultText ?? (run.ErrorCode is null ? "저장된 결과가 없습니다." : $"오류 코드: {run.ErrorCode}"))).ToArray();
    }

    private static string StatusText(string status) => status switch
    {
        "succeeded" => "성공",
        "failed" => "실패",
        "running" => "실행 중",
        "interrupted" => "중단됨",
        "skipped" => "건너뜀",
        _ => status,
    };

    private sealed record RunItem(string Header, string Timing, string Result);
}
