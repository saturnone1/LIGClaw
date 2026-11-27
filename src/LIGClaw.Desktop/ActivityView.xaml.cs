using System.Windows;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop;

public partial class ActivityView : System.Windows.Controls.UserControl
{
    private IReadOnlyList<ToolActivitySummary> _activity = [];
    private IReadOnlyList<UndoActivitySummary> _pendingUndo = [];

    public ActivityView()
    {
        InitializeComponent();
        ActivityFilterBox.ItemsSource = new[]
        {
            new ActivityFilter("전체 상태", "all"),
            new ActivityFilter("완료", "succeeded"),
            new ActivityFilter("실패", "failed"),
            new ActivityFilter("허용 안 함", "denied"),
            new ActivityFilter("중단", "cancelled"),
        };
        ActivityFilterBox.SelectedValue = "all";
        ActivitySortBox.ItemsSource = new[]
        {
            new ActivitySort("최신순", "newest"),
            new ActivitySort("오래된순", "oldest"),
            new ActivitySort("이름순", "name"),
        };
        ActivitySortBox.SelectedValue = "newest";
        SetItems([], []);
    }

    internal ActivityView(
        IReadOnlyList<ToolActivitySummary> activity,
        IReadOnlyList<UndoActivitySummary> pendingUndo)
        : this() => SetItems(activity, pendingUndo);

    internal event EventHandler<string>? UndoRequested;

    internal void SetItems(
        IReadOnlyList<ToolActivitySummary> activity,
        IReadOnlyList<UndoActivitySummary> pendingUndo)
    {
        _activity = activity;
        _pendingUndo = pendingUndo;
        ApplyFilter();
        UndoList.ItemsSource = _pendingUndo;
        EmptyUndoMessage.Visibility = _pendingUndo.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetFeedback(string message, bool isError)
    {
        ActivityFeedbackText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "SuccessBrush");
        ActivityFeedbackText.Text = message;
    }

    private void ActivityFilterBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (!IsInitialized) return;
        var status = ActivityFilterBox.SelectedValue as string ?? "all";
        IEnumerable<ToolActivitySummary> filtered = status == "all"
            ? _activity
            : _activity.Where(item => StringComparer.Ordinal.Equals(item.Status, status)).ToArray();
        filtered = (ActivitySortBox.SelectedValue as string ?? "newest") switch
        {
            "oldest" => filtered.OrderBy(item => item.CreatedAtUtc),
            "name" => filtered.OrderBy(item => item.Summary, StringComparer.CurrentCultureIgnoreCase),
            _ => filtered.OrderByDescending(item => item.CreatedAtUtc),
        };
        var items = filtered.ToArray();
        ActivityList.ItemsSource = items;
        EmptyMessage.Text = status == "all"
            ? "아직 기록된 실행이 없습니다."
            : "선택한 상태의 실행 기록이 없습니다.";
        EmptyMessage.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityResultText.Text = $"실행 {items.Length}개 · 되돌리기 {_pendingUndo.Count}개";
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string undoId })
            UndoRequested?.Invoke(this, undoId);
    }

    private sealed record ActivityFilter(string Label, string Status);
    private sealed record ActivitySort(string Label, string Value);
}
