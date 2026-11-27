using System.Windows;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Domain;

namespace LIGClaw.Desktop;

public partial class CapabilityReviewWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ICapabilityManagementService _management;
    private readonly ConnectionManagementService? _connectionManagement;
    private readonly IConnectionProfileRepository? _connectionProfiles;
    private readonly MailGatewayConnectionManagementService? _mailGatewayConnectionManagement;
    private readonly IMailGatewayConnectionRepository? _mailGatewayConnectionProfiles;
    private readonly SmtpConnectionManagementService? _smtpConnectionManagement;
    private readonly ISmtpConnectionRepository? _smtpConnectionProfiles;
    private readonly ICapabilityConnectionReadinessResolver? _connectionReadiness;
    private CapabilityActivationReview? _activationReview;
    private CapabilityRollbackReview? _rollbackReview;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _bindingLoadCancellation;
    private IReadOnlyList<CapabilityConnectionRequirement> _connectionRequirements = [];
    private CapabilityVersionRef? _connectionExactVersion;
    private string? _bindingStateFingerprint;
    private bool _busy;
    private bool _reviewStale;
    private bool _baseCanMutate;
    private bool _connectionsReady = true;
    private bool _resolvedConnectionsReady = true;
    private int _bindingRefreshVersion;

    private CapabilityReviewWindow(
        ICapabilityManagementService management,
        ConnectionManagementService? connectionManagement,
        IConnectionProfileRepository? connectionProfiles,
        MailGatewayConnectionManagementService? mailGatewayConnectionManagement,
        IMailGatewayConnectionRepository? mailGatewayConnectionProfiles,
        SmtpConnectionManagementService? smtpConnectionManagement,
        ISmtpConnectionRepository? smtpConnectionProfiles,
        ICapabilityConnectionReadinessResolver? connectionReadiness)
    {
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _connectionManagement = connectionManagement;
        _connectionProfiles = connectionProfiles;
        _mailGatewayConnectionManagement = mailGatewayConnectionManagement;
        _mailGatewayConnectionProfiles = mailGatewayConnectionProfiles;
        if ((smtpConnectionManagement is null) != (smtpConnectionProfiles is null))
            throw new ArgumentException("SMTP Capability binding 종속성은 함께 제공해야 합니다.");
        _smtpConnectionManagement = smtpConnectionManagement;
        _smtpConnectionProfiles = smtpConnectionProfiles;
        _connectionReadiness = connectionReadiness;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_connectionRequirements.Count == 0) return;
            try
            {
                await RefreshConnectionBindingsAsync();
            }
            catch (OperationCanceledException)
            {
            }
        };
        Closed += (_, _) =>
        {
            Interlocked.Exchange(ref _operationCancellation, null)?.Cancel();
            Interlocked.Exchange(ref _bindingLoadCancellation, null)?.Cancel();
        };
    }

    internal CapabilityReviewWindow(
        ICapabilityManagementService management,
        CapabilityActivationReview review) : this(management, null, null, null, null, null, null, null)
    {
        _activationReview = review;
        ConfigureActivationReview(review);
    }

    internal CapabilityReviewWindow(
        ICapabilityManagementService management,
        CapabilityActivationReview review,
        ConnectionManagementService connectionManagement,
        IConnectionProfileRepository connectionProfiles,
        ICapabilityConnectionReadinessResolver connectionReadiness) : this(
        management,
        connectionManagement,
        connectionProfiles,
        null,
        null,
        null,
        null,
        connectionReadiness)
    {
        _activationReview = review;
        ConfigureActivationReview(review);
    }

    internal CapabilityReviewWindow(
        ICapabilityManagementService management,
        CapabilityActivationReview review,
        ConnectionManagementService? connectionManagement,
        IConnectionProfileRepository? connectionProfiles,
        ICapabilityConnectionReadinessResolver connectionReadiness,
        MailGatewayConnectionManagementService? mailGatewayConnectionManagement,
        IMailGatewayConnectionRepository? mailGatewayConnectionProfiles,
        SmtpConnectionManagementService? smtpConnectionManagement = null,
        ISmtpConnectionRepository? smtpConnectionProfiles = null) : this(
        management,
        connectionManagement,
        connectionProfiles,
        mailGatewayConnectionManagement,
        mailGatewayConnectionProfiles,
        smtpConnectionManagement,
        smtpConnectionProfiles,
        connectionReadiness)
    {
        _activationReview = review;
        ConfigureActivationReview(review);
    }

    internal CapabilityReviewWindow(
        ICapabilityManagementService management,
        CapabilityRollbackReview review) : this(management, null, null, null, null, null, null, null)
    {
        _rollbackReview = review;
        ConfigureRollbackReview(review);
    }

    internal CapabilityReviewWindow(
        ICapabilityManagementService management,
        CapabilityRollbackReview review,
        ConnectionManagementService connectionManagement,
        IConnectionProfileRepository connectionProfiles,
        ICapabilityConnectionReadinessResolver connectionReadiness) : this(
        management,
        connectionManagement,
        connectionProfiles,
        null,
        null,
        null,
        null,
        connectionReadiness)
    {
        _rollbackReview = review;
        ConfigureRollbackReview(review);
    }

    internal CapabilityReviewWindow(
        ICapabilityManagementService management,
        CapabilityRollbackReview review,
        ConnectionManagementService? connectionManagement,
        IConnectionProfileRepository? connectionProfiles,
        ICapabilityConnectionReadinessResolver connectionReadiness,
        MailGatewayConnectionManagementService? mailGatewayConnectionManagement,
        IMailGatewayConnectionRepository? mailGatewayConnectionProfiles,
        SmtpConnectionManagementService? smtpConnectionManagement = null,
        ISmtpConnectionRepository? smtpConnectionProfiles = null) : this(
        management,
        connectionManagement,
        connectionProfiles,
        mailGatewayConnectionManagement,
        mailGatewayConnectionProfiles,
        smtpConnectionManagement,
        smtpConnectionProfiles,
        connectionReadiness)
    {
        _rollbackReview = review;
        ConfigureRollbackReview(review);
    }

    private void ConfigureActivationReview(CapabilityActivationReview review)
    {
        var draft = review.Draft;
        var manifest = draft.Payload.Manifest;
        ModeEyebrow.Text = "기능 만들기 / 적용 전 검토";
        ReviewTitle.Text = manifest.DisplayName;
        ReviewSubtitle.Text = "가져온 내용과 정확한 버전을 확인한 뒤 적용하세요. 기능 적용은 작업 실행 승인을 대신하지 않습니다.";
        IdentityText.Text = manifest.Id.Value;
        VersionText.Text = $"버전 {manifest.Version.Value} · {FormatState(draft.State)}";
        RevisionText.Text = $"초안 변경 번호 {draft.Revision} · 목록 변경 번호 {review.Catalog.CatalogRevision}";
        ParentText.Text = FormatVersion(draft.Parent);
        RiskText.Text = UserFacingText.Risk(manifest.MaximumRisk);
        ToolsText.Text = manifest.RequiredTools.Count == 0
            ? "추가 작업 도구 없음"
            : string.Join(", ", manifest.RequiredTools.Select(UserFacingText.Tool));
        SourceText.Text = $"{UserFacingText.SourceKind(draft.SourceKind)} · {draft.ExactVersion.ContentHash.Value}";
        DescriptionText.Text = manifest.Description;
        InstructionsTextBox.Text = draft.Payload.Instructions;
        _baseCanMutate = review.CanActivate;
        ActivateButton.Visibility = review.CanActivate ? Visibility.Visible : Visibility.Collapsed;
        ReviewStatusText.Text = review.CanActivate
            ? "검토가 끝나면 이 버전을 적용할 수 있습니다."
            : "이 버전은 현재 상태에서 적용할 수 없습니다.";
        ConfigureConnectionRequirements(draft.ExactVersion, manifest.RequiredConnections);
    }

    private void ConfigureRollbackReview(CapabilityRollbackReview review)
    {
        var target = review.TargetVersion;
        var manifest = target.Payload.Manifest;
        ModeEyebrow.Text = "기능 만들기 / 이전 버전 검토";
        ReviewTitle.Text = manifest.DisplayName;
        ReviewSubtitle.Text = "현재 적용된 기능을 이전에 사용했던 정확한 버전으로 되돌립니다. 현재 내용은 이력에 그대로 남습니다.";
        IdentityText.Text = manifest.Id.Value;
        VersionText.Text = $"버전 {review.CurrentVersion.ExactVersion.Version.Value} → 버전 {target.ExactVersion.Version.Value}";
        RevisionText.Text = $"목록 변경 번호 {review.Catalog.CatalogRevision}";
        ParentText.Text = FormatVersion(target.Parent);
        RiskText.Text = UserFacingText.Risk(manifest.MaximumRisk);
        ToolsText.Text = manifest.RequiredTools.Count == 0
            ? "추가 작업 도구 없음"
            : string.Join(", ", manifest.RequiredTools.Select(UserFacingText.Tool));
        SourceText.Text = $"{UserFacingText.SourceKind(target.SourceKind)} · {target.ExactVersion.ContentHash.Value}";
        DescriptionText.Text = manifest.Description;
        InstructionsTextBox.Text = target.Payload.Instructions;
        _baseCanMutate = true;
        RollbackButton.Visibility = Visibility.Visible;
        ReviewStatusText.Text = "되돌릴 정확한 버전과 내용을 확인해 주세요.";
        ConfigureConnectionRequirements(target.ExactVersion, manifest.RequiredConnections);
    }

    private void ConfigureConnectionRequirements(
        CapabilityVersionRef exactVersion,
        IReadOnlyList<CapabilityConnectionRequirement> requirements)
    {
        _connectionExactVersion = exactVersion;
        _connectionRequirements = requirements.ToArray();
        if (_connectionRequirements.Count == 0)
        {
            _connectionsReady = true;
            _resolvedConnectionsReady = true;
            UpdateMutationButtonState();
            return;
        }

        _connectionsReady = false;
        _resolvedConnectionsReady = false;
        _bindingStateFingerprint = null;
        ConnectionRequirementLabel.Visibility = Visibility.Visible;
        ConnectionsText.Visibility = Visibility.Visible;
        ConnectionsText.Text = string.Join(", ", _connectionRequirements.Select(requirement =>
            $"{requirement.Slot} ({requirement.Kind})"));
        ConnectionBindingSection.Visibility = Visibility.Visible;
        ConnectionBindingsList.ItemsSource = _connectionRequirements.Select(requirement =>
            CapabilityConnectionBindingRow.Placeholder(requirement)).ToArray();
        SetConnectionStatus("이 PC의 연결 선택과 실제 연결 상태를 확인하고 있어요…", isError: false);
        SetStatus("필요한 외부 연결이 모두 준비되면 이 버전을 적용할 수 있습니다.", isError: false);
        UpdateMutationButtonState();
    }

    private async void RefreshConnectionBindings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            await RefreshConnectionBindingsAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task RefreshConnectionBindingsAsync()
    {
        if (_connectionRequirements.Count == 0) return;
        var requiresHttp = _connectionRequirements.Any(requirement =>
            StringComparer.Ordinal.Equals(requirement.Kind, CapabilityConnectionKindPolicy.Http));
        var requiresMailGateway = _connectionRequirements.Any(requirement =>
            StringComparer.Ordinal.Equals(requirement.Kind, CapabilityConnectionKindPolicy.MailApi));
        var requiresSmtp = _connectionRequirements.Any(requirement =>
            StringComparer.Ordinal.Equals(requirement.Kind, CapabilityConnectionKindPolicy.Smtp));
        if (_connectionExactVersion is null || _connectionReadiness is null ||
            requiresHttp && _connectionProfiles is null ||
            requiresMailGateway && _mailGatewayConnectionProfiles is null ||
            requiresSmtp && _smtpConnectionProfiles is null)
        {
            FailConnectionReadiness("연결 선택 기능을 사용할 수 없습니다.");
            return;
        }

        var version = Interlocked.Increment(ref _bindingRefreshVersion);
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _bindingLoadCancellation, cancellation)?.Cancel();
        RefreshConnectionBindingsButton.IsEnabled = false;
        SetConnectionStatus("연결 선택과 준비 상태를 다시 확인하고 있어요…", isError: false);
        try
        {
            var httpProfilesTask = requiresHttp
                ? _connectionProfiles!.ListProfilesAsync(cancellation.Token)
                : Task.FromResult<IReadOnlyList<HttpConnectionProfile>>([]);
            var httpBindingsTask = requiresHttp
                ? _connectionProfiles!.GetBindingsAsync(_connectionExactVersion, cancellation.Token)
                : Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>([]);
            var mailGatewayProfilesTask = requiresMailGateway
                ? _mailGatewayConnectionProfiles!.ListProfilesAsync(cancellation.Token)
                : Task.FromResult<IReadOnlyList<MailGatewayConnectionProfile>>([]);
            var mailGatewayBindingsTask = requiresMailGateway
                ? _mailGatewayConnectionProfiles!.GetBindingsAsync(_connectionExactVersion, cancellation.Token)
                : Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>([]);
            var smtpProfilesTask = requiresSmtp
                ? _smtpConnectionProfiles!.ListProfilesAsync(cancellation.Token)
                : Task.FromResult<IReadOnlyList<SmtpConnectionProfile>>([]);
            var smtpBindingsTask = requiresSmtp
                ? _smtpConnectionProfiles!.GetBindingsAsync(_connectionExactVersion, cancellation.Token)
                : Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>([]);
            await Task.WhenAll(
                httpProfilesTask,
                httpBindingsTask,
                mailGatewayProfilesTask,
                mailGatewayBindingsTask,
                smtpProfilesTask,
                smtpBindingsTask);
            var resolution = await _connectionReadiness.ResolveAsync(
                _connectionExactVersion,
                _connectionRequirements,
                cancellation.Token);

            cancellation.Token.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref _bindingRefreshVersion)) return;

            var httpProfileChoices = (await httpProfilesTask)
                .Where(profile => profile.State == ConnectionProfileLifecycleState.Stable &&
                                  HttpConnectionProfilePolicy.IsValid(profile))
                .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(profile => profile.Id.Value, StringComparer.Ordinal)
                .Select(LocalConnectionProfileChoice.Create)
                .ToArray();
            var mailGatewayProfileChoices = (await mailGatewayProfilesTask)
                .Where(profile => profile.State == ConnectionProfileLifecycleState.Stable &&
                                  MailGatewayConnectionProfilePolicy.IsValid(profile))
                .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(profile => profile.Id.Value, StringComparer.Ordinal)
                .Select(LocalConnectionProfileChoice.Create)
                .ToArray();
            var smtpProfileChoices = (await smtpProfilesTask)
                .Where(profile => profile.State == ConnectionProfileLifecycleState.Stable &&
                                  SmtpConnectionProfilePolicy.IsValid(profile))
                .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(profile => profile.Id.Value, StringComparer.Ordinal)
                .Select(LocalConnectionProfileChoice.Create)
                .ToArray();
            var httpBindingBySlot = (await httpBindingsTask)
                .Where(binding => binding.ExactVersion.Equals(_connectionExactVersion) &&
                                  StringComparer.Ordinal.Equals(
                                      binding.Kind,
                                      CapabilityConnectionKindPolicy.Http))
                .GroupBy(binding => binding.Slot, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var mailGatewayBindingBySlot = (await mailGatewayBindingsTask)
                .Where(binding => binding.ExactVersion.Equals(_connectionExactVersion) &&
                                  StringComparer.Ordinal.Equals(
                                      binding.Kind,
                                      CapabilityConnectionKindPolicy.MailApi))
                .GroupBy(binding => binding.Slot, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var smtpBindingBySlot = (await smtpBindingsTask)
                .Where(binding => binding.ExactVersion.Equals(_connectionExactVersion) &&
                                  StringComparer.Ordinal.Equals(
                                      binding.Kind,
                                      CapabilityConnectionKindPolicy.Smtp))
                .GroupBy(binding => binding.Slot, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var resolutionBySlot = resolution.Bindings
                .Where(binding => binding.ExactVersion.Equals(_connectionExactVersion))
                .GroupBy(binding => binding.Slot, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

            var rows = _connectionRequirements.Select(requirement =>
            {
                var profileChoices = requirement.Kind switch
                {
                    CapabilityConnectionKindPolicy.Http => httpProfileChoices,
                    CapabilityConnectionKindPolicy.MailApi => mailGatewayProfileChoices,
                    CapabilityConnectionKindPolicy.Smtp => smtpProfileChoices,
                    _ => [],
                };
                var bindingBySlot = requirement.Kind switch
                {
                    CapabilityConnectionKindPolicy.Http => httpBindingBySlot,
                    CapabilityConnectionKindPolicy.MailApi => mailGatewayBindingBySlot,
                    CapabilityConnectionKindPolicy.Smtp => smtpBindingBySlot,
                    _ => null,
                };
                CapabilityConnectionBinding? binding = null;
                bindingBySlot?.TryGetValue(requirement.Slot, out binding);
                resolutionBySlot.TryGetValue(requirement.Slot, out var resolved);
                var selected = binding is null
                    ? null
                    : profileChoices.FirstOrDefault(choice => choice.ProfileId.Equals(binding.ProfileId));
                return CapabilityConnectionBindingRow.Create(
                    requirement,
                    profileChoices,
                    selected,
                    binding?.Revision ?? 0,
                    resolved,
                    binding);
            }).ToArray();
            ConnectionBindingsList.ItemsSource = rows;
            var loadedFingerprint = string.Join("\n", rows
                .OrderBy(row => row.Slot, StringComparer.Ordinal)
                .Select(row =>
                    $"{row.Kind}|{row.Slot}|{row.BindingRevision}|{row.BoundProfileId?.Value}|{row.BindingHash}"));

            var resolutionShapeValid = resolution.Bindings.Count == _connectionRequirements.Count &&
                                       resolutionBySlot.Count == _connectionRequirements.Count &&
                                       IsSha256(resolution.AggregateHash) &&
                                       _connectionRequirements.All(requirement =>
                                           resolutionBySlot.TryGetValue(requirement.Slot, out var item) &&
                                           StringComparer.Ordinal.Equals(item.Kind, requirement.Kind) &&
                                           item.IsReady && item.ProfileId is not null) &&
                                       rows.All(row => row.BindingRevision > 0 &&
                                                           row.SelectedProfile is not null &&
                                                           resolutionBySlot.TryGetValue(row.Slot, out var item) &&
                                                           item.ProfileId?.Equals(row.SelectedProfile.ProfileId) == true &&
                                                           row.SelectionMatchesBinding);
            _resolvedConnectionsReady = resolution.IsReady && resolutionShapeValid;
            _connectionsReady = _resolvedConnectionsReady;
            _bindingStateFingerprint = loadedFingerprint;
            SetConnectionStatus(
                _connectionsReady
                    ? "필요한 외부 연결의 설정, 실제 연결, 로그인 정보가 모두 준비됐습니다."
                    : "준비되지 않은 연결 항목이 있습니다. 연결 설정을 선택·저장하고 연결 설정 화면에서 실제 연결을 확인하세요.",
                isError: !_connectionsReady);
            SetStatus(
                _connectionsReady
                    ? "외부 연결 준비 상태를 확인했습니다. 이 버전을 적용할 수 있습니다."
                    : "필요한 외부 연결이 모두 준비되면 이 버전을 적용할 수 있습니다.",
                isError: !_connectionsReady);
            UpdateMutationButtonState();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            FailConnectionReadiness("연결 선택 또는 준비 상태를 확인하지 못했습니다.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _bindingLoadCancellation, null, cancellation);
            if (version == Volatile.Read(ref _bindingRefreshVersion))
                RefreshConnectionBindingsButton.IsEnabled = !_busy;
        }
    }

    private async void SaveConnectionBinding_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: CapabilityConnectionBindingRow row }) return;
        await SaveConnectionBindingAsync(row);
    }

    private void ConnectionProfileSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_busy || sender is not System.Windows.Controls.ComboBox
            { DataContext: CapabilityConnectionBindingRow changedRow } selector)
            return;
        ApplyConnectionProfileSelection(changedRow, selector.SelectedItem as LocalConnectionProfileChoice);
    }

    internal void ApplyConnectionProfileSelection(
        CapabilityConnectionBindingRow changedRow,
        LocalConnectionProfileChoice? selectedProfile)
    {
        ArgumentNullException.ThrowIfNull(changedRow);
        if (_busy) return;
        changedRow.SelectedProfile = selectedProfile is null ||
                                     StringComparer.Ordinal.Equals(selectedProfile.Kind, changedRow.Kind)
            ? selectedProfile
            : null;
        var rows = ConnectionBindingsList.Items.Cast<CapabilityConnectionBindingRow>().ToArray();
        _connectionsReady = _resolvedConnectionsReady && rows.All(row => row.SelectionMatchesBinding);
        if (!_connectionsReady)
        {
            SetConnectionStatus(
                "저장되지 않은 연결 설정이 있거나 아직 준비되지 않았습니다. 각 연결 항목의 선택을 저장해 주세요.",
                isError: true);
            SetStatus("필요한 외부 연결이 모두 준비되면 이 버전을 적용할 수 있습니다.", isError: true);
        }
        else
        {
            SetConnectionStatus("필요한 외부 연결의 설정, 실제 연결, 로그인 정보가 모두 준비됐습니다.", isError: false);
            SetStatus("외부 연결 준비 상태를 확인했습니다. 이 버전을 적용할 수 있습니다.", isError: false);
        }
        UpdateMutationButtonState();
    }

    internal async Task SaveConnectionBindingAsync(CapabilityConnectionBindingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_busy || _connectionExactVersion is null) return;
        if (row.SelectedProfile is null)
        {
            SetConnectionStatus($"{row.Slot} 연결 항목에 사용할 설정을 선택해 주세요.", isError: true);
            return;
        }
        if (!StringComparer.Ordinal.Equals(row.Kind, row.SelectedProfile.Kind))
        {
            SetConnectionStatus($"{row.Slot} 연결 항목과 선택한 설정의 종류가 일치하지 않습니다.", isError: true);
            return;
        }
        if (row.Kind == CapabilityConnectionKindPolicy.Http && _connectionManagement is null ||
            row.Kind == CapabilityConnectionKindPolicy.MailApi && _mailGatewayConnectionManagement is null ||
            row.Kind == CapabilityConnectionKindPolicy.Smtp && _smtpConnectionManagement is null)
        {
            FailConnectionReadiness("연결 선택 기능을 사용할 수 없습니다.");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _operationCancellation, cancellation)?.Cancel();
        SetBusy(true);
        SetConnectionStatus($"{row.Slot} 연결 항목에 사용할 이 PC의 연결 설정을 저장하고 있어요…", isError: false);
        try
        {
            var outcome = row.Kind switch
            {
                CapabilityConnectionKindPolicy.Http => (await _connectionManagement!.BindAsync(
                    _connectionExactVersion,
                    row.Slot,
                    row.Kind,
                    row.SelectedProfile.ProfileId,
                    row.BindingRevision,
                    cancellation.Token)).Outcome,
                CapabilityConnectionKindPolicy.MailApi =>
                    (await _mailGatewayConnectionManagement!.BindAsync(
                        _connectionExactVersion,
                        row.Slot,
                        row.SelectedProfile.ProfileId,
                        row.BindingRevision,
                        cancellation.Token)).Outcome,
                CapabilityConnectionKindPolicy.Smtp =>
                    (await _smtpConnectionManagement!.BindAsync(
                        _connectionExactVersion,
                        row.Slot,
                        row.SelectedProfile.ProfileId,
                        row.BindingRevision,
                        cancellation.Token)).Outcome,
                _ => ConnectionManagementOutcome.Invalid,
            };
            if (outcome == ConnectionManagementOutcome.Succeeded)
            {
                await RefreshConnectionBindingsAsync();
                if (!_connectionsReady)
                    SetConnectionStatus(
                        "연결 선택을 저장했습니다. 연결 설정 화면에서 해당 설정의 실제 연결을 확인하세요.",
                        isError: false);
                return;
            }

            if (outcome == ConnectionManagementOutcome.Stale)
            {
                await RefreshConnectionBindingsAsync();
                SetConnectionStatus(
                    "다른 작업에서 연결 선택이 변경되어 최신 상태를 다시 불러왔습니다. 선택을 확인하고 다시 저장하세요.",
                    isError: true);
                return;
            }

            if (outcome is ConnectionManagementOutcome.NotFound or ConnectionManagementOutcome.NotReady)
                await RefreshConnectionBindingsAsync();
            SetConnectionStatus(DescribeBindingFailure(outcome), isError: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            FailConnectionReadiness("연결 선택을 저장하지 못했습니다.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _operationCancellation, null, cancellation);
            SetBusy(false);
        }
    }

    private void FailConnectionReadiness(string message)
    {
        _connectionsReady = false;
        _resolvedConnectionsReady = false;
        _bindingStateFingerprint = null;
        SetConnectionStatus(message, isError: true);
        SetStatus("외부 연결 준비 상태를 확인할 수 없어 이 버전을 적용할 수 없습니다.", isError: true);
        UpdateMutationButtonState();
    }

    private void SetConnectionStatus(string message, bool isError)
    {
        ConnectionBindingStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "TextSecondaryBrush");
        ConnectionBindingStatusText.Text = message;
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _activationReview is null || !_connectionsReady) return;
        if (!await RevalidateConnectionsBeforeMutationAsync()) return;
        var exact = _activationReview.Draft.ExactVersion;
        if (System.Windows.MessageBox.Show(
                this,
                $"v{exact.Version.Value}을 활성화할까요?\n해시: {exact.ContentHash.Value}\nCatalog revision: {_activationReview.Catalog.CatalogRevision}",
                "기능 적용",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!await RevalidateConnectionsBeforeMutationAsync()) return;
        await RunMutationAsync(
            cancellationToken => _management.ActivateAsync(
                _activationReview,
                DateTimeOffset.UtcNow,
                cancellationToken),
            "기능 버전을 적용하고 있어요…");
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _rollbackReview is null || !_connectionsReady) return;
        if (!await RevalidateConnectionsBeforeMutationAsync()) return;
        var target = _rollbackReview.TargetVersion.ExactVersion;
        if (System.Windows.MessageBox.Show(
                this,
                $"버전 {target.Version.Value}(으)로 되돌릴까요?\n무결성 값: {target.ContentHash.Value}\n목록 변경 번호: {_rollbackReview.Catalog.CatalogRevision}",
                "기능 이전 버전으로 되돌리기",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!await RevalidateConnectionsBeforeMutationAsync()) return;
        await RunMutationAsync(
            cancellationToken => _management.RollbackAsync(
                _rollbackReview,
                DateTimeOffset.UtcNow,
                cancellationToken),
            "기능을 이전 버전으로 되돌리고 있어요…");
    }

    internal async Task<bool> RevalidateConnectionsBeforeMutationAsync()
    {
        if (_connectionRequirements.Count == 0) return true;
        var reviewedFingerprint = _bindingStateFingerprint;
        SetBusy(true);
        try
        {
            await RefreshConnectionBindingsAsync();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            SetBusy(false);
        }
        if (!_connectionsReady) return false;
        if (reviewedFingerprint is null ||
            !StringComparer.Ordinal.Equals(reviewedFingerprint, _bindingStateFingerprint))
        {
            SetConnectionStatus(
                "검토 중 연결 선택이 변경되어 최신 상태를 다시 불러왔습니다. 변경된 연결 설정을 확인한 뒤 다시 적용하세요.",
                isError: true);
            SetStatus("연결 선택이 변경되었습니다. 이 PC의 최신 연결 선택을 다시 검토하세요.", isError: true);
            return false;
        }
        return true;
    }

    private async Task RunMutationAsync(
        Func<CancellationToken, Task<CapabilityManagementMutationResult>> operation,
        string progressMessage)
    {
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _operationCancellation, cancellation)?.Cancel();
        SetBusy(true);
        SetStatus(progressMessage, isError: false);
        try
        {
            var result = await operation(cancellation.Token);
            if (result.Outcome == CapabilityManagementOutcome.Succeeded)
            {
                DialogResult = true;
                return;
            }
            SetStatus(DescribeFailure(result.Outcome), isError: true);
            if (result.Outcome == CapabilityManagementOutcome.Stale)
                _reviewStale = true;
            if (result.Outcome == CapabilityManagementOutcome.NotReady &&
                _connectionRequirements.Count > 0 && IsConnectionError(result.ErrorCode))
            {
                _connectionsReady = false;
                await RefreshConnectionBindingsAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            SetStatus("기능 변경을 완료하지 못했습니다. 창을 닫고 다시 검토하세요.", isError: true);
        }
        finally
        {
            Interlocked.CompareExchange(ref _operationCancellation, null, cancellation);
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CloseButton.IsEnabled = !busy;
        ConnectionBindingsList.IsEnabled = !busy;
        RefreshConnectionBindingsButton.IsEnabled = !busy;
        UpdateMutationButtonState();
    }

    private void UpdateMutationButtonState()
    {
        var enabled = !_busy && !_reviewStale && _baseCanMutate && _connectionsReady;
        ActivateButton.IsEnabled = enabled;
        RollbackButton.IsEnabled = enabled;
    }

    private void SetStatus(string message, bool isError)
    {
        ReviewStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "TextSecondaryBrush");
        ReviewStatusText.Text = message;
    }

    private static string DescribeFailure(CapabilityManagementOutcome outcome) => outcome switch
    {
        CapabilityManagementOutcome.Stale => "검토를 시작한 뒤 상태가 바뀌었습니다. 창을 닫고 새로 검토하세요.",
        CapabilityManagementOutcome.NotReady => "이 버전은 현재 활성화할 준비가 되지 않았습니다.",
        CapabilityManagementOutcome.NotFound => "정확한 기능 버전을 찾지 못했습니다.",
        _ => "기능 변경 요청을 적용하지 못했습니다.",
    };

    private static string DescribeBindingFailure(ConnectionManagementOutcome outcome) => outcome switch
    {
        ConnectionManagementOutcome.Invalid => "선택한 연결 설정 요청이 올바르지 않습니다.",
        ConnectionManagementOutcome.NotFound => "선택한 프로필을 찾지 못해 최신 목록을 다시 불러왔습니다.",
        ConnectionManagementOutcome.NotReady => "선택한 프로필이 안정적으로 저장된 상태가 아닙니다.",
        ConnectionManagementOutcome.StoreFailure => "연결 선택 저장소를 사용할 수 없습니다.",
        ConnectionManagementOutcome.CredentialFailure => "윈도우 보안 저장소를 사용할 수 없습니다.",
        _ => "연결 선택을 저장하지 못했습니다.",
    };

    private static bool IsSha256(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef");

    private static bool IsConnectionError(string? value) =>
        value?.StartsWith("connection.", StringComparison.Ordinal) == true ||
        value?.StartsWith("capability.connection.", StringComparison.Ordinal) == true;

    private static string FormatVersion(CapabilityVersionRef? version) => version is null
        ? "초기 버전"
        : $"v{version.Version.Value} · {version.ContentHash.Value}";

    private static string FormatState(CapabilityDraftState state) => state switch
    {
        CapabilityDraftState.ReadyForReview => "검토 필요",
        CapabilityDraftState.Applied => "적용됨",
        CapabilityDraftState.Stale => "변경됨",
        CapabilityDraftState.Rejected => "거부됨",
        CapabilityDraftState.Validating => "검사 중",
        _ => "초안",
    };
}

