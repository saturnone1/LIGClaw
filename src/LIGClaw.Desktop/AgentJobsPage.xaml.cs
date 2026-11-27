using System.Globalization;
using System.Windows;
using LIGClaw.Application.Scheduling;
using LIGClaw.Domain;

namespace LIGClaw.Desktop;

public partial class AgentJobsPage : System.Windows.Controls.UserControl
{
    private readonly IAgentJobRepository _jobs;
    private CancellationTokenSource? _refreshCancellation;
    private int _refreshVersion;

    internal AgentJobsPage(IAgentJobRepository jobs)
    {
        _jobs = jobs;
        InitializeComponent();
        Loaded += async (_, _) => await ExecuteUiAsync(() => RefreshAsync(), "Agent 작업을 불러오지 못했습니다.");
        Unloaded += (_, _) => Interlocked.Exchange(ref _refreshCancellation, null)?.Cancel();
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var editor = new AgentJobEditorWindow(_jobs) { Owner = OwnerWindow };
        if (editor.ShowDialog() == true) await RefreshAsync("Agent 작업을 예약했어요.");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiAsync(() => RefreshAsync(), "Agent 작업을 새로 고치지 못했습니다.");

    private async void IncludeInactive_Changed(object sender, RoutedEventArgs e) =>
        await ExecuteUiAsync(() => RefreshAsync(), "Agent 작업을 불러오지 못했습니다.");

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            var job = await ReadTaggedJobAsync(sender);
            if (job is null) return;
            if (System.Windows.MessageBox.Show(
                    OwnerWindow,
                    $"‘{job.Title}’ Agent 작업을 취소할까요?\n이미 저장된 실행 이력은 유지됩니다.",
                    "Agent 작업 취소",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _ = await _jobs.CancelAgentJobAsync(job.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            await RefreshAsync("Agent 작업을 취소했어요.");
        }, "Agent 작업을 취소하지 못했습니다.");
    }

    private async void RunMissed_Click(object sender, RoutedEventArgs e)
    {
        await ResolveMissedAsync(sender, true, "놓친 Agent 작업을 실행 대기로 바꿨어요.");
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) =>
        await SetPausedAsync(sender, true, "Agent 작업을 일시정지했어요.");

    private async void Resume_Click(object sender, RoutedEventArgs e) =>
        await SetPausedAsync(sender, false, "Agent 작업을 재개했어요.");

    private async Task SetPausedAsync(object sender, bool paused, string message)
    {
        await ExecuteUiAsync(async () =>
        {
            if (sender is FrameworkElement { Tag: string id })
                _ = await _jobs.SetAgentJobPausedAsync(id, paused, DateTimeOffset.UtcNow, CancellationToken.None);
            await RefreshAsync(message);
        }, paused ? "Agent 작업을 일시정지하지 못했습니다." : "Agent 작업을 재개하지 못했습니다.");
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            if (sender is FrameworkElement { Tag: string id })
                _ = await _jobs.RetryAgentJobNowAsync(id, DateTimeOffset.UtcNow, CancellationToken.None);
            await RefreshAsync("Agent 작업을 즉시 재시도하도록 예약했어요.");
        }, "Agent 작업을 다시 실행하지 못했습니다.");
    }

    private async void SkipMissed_Click(object sender, RoutedEventArgs e)
    {
        await ResolveMissedAsync(sender, false, "놓친 Agent 작업을 건너뛰었어요.");
    }

    private async Task ResolveMissedAsync(object sender, bool runNow, string message)
    {
        await ExecuteUiAsync(async () =>
        {
            if (sender is FrameworkElement { Tag: string id })
                _ = await _jobs.ResolveAgentJobMisfireAsync(id, runNow, DateTimeOffset.UtcNow, CancellationToken.None);
            await RefreshAsync(message);
        }, "놓친 Agent 작업을 처리하지 못했습니다.");
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            var job = await ReadTaggedJobAsync(sender);
            if (job is null) return;
            var runs = await _jobs.ListAgentJobRunsAsync(job.Id, 50, CancellationToken.None);
            new AgentJobHistoryWindow(job, runs) { Owner = OwnerWindow }.ShowDialog();
        }, "실행 결과를 불러오지 못했습니다.");
    }

    private Task<ScheduledAgentJob?> ReadTaggedJobAsync(object sender) =>
        sender is FrameworkElement { Tag: string id }
            ? _jobs.GetAgentJobAsync(id, CancellationToken.None)
            : Task.FromResult<ScheduledAgentJob?>(null);

    internal async Task RefreshAsync(string? completionMessage = null)
    {
        if (!IsLoaded) return;
        var version = Interlocked.Increment(ref _refreshVersion);
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _refreshCancellation, cancellation)?.Cancel();
        var includeInactive = IncludeInactive.IsChecked == true;
        SetStatus("Agent 작업을 불러오고 있어요…", false);
        var jobs = await _jobs.ListAgentJobsAsync(includeInactive, 0, 500, cancellation.Token);
        Interlocked.CompareExchange(ref _refreshCancellation, null, cancellation);
        cancellation.Token.ThrowIfCancellationRequested();
        if (version != Volatile.Read(ref _refreshVersion) || includeInactive != (IncludeInactive.IsChecked == true)) return;
        var items = jobs.Select(AgentJobListItem.Create).ToArray();
        AgentJobList.ItemsSource = items;
        EmptyMessage.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(completionMessage ?? $"Agent 작업 {items.Length}개", false);
    }

    private async Task ExecuteUiAsync(Func<Task> action, string failure)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch { SetStatus(failure, true); }
    }

    private void SetStatus(string text, bool error)
    {
        PageStatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "DangerBrush" : "TextSecondaryBrush");
        PageStatusText.Text = text;
    }

    private sealed record AgentJobListItem(
        string Id,
        string Title,
        string Prompt,
        string ScheduleText,
        string StatusText,
        Visibility CancelVisibility,
        Visibility PauseVisibility,
        Visibility ResumeVisibility,
        Visibility RetryVisibility,
        Visibility DecisionVisibility)
    {
        public static AgentJobListItem Create(ScheduledAgentJob job)
        {
            var next = job.NextRunAtUtc is null
                ? "다음 실행 없음"
                : $"다음 실행 {FormatLocal(job)}";
            var recurrence = job.Recurrence switch
            {
                ScheduleValues.Once => "한 번",
                ScheduleValues.Daily => job.Interval == 1 ? "매일" : $"{job.Interval}일마다",
                _ => job.Interval == 1 ? "매주" : $"{job.Interval}주마다",
            };
            return new AgentJobListItem(
                job.Id, job.Title, job.Prompt,
                $"{next} · {recurrence} · 최대 {job.MaxRuntimeSeconds}초 · 시도 {job.AttemptCount}/{job.MaxAttempts}",
                FormatStatus(job.Status),
                job.Status is ScheduleValues.Pending or ScheduleValues.AwaitingDecision or ScheduleValues.Paused ? Visibility.Visible : Visibility.Collapsed,
                job.Status is ScheduleValues.Pending or ScheduleValues.AwaitingDecision ? Visibility.Visible : Visibility.Collapsed,
                job.Status == ScheduleValues.Paused ? Visibility.Visible : Visibility.Collapsed,
                job.Status is ScheduleValues.Failed or ScheduleValues.Completed or ScheduleValues.Cancelled ? Visibility.Visible : Visibility.Collapsed,
                job.Status == ScheduleValues.AwaitingDecision ? Visibility.Visible : Visibility.Collapsed);
        }

        private static string FormatLocal(ScheduledAgentJob job)
        {
            if (job.NextRunAtUtc is null ||
                !NotificationSchedulePolicy.TryFindTimeZone(job.TimeZoneId, out var zone)) return "알 수 없음";
            return TimeZoneInfo.ConvertTime(job.NextRunAtUtc.Value, zone!)
                .ToString("yyyy.MM.dd HH:mm zzz", CultureInfo.CurrentCulture);
        }

        private static string FormatStatus(string status) => status switch
        {
            ScheduleValues.Pending => "대기 중",
            ScheduleValues.Running => "실행 중",
            ScheduleValues.AwaitingDecision => "놓친 실행: 확인 필요",
            ScheduleValues.Completed => "완료",
            ScheduleValues.Failed => "실패",
            ScheduleValues.Cancelled => "취소됨",
            ScheduleValues.Paused => "일시정지",
            _ => status,
        };
    }
}
