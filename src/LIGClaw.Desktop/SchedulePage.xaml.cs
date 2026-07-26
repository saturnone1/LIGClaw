using System.Globalization;
using System.Windows;
using LIGClaw.Application.Scheduling;
using LIGClaw.Domain;

namespace LIGClaw.Desktop;

public partial class SchedulePage : System.Windows.Controls.UserControl
{
    private readonly IScheduleRepository _schedules;
    private int _refreshVersion;
    private CancellationTokenSource? _refreshCancellation;

    internal SchedulePage(IScheduleRepository schedules)
    {
        _schedules = schedules;
        InitializeComponent();
        ScheduleSortBox.ItemsSource = new[]
        {
            new SortChoice("다음 실행순", "next"),
            new SortChoice("최근 수정순", "updated"),
            new SortChoice("이름순", "name"),
        };
        ScheduleSortBox.SelectedValue = "next";
        Loaded += async (_, _) => await ExecuteUiAsync(() => RefreshAsync(), "예약 목록을 불러오지 못했습니다.");
        Unloaded += (_, _) => Interlocked.Exchange(ref _refreshCancellation, null)?.Cancel();
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private async void IncludeInactive_Changed(object sender, RoutedEventArgs e) =>
        await ExecuteUiAsync(() => RefreshAsync(), "예약 목록을 불러오지 못했습니다.");

    private async void ScheduleSortBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        await ExecuteUiAsync(() => RefreshAsync(), "예약 목록을 정렬하지 못했습니다.");

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            var job = await ReadTaggedJobAsync(sender);
            if (job is null) return;
            var editor = new ScheduleEditorWindow(_schedules, job) { Owner = OwnerWindow };
            if (editor.ShowDialog() == true) await RefreshAsync("예약을 수정했어요.");
        }, "예약을 수정하지 못했습니다.");
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            var job = await ReadTaggedJobAsync(sender);
            if (job is null) return;
            var result = System.Windows.MessageBox.Show(
                OwnerWindow,
                $"‘{job.Title}’ 예약을 삭제할까요?\n실행 이력은 안전 감사 목적으로 남고, 이후 알림은 실행되지 않습니다.",
                "예약 삭제",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;
            _ = await _schedules.CancelAsync(job.Id, DateTimeOffset.UtcNow, CancellationToken.None);
            await RefreshAsync("예약을 삭제했어요.");
        }, "예약을 삭제하지 못했습니다.");
    }

    private async void RunMissed_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            if (sender is FrameworkElement { Tag: string id })
                _ = await _schedules.ResolveMisfireAsync(id, true, DateTimeOffset.UtcNow, CancellationToken.None);
            await RefreshAsync("놓친 예약을 실행 대기 상태로 바꿨어요.");
        }, "놓친 예약을 실행 대기 상태로 바꾸지 못했습니다.");
    }

    private async void SkipMissed_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            if (sender is FrameworkElement { Tag: string id })
                _ = await _schedules.ResolveMisfireAsync(id, false, DateTimeOffset.UtcNow, CancellationToken.None);
            await RefreshAsync("놓친 예약을 건너뛰었어요.");
        }, "놓친 예약을 건너뛰지 못했습니다.");
    }

    private async Task<ScheduledNotification?> ReadTaggedJobAsync(object sender) =>
        sender is FrameworkElement { Tag: string id }
            ? await _schedules.GetAsync(id, CancellationToken.None)
            : null;

    internal async Task RefreshAsync(string? completionMessage = null)
    {
        if (!IsLoaded) return;
        var version = Interlocked.Increment(ref _refreshVersion);
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _refreshCancellation, cancellation)?.Cancel();
        var includeInactive = IncludeInactive.IsChecked == true;
        SetPageStatus("예약을 불러오고 있어요…", isError: false);
        IReadOnlyList<ScheduledNotification> jobs;
        try
        {
            jobs = await LoadAllAsync(includeInactive, cancellation.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _refreshCancellation, null, cancellation);
        }
        cancellation.Token.ThrowIfCancellationRequested();
        if (version != Volatile.Read(ref _refreshVersion) || includeInactive != (IncludeInactive.IsChecked == true)) return;
        var sorted = (ScheduleSortBox.SelectedValue as string ?? "next") switch
        {
            "updated" => jobs.OrderByDescending(job => job.UpdatedAtUtc),
            "name" => jobs.OrderBy(job => job.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => jobs.OrderBy(job => job.NextRunAtUtc is null)
                .ThenBy(job => job.NextRunAtUtc)
                .ThenByDescending(job => job.UpdatedAtUtc),
        };
        var items = sorted.Select(ScheduleListItem.Create).ToArray();
        ScheduleList.ItemsSource = items;
        EmptyMessage.Text = includeInactive
            ? "저장된 예약이 없습니다."
            : "진행 중인 예약이 없습니다. 완료·취소된 예약 표시를 켜서 이력을 확인할 수 있습니다.";
        EmptyMessage.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetPageStatus(completionMessage ?? $"예약 {items.Length}개", isError: false);
    }

    private async Task<IReadOnlyList<ScheduledNotification>> LoadAllAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 500;
        var results = new List<ScheduledNotification>();
        while (true)
        {
            var page = await _schedules.ListAsync(
                includeInactive, results.Count, pageSize, cancellationToken);
            results.AddRange(page);
            if (page.Count < pageSize) return results;
        }
    }

    private async Task ExecuteUiAsync(Func<Task> action, string failureMessage)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // A newer refresh or page navigation superseded this request.
        }
        catch (Exception)
        {
            SetPageStatus(failureMessage, isError: true);
        }
    }

    private void SetPageStatus(string message, bool isError)
    {
        PageStatusText.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "DangerBrush" : "TextSecondaryBrush");
        PageStatusText.Text = message;
    }

    private sealed record ScheduleListItem(
        string Id,
        string Title,
        string Message,
        string ScheduleText,
        string StatusText,
        Visibility ManageVisibility,
        Visibility DecisionVisibility)
    {
        public static ScheduleListItem Create(ScheduledNotification job)
        {
            var next = job.NextRunAtUtc is null
                ? "다음 실행 없음"
                : $"다음 실행 {FormatInJobTimeZone(job)}";
            var recurrence = job.Recurrence switch
            {
                ScheduleValues.Once => "한 번",
                ScheduleValues.Daily => job.Interval == 1 ? "매일" : $"{job.Interval}일마다",
                _ => job.Interval == 1 ? "매주" : $"{job.Interval}주마다",
            };
            var active = job.Status is ScheduleValues.Pending or ScheduleValues.AwaitingDecision;
            return new ScheduleListItem(
                job.Id,
                job.Title,
                job.Message,
                $"{next} · {recurrence} · {job.TimeZoneId}",
                StatusTextFor(job.Status),
                active ? Visibility.Visible : Visibility.Collapsed,
                job.Status == ScheduleValues.AwaitingDecision ? Visibility.Visible : Visibility.Collapsed);
        }

        private static string FormatInJobTimeZone(ScheduledNotification job)
        {
            if (job.NextRunAtUtc is null ||
                !NotificationSchedulePolicy.TryFindTimeZone(job.TimeZoneId, out var timeZone)) return "알 수 없음";
            return TimeZoneInfo.ConvertTime(job.NextRunAtUtc.Value, timeZone!)
                .ToString("yyyy.MM.dd HH:mm zzz", CultureInfo.CurrentCulture);
        }

        private static string StatusTextFor(string status) => status switch
        {
            ScheduleValues.Pending => "대기 중",
            ScheduleValues.Running => "실행 중",
            ScheduleValues.AwaitingDecision => "놓친 실행: 사용자 확인 필요",
            ScheduleValues.Completed => "완료",
            ScheduleValues.Failed => "실패",
            ScheduleValues.Cancelled => "삭제됨",
            _ => status,
        };
    }

    private sealed record SortChoice(string Label, string Value);
}