internal sealed record LocalConnectionProfileChoice(
    ConnectionProfileId ProfileId,
    string Kind,
    string DisplayText)
{
    public static LocalConnectionProfileChoice Create(HttpConnectionProfile profile)
    {
        var state = profile.Enabled ? "사용" : "사용 안 함";
        var authentication = profile.Authentication == ConnectionAuthenticationKind.Bearer
            ? "접속 토큰"
            : "인증 없음";
        return new(
            profile.Id,
            CapabilityConnectionKindPolicy.Http,
            $"{profile.DisplayName} · {profile.Id.Value} · {profile.BaseUrl} · {authentication} · {state}");
    }

    public static LocalConnectionProfileChoice Create(MailGatewayConnectionProfile profile)
    {
        var state = profile.Enabled ? "사용" : "사용 안 함";
        return new(
            profile.Id,
            CapabilityConnectionKindPolicy.MailApi,
            $"{profile.DisplayName} · {profile.Id.Value} · {profile.BaseUrl} · 발신자 {profile.Sender} · 접속 토큰 · {state}");
    }

    public static LocalConnectionProfileChoice Create(SmtpConnectionProfile profile)
    {
        var state = profile.Enabled ? "사용" : "사용 안 함";
        var tls = profile.TlsMode == SmtpTlsMode.StartTlsRequired
            ? "연결 후 암호화 필수"
            : "처음부터 암호화";
        return new(
            profile.Id,
            CapabilityConnectionKindPolicy.Smtp,
            $"{profile.DisplayName} · {profile.Id.Value} · {profile.Host}:{profile.Port} · " +
            $"발신자 {profile.Sender} · {tls} · 사용자명과 암호로 로그인 · 윈도우 인증서 확인 · {state}");
    }
}

