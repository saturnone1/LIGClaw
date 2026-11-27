using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using LIGClaw.Application.Memory;
using LIGClaw.Domain;

namespace LIGClaw.Desktop;

public partial class MemoryPage : System.Windows.Controls.UserControl
{
    private readonly IMemoryRepository _memories;
    private int _refreshVersion;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _searchDebounceCancellation;

    internal MemoryPage(IMemoryRepository memories)
    {
        _memories = memories;
        InitializeComponent();
        MemorySortBox.ItemsSource = new[]
        {
            new SortChoice("최근 수정순", "updated"),
            new SortChoice("오래된순", "oldest"),
            new SortChoice("이름순", "name"),
        };
        MemorySortBox.SelectedValue = "updated";
        Loaded += async (_, _) => await ExecuteUiAsync(() => RefreshAsync(), "기억 목록을 불러오지 못했습니다.");
        Unloaded += (_, _) =>
        {
            Interlocked.Exchange(ref _refreshCancellation, null)?.Cancel();
            Interlocked.Exchange(ref _searchDebounceCancellation, null)?.Cancel();
        };
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private async void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ClearSearchButton.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _searchDebounceCancellation, cancellation)?.Cancel();
        await ExecuteUiAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token);
            await RefreshAsync();
        }, "기억 목록을 불러오지 못했습니다.");
        Interlocked.CompareExchange(ref _searchDebounceCancellation, null, cancellation);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private async void MemorySortBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) =>
        await ExecuteUiAsync(() => RefreshAsync(), "기억 목록을 정렬하지 못했습니다.");

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            var editor = new MemoryEditorWindow(_memories) { Owner = OwnerWindow };
            if (editor.ShowDialog() == true) await RefreshAsync("기억을 추가했어요.");
        }, "기억 편집 화면을 열지 못했습니다.");
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            if (sender is not FrameworkElement { Tag: string id }) return;
            var memory = await _memories.GetForManagementAsync(id, CancellationToken.None);
            if (memory is null) return;
            var editor = new MemoryEditorWindow(_memories, memory) { Owner = OwnerWindow };
            if (editor.ShowDialog() == true) await RefreshAsync("기억을 수정했어요.");
        }, "기억을 수정하지 못했습니다.");
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            var memories = await LoadAllAsync(null);
            if (memories.Count == 0)
            {
                System.Windows.MessageBox.Show(OwnerWindow, "내보낼 기억이 없습니다.", "기억 내보내기");
                return;
            }
            var confirmation = System.Windows.MessageBox.Show(
                OwnerWindow,
                "기억 원문과 민감도 정보가 암호화되지 않은 JSON 파일에 저장됩니다. 계속할까요?",
                "기억 내보내기",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "LIGClaw 기억 내보내기",
                FileName = $"LIGClaw-memories-{DateTime.Now:yyyyMMdd}.json",
                DefaultExt = ".json",
                Filter = "JSON 파일 (*.json)|*.json",
                AddExtension = true,
            };
            if (dialog.ShowDialog(OwnerWindow) != true) return;
            var export = memories.Select(memory => new
            {
                memory.Kind,
                memory.Key,
                memory.Value,
                memory.Sensitivity,
                memory.Source,
                memory.CreatedAtUtc,
                memory.UpdatedAtUtc,
                memory.ExpiresAtUtc,
            });
            await File.WriteAllTextAsync(
                dialog.FileName,
                JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
            SetPageStatus("기억을 JSON 파일로 내보냈어요.", isError: false);
        }, "기억을 내보내지 못했습니다. 저장 위치와 권한을 확인해 주세요.");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteUiAsync(async () =>
        {
            if (sender is not FrameworkElement { Tag: string id }) return;
            var selected = await _memories.GetForManagementAsync(id, CancellationToken.None);
            if (selected is null)
            {
                await RefreshAsync();
                return;
            }
            var confirmation = System.Windows.MessageBox.Show(
                OwnerWindow,
                $"‘{selected.Key}’ 기억을 삭제할까요?\n삭제한 기억은 자동으로 복구되지 않습니다.",
                "기억 삭제",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;
            _ = await _memories.DeleteAsync(id, CancellationToken.None);
            await RefreshAsync("기억을 삭제했어요.");
        }, "기억을 삭제하지 못했습니다.");
    }

    internal async Task RefreshAsync(string? completionMessage = null)
    {
        if (!IsLoaded) return;
        var version = Interlocked.Increment(ref _refreshVersion);
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _refreshCancellation, cancellation)?.Cancel();
        var query = string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim();
        SetPageStatus("기억을 불러오고 있어요…", isError: false);
        IReadOnlyList<PersonalMemory> memories;
        try
        {
            memories = await LoadAllAsync(query, cancellation.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _refreshCancellation, null, cancellation);
        }
        cancellation.Token.ThrowIfCancellationRequested();
        if (version != Volatile.Read(ref _refreshVersion)) return;
        var sorted = (MemorySortBox.SelectedValue as string ?? "updated") switch
        {
            "oldest" => memories.OrderBy(memory => memory.UpdatedAtUtc),
            "name" => memories.OrderBy(memory => memory.Key, StringComparer.CurrentCultureIgnoreCase),
            _ => memories.OrderByDescending(memory => memory.UpdatedAtUtc),
        };
        var items = sorted.Select(MemoryListItem.Create).ToArray();
        MemoryList.ItemsSource = items;
        EmptyMessage.Text = query is null ? "저장된 기억이 없습니다." : "검색과 일치하는 기억이 없습니다.";
        EmptyMessage.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetPageStatus(completionMessage ?? $"기억 {items.Length}개", isError: false);
    }

    private async Task<IReadOnlyList<PersonalMemory>> LoadAllAsync(
        string? query,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 500;
        if (!string.IsNullOrWhiteSpace(query))
            return await _memories.ListAsync(query, pageSize, DateTimeOffset.UtcNow, cancellationToken);
        var results = new List<PersonalMemory>();
        while (true)
        {
            var page = await _memories.ListForManagementAsync(query, results.Count, pageSize, cancellationToken);
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

    private sealed record MemoryListItem(string Id, string Key, string Value, string Metadata)
    {
        public static MemoryListItem Create(PersonalMemory memory)
        {
            var expiry = memory.ExpiresAtUtc is null
                ? "만료 없음"
                : memory.ExpiresAtUtc <= DateTimeOffset.UtcNow
                    ? $"{memory.ExpiresAtUtc.Value.ToLocalTime().ToString("yyyy.MM.dd", CultureInfo.CurrentCulture)} 만료됨"
                    : $"{memory.ExpiresAtUtc.Value.ToLocalTime().ToString("yyyy.MM.dd", CultureInfo.CurrentCulture)} 만료";
            return new MemoryListItem(
                memory.Id,
                memory.Key,
                memory.Value,
                $"{memory.Kind} · {memory.Sensitivity} · 출처 {memory.Source} · {expiry}");
        }
    }

    private sealed record SortChoice(string Label, string Value);
}
