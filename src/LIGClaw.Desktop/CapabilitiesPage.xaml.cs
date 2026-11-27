using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Domain;

namespace LIGClaw.Desktop;

public partial class CapabilitiesPage : System.Windows.Controls.UserControl
{
    private readonly ICapabilityManagementService _management;
    private readonly ConnectionManagementService? _connectionManagement;
    private readonly IConnectionProfileRepository? _connectionProfiles;
    private readonly MailGatewayConnectionManagementService? _mailGatewayConnectionManagement;
    private readonly IMailGatewayConnectionRepository? _mailGatewayConnectionProfiles;
    private readonly SmtpConnectionManagementService? _smtpConnectionManagement;
    private readonly ISmtpConnectionRepository? _smtpConnectionProfiles;
    private readonly ICapabilityConnectionReadinessResolver? _connectionReadiness;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _operationCancellation;
    private int _refreshVersion;
    private bool _busy;

    internal CapabilitiesPage(
        ICapabilityManagementService management,
        ConnectionManagementService? connectionManagement = null,
        IConnectionProfileRepository? connectionProfiles = null,
        ICapabilityConnectionReadinessResolver? connectionReadiness = null,
        MailGatewayConnectionManagementService? mailGatewayConnectionManagement = null,
        IMailGatewayConnectionRepository? mailGatewayConnectionProfiles = null,
        SmtpConnectionManagementService? smtpConnectionManagement = null,
        ISmtpConnectionRepository? smtpConnectionProfiles = null)
    {
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _connectionManagement = connectionManagement;
        _connectionProfiles = connectionProfiles;
        _mailGatewayConnectionManagement = mailGatewayConnectionManagement;
        _mailGatewayConnectionProfiles = mailGatewayConnectionProfiles;
        if ((smtpConnectionManagement is null) != (smtpConnectionProfiles is null))
            throw new ArgumentException("메일 서버 기능 연결에 필요한 구성 요소를 함께 제공해야 합니다.");
        _smtpConnectionManagement = smtpConnectionManagement;
        _smtpConnectionProfiles = smtpConnectionProfiles;
        _connectionReadiness = connectionReadiness;
        InitializeComponent();
        CapabilityFilterBox.ItemsSource = new[]
        {
            new FilterChoice("모든 상태", CapabilityManagementFilter.All),
            new FilterChoice("검토 필요", CapabilityManagementFilter.ReadyForReview),
            new FilterChoice("활성", CapabilityManagementFilter.Active),
            new FilterChoice("비활성", CapabilityManagementFilter.Inactive),
        };
        CapabilitySortBox.ItemsSource = new[]
        {
            new SortChoice("최근 가져온 순", CapabilityManagementSort.RecentlyUpdated),
            new SortChoice("이름순", CapabilityManagementSort.Name),
        };
        CapabilityFilterBox.SelectedValue = CapabilityManagementFilter.All;
        CapabilitySortBox.SelectedValue = CapabilityManagementSort.RecentlyUpdated;
        Loaded += async (_, _) => await ExecuteUiAsync(
            () => RefreshAsync(),
            "기능 목록을 불러오지 못했습니다.");
        Unloaded += (_, _) =>
        {
            Interlocked.Exchange(ref _refreshCancellation, null)?.Cancel();
            Interlocked.Exchange(ref _operationCancellation, null)?.Cancel();
        };
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    internal event EventHandler? BuilderRequested;

    private void Builder_Click(object sender, RoutedEventArgs e) =>
        BuilderRequested?.Invoke(this, EventArgs.Empty);

    private async void CapabilityFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await ExecuteUiAsync(() => RefreshAsync(), "기능 목록을 필터링하지 못했습니다.");

    private async void CapabilitySortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await ExecuteUiAsync(() => RefreshAsync(), "기능 목록을 정렬하지 못했습니다.");

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiAsync(() => RefreshAsync(), "기능 목록을 새로 고치지 못했습니다.");

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "LIGClaw 기능 파일 가져오기",
            DefaultExt = ".ligcap",
            Filter = "LIGClaw 기능 파일 (*.ligcap)|*.ligcap",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(OwnerWindow) == true) await ImportPackageAsync(dialog.FileName);
    }

    internal async Task ImportPackageAsync(string packagePath)
    {
        await RunOperationAsync(async cancellationToken =>
        {
            SetPageStatus("기능 파일을 검사하고 있어요…", isError: false);
            var result = await _management.ImportPackageAsync(
                packagePath,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (result.Outcome is CapabilityManagementOutcome.Succeeded or CapabilityManagementOutcome.Duplicate &&
                result.Draft is not null)
            {
                await RefreshAsync(
                    result.Outcome == CapabilityManagementOutcome.Succeeded
                        ? "기능을 가져왔습니다. 켜기 전에 내용을 검토하세요."
                        : "같은 기능 버전이 이미 있습니다.",
                    result.Draft.ExactVersion);
                return;
            }
            SetPageStatus(DescribeFailure(result.Outcome, result.ErrorCode), isError: true);
        });
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CapabilityListItem item } || _busy) return;
        await RunOperationAsync(async cancellationToken =>
        {
            SetPageStatus("기능 검토 정보를 불러오고 있어요…", isError: false);
            var result = await _management.GetActivationReviewAsync(
                item.Source.DraftId,
                item.Source.Summary.LatestVersion,
                item.Source.Summary.CatalogRevision,
                cancellationToken);
            if (result.Outcome != CapabilityManagementOutcome.Succeeded || result.Review is null)
            {
                SetPageStatus(DescribeFailure(result.Outcome, result.ErrorCode), isError: true);
                return;
            }
            SetBusy(false);
            var reviewWindow = CreateReviewWindow(result.Review);
            reviewWindow.Owner = OwnerWindow;
            var changed = reviewWindow.ShowDialog() == true;
            SetBusy(true);
            if (changed)
                await RefreshAsync("기능 버전을 활성화했습니다.", result.Review.Draft.ExactVersion);
            else
                SetPageStatus("검토를 마쳤습니다.", isError: false);
        });
    }

    private async void RollbackReview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CapabilityListItem item } ||
            item.Source.Summary.ActiveVersion is not { } active || _busy) return;
        await RunOperationAsync(async cancellationToken =>
        {
            SetPageStatus("이전 활성 버전을 확인하고 있어요…", isError: false);
            var result = await _management.GetRollbackReviewAsync(
                item.Source.Summary.Id,
                active,
                item.Source.Summary.CatalogRevision,
                cancellationToken);
            if (result.Outcome != CapabilityManagementOutcome.Succeeded || result.Review is null)
            {
                SetPageStatus(DescribeFailure(result.Outcome, result.ErrorCode), isError: true);
                return;
            }
            SetBusy(false);
            var reviewWindow = CreateReviewWindow(result.Review);
            reviewWindow.Owner = OwnerWindow;
            var changed = reviewWindow.ShowDialog() == true;
            SetBusy(true);
            if (changed)
                await RefreshAsync("이전 기능 버전으로 되돌렸습니다.", result.Review.TargetVersion.ExactVersion);
            else
                SetPageStatus("롤백 검토를 마쳤습니다.", isError: false);
        });
    }

    private CapabilityReviewWindow CreateReviewWindow(CapabilityActivationReview review) =>
        HasConnectionBindingServices
            ? new CapabilityReviewWindow(
                _management,
                review,
                _connectionManagement,
                _connectionProfiles,
                _connectionReadiness!,
                _mailGatewayConnectionManagement,
                _mailGatewayConnectionProfiles,
                _smtpConnectionManagement,
                _smtpConnectionProfiles)
            : new CapabilityReviewWindow(_management, review);

    private CapabilityReviewWindow CreateReviewWindow(CapabilityRollbackReview review) =>
        HasConnectionBindingServices
            ? new CapabilityReviewWindow(
                _management,
                review,
                _connectionManagement,
                _connectionProfiles,
                _connectionReadiness!,
                _mailGatewayConnectionManagement,
                _mailGatewayConnectionProfiles,
                _smtpConnectionManagement,
                _smtpConnectionProfiles)
            : new CapabilityReviewWindow(_management, review);

    private bool HasConnectionBindingServices =>
        _connectionReadiness is not null &&
        (_connectionManagement is not null && _connectionProfiles is not null ||
         _mailGatewayConnectionManagement is not null && _mailGatewayConnectionProfiles is not null ||
         _smtpConnectionManagement is not null && _smtpConnectionProfiles is not null);

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CapabilityListItem item } ||
            item.Source.Summary.ActiveVersion is not { } active || _busy) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "활성 기능 버전 내보내기",
            FileName = $"{active.Id.Value}-{active.Version.Value}.ligcap",
            DefaultExt = ".ligcap",
            Filter = "LIGClaw 기능 파일 (*.ligcap)|*.ligcap",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        await ExportVersionAsync(active, dialog.FileName);
    }

    internal async Task ExportVersionAsync(CapabilityVersionRef exactVersion, string destinationPath)
    {
        await RunOperationAsync(async cancellationToken =>
        {
            SetPageStatus("활성 기능 버전을 내보내고 있어요…", isError: false);
            var result = await _management.ExportVersionAsync(exactVersion, destinationPath, cancellationToken);
            SetPageStatus(
                result.Outcome == CapabilityManagementOutcome.Succeeded
                    ? "활성 기능 버전을 내보냈습니다."
                    : DescribeFailure(result.Outcome, result.ErrorCode),
                result.Outcome != CapabilityManagementOutcome.Succeeded);
        });
    }

    internal async Task RefreshAsync(
        string? completionMessage = null,
        CapabilityVersionRef? focusVersion = null)
    {
        if (!IsLoaded) return;
        var version = Interlocked.Increment(ref _refreshVersion);
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _refreshCancellation, cancellation)?.Cancel();
        var filter = CapabilityFilterBox.SelectedValue is CapabilityManagementFilter selectedFilter
            ? selectedFilter
            : CapabilityManagementFilter.All;
        var sort = CapabilitySortBox.SelectedValue is CapabilityManagementSort selectedSort
            ? selectedSort
            : CapabilityManagementSort.RecentlyUpdated;
        SetPageStatus("기능 목록을 불러오고 있어요…", isError: false);
        var capabilities = await _management.ListAsync(filter, sort, cancellation.Token);
        Interlocked.CompareExchange(ref _refreshCancellation, null, cancellation);
        cancellation.Token.ThrowIfCancellationRequested();
        if (version != Volatile.Read(ref _refreshVersion) ||
            filter != (CapabilityFilterBox.SelectedValue as CapabilityManagementFilter? ?? CapabilityManagementFilter.All) ||
            sort != (CapabilitySortBox.SelectedValue as CapabilityManagementSort? ?? CapabilityManagementSort.RecentlyUpdated))
            return;

        var items = capabilities.Select(CapabilityListItem.Create).ToArray();
        CapabilityList.ItemsSource = items;
        EmptyMessage.Text = items.Length == 0 && filter != CapabilityManagementFilter.All
            ? "선택한 상태에 해당하는 기능이 없습니다."
            : "아직 기능 카드가 없습니다. ‘새 기능 만들기’에서 원하는 일을 설명해 보세요.";
        EmptyMessage.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetPageStatus(completionMessage ?? $"기능 {items.Length}개", isError: false);
        if (focusVersion is not null)
        {
            var target = items.FirstOrDefault(item => item.Source.Summary.LatestVersion.Equals(focusVersion) ||
                                                     Equals(item.Source.Summary.ActiveVersion, focusVersion));
            if (target is not null)
            {
                CapabilityList.SelectedItem = target;
                CapabilityList.ScrollIntoView(target);
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (CapabilityList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem container)
                        container.Focus();
                    else
                        CapabilityList.Focus();
                }, DispatcherPriority.Input);
            }
        }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _operationCancellation, cancellation)?.Cancel();
        SetBusy(true);
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            SetPageStatus("기능 작업을 완료하지 못했습니다. 다시 시도하세요.", isError: true);
        }
        finally
        {
            Interlocked.CompareExchange(ref _operationCancellation, null, cancellation);
            SetBusy(false);
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
        }
        catch
        {
            SetPageStatus(failureMessage, isError: true);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BuilderButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        CapabilityFilterBox.IsEnabled = !busy;
        CapabilitySortBox.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        CapabilityList.IsEnabled = !busy;
    }

    private void SetPageStatus(string message, bool isError)
    {
        PageStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "TextSecondaryBrush");
        PageStatusText.Text = message;
    }

    private static string DescribeFailure(CapabilityManagementOutcome outcome, string? errorCode) => outcome switch
    {
        CapabilityManagementOutcome.Duplicate => "같은 기능 버전이 이미 있습니다.",
        CapabilityManagementOutcome.Stale => "목록을 연 뒤 기능 상태가 바뀌었습니다. 새로 검토하세요.",
        CapabilityManagementOutcome.NotFound when errorCode == "capability.rollback.target_unavailable" =>
            "롤백할 이전 활성 버전이 없습니다.",
        CapabilityManagementOutcome.NotFound => "기능 버전을 찾지 못했습니다. 목록을 새로 불러오세요.",
        CapabilityManagementOutcome.NotReady => "이 기능 버전은 아직 활성화할 준비가 되지 않았습니다.",
        CapabilityManagementOutcome.Unsupported => "현재 지원하지 않는 기능 파일입니다.",
        CapabilityManagementOutcome.IoFailure => "파일을 안전하게 읽거나 쓰지 못했습니다. 위치와 권한을 확인하세요.",
        _ => "기능 파일 또는 요청이 올바르지 않습니다.",
    };

    private static string FormatState(CapabilityManagementItem item) => item.Summary.LatestState switch
    {
        CapabilityDraftState.ReadyForReview => "검토 필요",
        CapabilityDraftState.Applied when Equals(item.Summary.ActiveVersion, item.Summary.LatestVersion) => "활성",
        CapabilityDraftState.Applied => "적용 이력",
        CapabilityDraftState.Stale => "변경됨: 다시 가져오기 필요",
        CapabilityDraftState.Rejected => "거부됨",
        CapabilityDraftState.Validating => "검사 중",
        _ => "초안",
    };

    private sealed record FilterChoice(string Label, CapabilityManagementFilter Value);
    private sealed record SortChoice(string Label, CapabilityManagementSort Value);

    private sealed record CapabilityListItem(
        CapabilityManagementItem Source,
        string DisplayName,
        string Description,
        string IdentityText,
        string VersionText,
        string MetadataText,
        string StatusText,
        string AutomationName,
        Visibility ExportVisibility,
        Visibility RollbackVisibility)
    {
        public static CapabilityListItem Create(CapabilityManagementItem item)
        {
            var summary = item.Summary;
            var active = summary.ActiveVersion is null
                ? "활성 버전 없음"
                : $"활성 버전 {summary.ActiveVersion.Version.Value}";
            var hash = summary.LatestVersion.ContentHash.Value;
            var shortHash = hash.Length > 19 ? hash[..19] : hash;
            return new(
                item,
                summary.DisplayName,
                summary.Description,
                $"기능 식별자 {summary.Id.Value} · {UserFacingText.CapabilityType(summary.Kind)}",
                $"최신 버전 {summary.LatestVersion.Version.Value} · {active}",
                $"{UserFacingText.Risk(summary.MaximumRisk)} · {UserFacingText.SourceKind(summary.SourceKind)} · 무결성 {shortHash} · " +
                summary.UpdatedAtUtc.ToLocalTime().ToString("yyyy.MM.dd HH:mm", CultureInfo.CurrentCulture),
                FormatState(item),
                $"{summary.DisplayName} 기능 카드, {FormatState(item)}",
                item.CanExport ? Visibility.Visible : Visibility.Collapsed,
                item.CanRollback ? Visibility.Visible : Visibility.Collapsed);
        }
    }
}