internal sealed class CapabilityConnectionBindingRow
{
    private CapabilityConnectionBindingRow(
        string slot,
        string kind,
        IReadOnlyList<LocalConnectionProfileChoice> profiles,
        LocalConnectionProfileChoice? selectedProfile,
        ConnectionProfileId? boundProfileId,
        string? bindingHash,
        long bindingRevision,
        string statusText)
    {
        Slot = slot;
        Kind = kind;
        Profiles = profiles;
        SelectedProfile = selectedProfile;
        BoundProfileId = boundProfileId;
        BindingHash = bindingHash;
        BindingRevision = bindingRevision;
        StatusText = statusText;
    }

    public string Slot { get; }
    public string Kind { get; }
    public string SlotText => $"{Slot} · {Kind}";
    public string SelectorAutomationName => $"{Slot} 연결 항목에 사용할 이 PC의 연결 설정";
    public IReadOnlyList<LocalConnectionProfileChoice> Profiles { get; }
    public LocalConnectionProfileChoice? SelectedProfile { get; set; }
    public ConnectionProfileId? BoundProfileId { get; }
    public string? BindingHash { get; }
    public bool SelectionMatchesBinding =>
        BoundProfileId is not null &&
        StringComparer.Ordinal.Equals(SelectedProfile?.Kind, Kind) &&
        SelectedProfile?.ProfileId.Equals(BoundProfileId) == true;
    public long BindingRevision { get; }
    public string StatusText { get; }

    public static CapabilityConnectionBindingRow Placeholder(CapabilityConnectionRequirement requirement) =>
        new(
            requirement.Slot,
            requirement.Kind,
            [],
            null,
            null,
            null,
            0,
            "저장된 연결 설정과 선택 내용을 불러오고 있어요…");

    public static CapabilityConnectionBindingRow Create(
        CapabilityConnectionRequirement requirement,
        IReadOnlyList<LocalConnectionProfileChoice> profiles,
        LocalConnectionProfileChoice? selectedProfile,
        long bindingRevision,
        ResolvedConnectionBinding? resolved,
        CapabilityConnectionBinding? binding)
    {
        var status = binding is null
            ? "저장된 연결 선택이 없습니다. 연결 설정을 선택하고 저장하세요."
            : selectedProfile is null
                ? "선택된 연결 설정을 사용할 수 없습니다. 다른 설정을 선택하세요."
                : resolved?.IsReady == true
                    ? $"준비됨 · 연결 선택 변경 번호 {bindingRevision}"
                    : DescribeReadiness(resolved?.ErrorCode, requirement.Kind);
        return new(
            requirement.Slot,
            requirement.Kind,
            profiles,
            selectedProfile,
            binding?.ProfileId,
            binding?.BindingHash,
            bindingRevision,
            status);
    }

    private static string DescribeReadiness(string? errorCode, string kind) => errorCode switch
    {
        "connection.binding.missing" => "저장된 연결 선택이 없습니다.",
        "connection.binding.invalid" => "연결 선택 형식이 올바르지 않아 다시 저장해야 합니다.",
        "connection.binding.stale" => "연결 설정을 바꾼 뒤 선택을 다시 저장해야 합니다.",
        "connection.profile.missing" => "선택된 연결 설정을 찾을 수 없습니다.",
        "connection.profile.disabled" => "선택된 연결 설정이 사용 안 함 상태입니다.",
        "connection.profile.unstable" => "선택된 연결 설정의 저장 상태를 확인해야 합니다.",
        "connection.profile.invalid" => "선택된 연결 설정이 올바르지 않습니다.",
        "connection.probe.required" => "연결 설정 화면에서 실제 연결 확인이 필요합니다.",
        "connection.secret.missing" => StringComparer.Ordinal.Equals(
            kind,
            CapabilityConnectionKindPolicy.Smtp)
            ? "저장된 메일 서버 로그인 정보가 없습니다."
            : "저장된 접속 토큰이 없습니다.",
        "connection.secret.unavailable" => "윈도우 보안 저장소를 확인할 수 없습니다.",
        _ => "외부 연결이 아직 준비되지 않았습니다.",
    };
}
