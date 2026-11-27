using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop;

internal enum ConnectionProfileKind
{
    Http,
    MailGateway,
    Smtp,
}

public partial class ConnectionsPage : System.Windows.Controls.UserControl
{
    private const int MaximumStatusCharacters = 180;
    private readonly ConnectionManagementService _management;
    private readonly IConnectionProfileRepository _profiles;
    private readonly IConnectionCredentialStore _credentials;
    private readonly MailGatewayConnectionManagementService? _mailGatewayManagement;
    private readonly IMailGatewayConnectionRepository? _mailGatewayProfiles;
    private readonly SmtpConnectionManagementService? _smtpManagement;
    private readonly ISmtpConnectionRepository? _smtpProfiles;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _operationCancellation;
    private ConnectionProfileListItem? _editingProfile;
    private ConnectionProfileKind _newProfileKind;
    private string _storedCredentialStatus = "인증 없음을 사용합니다.";
    private bool _busy;
    private bool _populatingEditor;
    private int _refreshVersion;

    internal ConnectionsPage(
        ConnectionManagementService management,
        IConnectionProfileRepository profiles,
        IConnectionCredentialStore credentials,
        MailGatewayConnectionManagementService? mailGatewayManagement = null,
        IMailGatewayConnectionRepository? mailGatewayProfiles = null,
        SmtpConnectionManagementService? smtpManagement = null,
        ISmtpConnectionRepository? smtpProfiles = null)
    {
        _management = management ?? throw new ArgumentNullException(nameof(management));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        if ((mailGatewayManagement is null) != (mailGatewayProfiles is null))
            throw new ArgumentException("Mail Gateway UI 종속성은 함께 제공해야 합니다.");
        if ((smtpManagement is null) != (smtpProfiles is null))
            throw new ArgumentException("SMTP UI 종속성은 함께 제공해야 합니다.");
        _mailGatewayManagement = mailGatewayManagement;
        _mailGatewayProfiles = mailGatewayProfiles;
        _smtpManagement = smtpManagement;
        _smtpProfiles = smtpProfiles;
        InitializeComponent();

        var profileKinds = new List<ProfileKindChoice>
        {
            new("웹 서버", ConnectionProfileKind.Http),
        };
        if (HasMailGatewaySupport)
            profileKinds.Add(new("사내 메일 중계 서버", ConnectionProfileKind.MailGateway));
        if (HasSmtpSupport)
            profileKinds.Add(new("표준 메일 서버", ConnectionProfileKind.Smtp));
        ProfileKindBox.ItemsSource = profileKinds;
        AuthenticationBox.ItemsSource = new[]
        {
            new AuthenticationChoice("인증 없음", ConnectionAuthenticationKind.None),
            new AuthenticationChoice("접속 토큰", ConnectionAuthenticationKind.Bearer),
        };
        SecretMutationBox.ItemsSource = new[]
        {
            new SecretMutationChoice("저장된 인증정보 유지", ConnectionSecretMutation.Keep),
            new SecretMutationChoice("새 인증정보로 교체", ConnectionSecretMutation.Replace),
            new SecretMutationChoice("저장된 인증정보 삭제", ConnectionSecretMutation.Delete),
        };
        SmtpTlsModeBox.ItemsSource = new[]
        {
            new SmtpTlsModeChoice("연결 후 암호화 필수", SmtpTlsMode.StartTlsRequired),
            new SmtpTlsModeChoice("처음부터 암호화", SmtpTlsMode.ImplicitTls),
        };

        BeginNewProfile(ConnectionProfileKind.Http);
        Loaded += async (_, _) => await ExecuteUiAsync(
            () => RefreshAsync(),
            "연결 설정 목록을 불러오지 못했습니다.");
        Unloaded += (_, _) =>
        {
            Interlocked.Exchange(ref _refreshCancellation, null)?.Cancel();
            Interlocked.Exchange(ref _operationCancellation, null)?.Cancel();
            ClearSecretInputs();
        };
    }

    private bool HasMailGatewaySupport =>
        _mailGatewayManagement is not null && _mailGatewayProfiles is not null;

    private bool HasSmtpSupport =>
        _smtpManagement is not null && _smtpProfiles is not null;

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiAsync(
            () => RefreshAsync(
                focusProfileId: _editingProfile?.Id,
                focusProfileKind: _editingProfile?.Kind),
            "연결 설정 목록을 새로 고치지 못했습니다.");

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ConnectionList.SelectedItem = null;
        BeginNewProfile(ConnectionProfileKind.Http);
        DisplayNameTextBox.Focus();
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement { Tag: ConnectionProfileListItem item }) return;
        ConnectionList.SelectedItem = item;
        ConnectionList.ScrollIntoView(item);
        PopulateEditor(item);
    }

    private void ConnectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy || _populatingEditor || ConnectionList.SelectedItem is not ConnectionProfileListItem item) return;
        PopulateEditor(item);
    }

    private void ProfileKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingEditor || _editingProfile is not null) return;
        var selected = SelectedProfileKind;
        if (selected == ConnectionProfileKind.MailGateway && !HasMailGatewaySupport)
        {
            SetPageStatus("Mail Gateway 관리 기능을 사용할 수 없습니다.", isError: true);
            BeginNewProfile(ConnectionProfileKind.Http);
            return;
        }
        if (selected == ConnectionProfileKind.Smtp && !HasSmtpSupport)
        {
            SetPageStatus("메일 서버 관리 기능을 사용할 수 없습니다.", isError: true);
            BeginNewProfile(ConnectionProfileKind.Http);
            return;
        }
        if (selected == _newProfileKind)
        {
            ApplyKindPresentation(selected);
            return;
        }

        var displayName = DisplayNameTextBox.Text;
        BeginNewProfile(selected);
        DisplayNameTextBox.Text = displayName;
    }

    private void AuthenticationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingEditor || SelectedProfileKind != ConnectionProfileKind.Http) return;
        var authentication = SelectedAuthentication;
        if (authentication == ConnectionAuthenticationKind.Bearer)
        {
            SecretMutationBox.SelectedValue = _editingProfile is
            {
                Kind: ConnectionProfileKind.Http,
                HttpProfile.Authentication: ConnectionAuthenticationKind.Bearer,
                State: ConnectionProfileLifecycleState.Stable,
            }
                ? ConnectionSecretMutation.Keep
                : ConnectionSecretMutation.Replace;
        }
        else
        {
            SecretMutationBox.SelectedValue = ConnectionSecretMutation.Keep;
            ClearSecretInputs();
        }
        UpdateSecretControls();
    }

    private void SecretMutationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingEditor) return;
        if (SelectedSecretMutation != ConnectionSecretMutation.Replace) ClearSecretInputs();
        UpdateSecretControls();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var profileKind = SelectedProfileKind;
        if (profileKind == ConnectionProfileKind.MailGateway && !HasMailGatewaySupport)
        {
            SetPageStatus("Mail Gateway 관리 기능을 사용할 수 없습니다.", isError: true);
            return;
        }
        if (profileKind == ConnectionProfileKind.Smtp && !HasSmtpSupport)
        {
            SetPageStatus("메일 서버 관리 기능을 사용할 수 없습니다.", isError: true);
            return;
        }
        if (_editingProfile is not null && _editingProfile.Kind != profileKind)
        {
            SetPageStatus("저장된 프로필 종류는 변경할 수 없습니다.", isError: true);
            return;
        }
        if (!ConnectionProfileId.TryParse(ProfileIdTextBox.Text, out var profileId) || profileId is null)
        {
            SetPageStatus("프로필 ID가 올바르지 않습니다. 새 프로필을 다시 만들어 주세요.", isError: true);
            return;
        }

        var authentication = profileKind == ConnectionProfileKind.Http
            ? SelectedAuthentication
            : ConnectionAuthenticationKind.Bearer;
        var mutation = profileKind != ConnectionProfileKind.Http ||
                       authentication == ConnectionAuthenticationKind.Bearer
            ? SelectedSecretMutation
            : ConnectionSecretMutation.Keep;
        string? secret = null;
        string? smtpUsername = null;
        string? smtpPassword = null;
        if (mutation == ConnectionSecretMutation.Replace)
        {
            if (profileKind == ConnectionProfileKind.Smtp)
            {
                smtpUsername = SmtpUsernameBox.Password;
                smtpPassword = SmtpPasswordBox.Password;
            }
            else
            {
                secret = BearerTokenBox.Password;
            }
        }
        ClearSecretInputs();
        if (mutation == ConnectionSecretMutation.Replace &&
            (profileKind == ConnectionProfileKind.Smtp
                ? string.IsNullOrEmpty(smtpUsername) || string.IsNullOrEmpty(smtpPassword)
                : string.IsNullOrEmpty(secret)))
        {
            secret = null;
            smtpUsername = null;
            smtpPassword = null;
            SetPageStatus(
                profileKind == ConnectionProfileKind.Smtp
                    ? "교체할 메일 서버 사용자명과 암호를 모두 입력해 주세요."
                    : "교체할 접속 토큰을 입력해 주세요.",
                isError: true);
            return;
        }

        try
        {
            switch (profileKind)
            {
                case ConnectionProfileKind.Http:
                    {
                        var draft = new HttpConnectionProfileDraft(
                            profileId,
                            DisplayNameTextBox.Text,
                            BaseUrlTextBox.Text,
                            authentication,
                            EnabledCheckBox.IsChecked == true);
                        var expectedRevision = _editingProfile?.Revision ?? 0;
                        await RunOperationAsync(cancellationToken => SaveHttpProfileAsync(
                            draft,
                            expectedRevision,
                            mutation,
                            secret,
                            cancellationToken));
                        break;
                    }
                case ConnectionProfileKind.MailGateway:
                    {
                        var draft = new MailGatewayConnectionProfileDraft(
                            profileId,
                            DisplayNameTextBox.Text,
                            BaseUrlTextBox.Text,
                            SenderTextBox.Text,
                            EnabledCheckBox.IsChecked == true);
                        var expectedRevision = _editingProfile?.Revision ?? 0;
                        await RunOperationAsync(cancellationToken => SaveMailGatewayProfileAsync(
                            draft,
                            expectedRevision,
                            mutation,
                            secret,
                            cancellationToken));
                        break;
                    }
                case ConnectionProfileKind.Smtp:
                    {
                        if (!int.TryParse(
                                SmtpPortTextBox.Text,
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var port))
                        {
                            SetPageStatus("메일 서버 포트는 1부터 65535 사이의 숫자여야 합니다.", isError: true);
                            return;
                        }
                        var draft = new SmtpConnectionProfileDraft(
                            profileId,
                            DisplayNameTextBox.Text,
                            SmtpHostTextBox.Text,
                            port,
                            SelectedSmtpTlsMode,
                            SenderTextBox.Text,
                            EnabledCheckBox.IsChecked == true);
                        var expectedRevision = _editingProfile?.Revision ?? 0;
                        await RunOperationAsync(cancellationToken => SaveSmtpProfileAsync(
                            draft,
                            expectedRevision,
                            mutation,
                            smtpUsername,
                            smtpPassword,
                            cancellationToken));
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(profileKind));
            }
        }
        finally
        {
            secret = null;
            smtpUsername = null;
            smtpPassword = null;
            ClearSecretInputs();
        }
    }

    private async Task SaveHttpProfileAsync(
        HttpConnectionProfileDraft draft,
        long expectedRevision,
        ConnectionSecretMutation mutation,
        string? secret,
        CancellationToken cancellationToken)
    {
        SetPageStatus("웹 서버 연결 설정을 저장하고 있어요…", isError: false);
        var result = await _management.SaveHttpProfileAsync(
            draft,
            expectedRevision,
            mutation,
            secret,
            cancellationToken);
        if (result.Outcome == ConnectionManagementOutcome.Succeeded && result.Profile is not null)
        {
            await RefreshAsync(
                "웹 서버 연결 설정을 저장했습니다. 연결 확인을 실행하세요.",
                result.Profile.Id,
                ConnectionProfileKind.Http);
            return;
        }

        var failureMessage = DescribeMutationFailure(
            result.Outcome,
            result.ErrorCode,
            ConnectionProfileKind.Http);
        if (result.Outcome is ConnectionManagementOutcome.Stale or ConnectionManagementOutcome.CredentialFailure)
            await RefreshAsync(
                focusProfileId: draft.Id,
                focusProfileKind: ConnectionProfileKind.Http);
        SetPageStatus(failureMessage, isError: true);
    }

    private async Task SaveMailGatewayProfileAsync(
        MailGatewayConnectionProfileDraft draft,
        long expectedRevision,
        ConnectionSecretMutation mutation,
        string? secret,
        CancellationToken cancellationToken)
    {
        if (_mailGatewayManagement is null)
        {
            SetPageStatus("Mail Gateway 관리 기능을 사용할 수 없습니다.", isError: true);
            return;
        }

        SetPageStatus("Mail Gateway 프로필을 저장하고 있어요…", isError: false);
        var result = await _mailGatewayManagement.SaveMailGatewayProfileAsync(
            draft,
            expectedRevision,
            mutation,
            secret,
            cancellationToken);
        if (result.Outcome == ConnectionManagementOutcome.Succeeded && result.Profile is not null)
        {
            await RefreshAsync(
                "Mail Gateway 프로필을 저장했습니다. 고정 Gateway 확인을 실행하세요.",
                result.Profile.Id,
                ConnectionProfileKind.MailGateway);
            return;
        }

        var failureMessage = DescribeMutationFailure(
            result.Outcome,
            result.ErrorCode,
            ConnectionProfileKind.MailGateway);
        if (result.Outcome is ConnectionManagementOutcome.Stale or ConnectionManagementOutcome.CredentialFailure)
            await RefreshAsync(
                focusProfileId: draft.Id,
                focusProfileKind: ConnectionProfileKind.MailGateway);
        SetPageStatus(failureMessage, isError: true);
    }

    private async Task SaveSmtpProfileAsync(
        SmtpConnectionProfileDraft draft,
        long expectedRevision,
        ConnectionSecretMutation mutation,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (_smtpManagement is null)
        {
            SetPageStatus("메일 서버 관리 기능을 사용할 수 없습니다.", isError: true);
            return;
        }

        SetPageStatus("표준 메일 서버 연결 설정을 저장하고 있어요…", isError: false);
        var result = await _smtpManagement.SaveSmtpProfileAsync(
            draft,
            expectedRevision,
            mutation,
            username,
            password,
            cancellationToken);
        if (result.Outcome == ConnectionManagementOutcome.Succeeded && result.Profile is not null)
        {
            await RefreshAsync(
                "표준 메일 서버 연결 설정을 저장했습니다. 연결 확인을 실행하세요.",
                result.Profile.Id,
                ConnectionProfileKind.Smtp);
            return;
        }

        var failureMessage = DescribeMutationFailure(
            result.Outcome,
            result.ErrorCode,
            ConnectionProfileKind.Smtp);
        if (result.Outcome is ConnectionManagementOutcome.Stale or ConnectionManagementOutcome.CredentialFailure)
            await RefreshAsync(
                focusProfileId: draft.Id,
                focusProfileKind: ConnectionProfileKind.Smtp);
        SetPageStatus(failureMessage, isError: true);
    }

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _editingProfile is null) return;
        var item = _editingProfile;
        await RunOperationAsync(cancellationToken => item.Kind switch
        {
            ConnectionProfileKind.Http => ProbeHttpProfileAsync(item.Id, cancellationToken),
            ConnectionProfileKind.MailGateway => ProbeMailGatewayProfileAsync(item.Id, cancellationToken),
            ConnectionProfileKind.Smtp => ProbeSmtpProfileAsync(item.Id, cancellationToken),
            _ => Task.CompletedTask,
        });
    }

    private async Task ProbeHttpProfileAsync(
        ConnectionProfileId profileId,
        CancellationToken cancellationToken)
    {
        SetPageStatus("저장된 설정으로 웹 서버 연결을 확인하고 있어요…", isError: false);
        var result = await _management.ProbeHttpProfileAsync(profileId, cancellationToken);
        if (result.Outcome != ConnectionManagementOutcome.Succeeded || result.Receipt is null)
        {
            SetPageStatus(
                DescribeProbeOperationFailure(
                    result.Outcome,
                    result.ErrorCode,
                    ConnectionProfileKind.Http),
                isError: true);
            if (result.Outcome == ConnectionManagementOutcome.Stale)
                await RefreshAsync(
                    focusProfileId: profileId,
                    focusProfileKind: ConnectionProfileKind.Http);
            return;
        }

        await RefreshAsync(
            focusProfileId: profileId,
            focusProfileKind: ConnectionProfileKind.Http);
        SetPageStatus(
            result.Receipt.Succeeded
                ? "웹 서버에 실제로 연결되는 것을 확인했습니다."
                : DescribeHttpProbeFailure(result.Receipt.ErrorCode),
            isError: !result.Receipt.Succeeded);
    }

    private async Task ProbeMailGatewayProfileAsync(
        ConnectionProfileId profileId,
        CancellationToken cancellationToken)
    {
        if (_mailGatewayManagement is null)
        {
            SetPageStatus("Mail Gateway 관리 기능을 사용할 수 없습니다.", isError: true);
            return;
        }

        SetPageStatus("저장된 설정으로 고정 Mail Gateway 계약을 확인하고 있어요…", isError: false);
        var result = await _mailGatewayManagement.ProbeMailGatewayProfileAsync(
            profileId,
            cancellationToken);
        if (result.Outcome != ConnectionManagementOutcome.Succeeded || result.Receipt is null)
        {
            SetPageStatus(
                DescribeProbeOperationFailure(
                    result.Outcome,
                    result.ErrorCode,
                    ConnectionProfileKind.MailGateway),
                isError: true);
            if (result.Outcome == ConnectionManagementOutcome.Stale)
                await RefreshAsync(
                    focusProfileId: profileId,
                    focusProfileKind: ConnectionProfileKind.MailGateway);
            return;
        }

        await RefreshAsync(
            focusProfileId: profileId,
            focusProfileKind: ConnectionProfileKind.MailGateway);
        SetPageStatus(
            result.Receipt.Succeeded
                ? "고정 Mail Gateway 계약과 연결을 확인했습니다."
                : DescribeMailGatewayProbeFailure(result.Receipt.ErrorCode),
            isError: !result.Receipt.Succeeded);
    }

    private async Task ProbeSmtpProfileAsync(
        ConnectionProfileId profileId,
        CancellationToken cancellationToken)
    {
        if (_smtpManagement is null)
        {
            SetPageStatus("메일 서버 관리 기능을 사용할 수 없습니다.", isError: true);
            return;
        }

        SetPageStatus("저장된 설정으로 메일 서버의 암호화 연결과 로그인을 확인하고 있어요…", isError: false);
        var result = await _smtpManagement.ProbeSmtpProfileAsync(profileId, cancellationToken);
        if (result.Outcome != ConnectionManagementOutcome.Succeeded || result.Receipt is null)
        {
            SetPageStatus(
                DescribeProbeOperationFailure(
                    result.Outcome,
                    result.ErrorCode,
                    ConnectionProfileKind.Smtp),
                isError: true);
            if (result.Outcome == ConnectionManagementOutcome.Stale)
                await RefreshAsync(
                    focusProfileId: profileId,
                    focusProfileKind: ConnectionProfileKind.Smtp);
            return;
        }

        await RefreshAsync(
            focusProfileId: profileId,
            focusProfileKind: ConnectionProfileKind.Smtp);
        SetPageStatus(
            result.Receipt.Succeeded
                ? "메일 서버의 암호화 연결과 로그인을 확인했습니다. 실제 메일은 보내지 않았습니다."
                : DescribeSmtpProbeFailure(result.Receipt.ErrorCode),
            isError: !result.Receipt.Succeeded);
    }

    internal async Task RefreshAsync(
        string? completionMessage = null,
        ConnectionProfileId? focusProfileId = null,
        ConnectionProfileKind? focusProfileKind = null)
    {
        if (!IsLoaded) return;
        var version = Interlocked.Increment(ref _refreshVersion);
        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _refreshCancellation, cancellation)?.Cancel();
        SetPageStatus("연결 설정 목록을 불러오고 있어요…", isError: false);

        var httpProfiles = await _profiles.ListProfilesAsync(cancellation.Token);
        var items = new List<ConnectionProfileListItem>(httpProfiles.Count);
        foreach (var profile in httpProfiles)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var probe = await _profiles.GetProbeReceiptAsync(profile.Id, cancellation.Token);
            var credentialExists = profile.Authentication == ConnectionAuthenticationKind.Bearer &&
                                   await _credentials.ExistsAsync(
                                       profile.Id,
                                       ConnectionCredentialKind.BearerToken,
                                       cancellation.Token);
            items.Add(ConnectionProfileListItem.CreateHttp(profile, probe, credentialExists));
        }

        if (_mailGatewayProfiles is not null)
        {
            var mailProfiles = await _mailGatewayProfiles.ListProfilesAsync(cancellation.Token);
            foreach (var profile in mailProfiles)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var probe = await _mailGatewayProfiles.GetProbeReceiptAsync(profile.Id, cancellation.Token);
                var credentialExists = await _credentials.ExistsAsync(
                    profile.Id,
                    ConnectionCredentialKind.MailGatewayBearerToken,
                    cancellation.Token);
                items.Add(ConnectionProfileListItem.CreateMailGateway(profile, probe, credentialExists));
            }
        }

        if (_smtpProfiles is not null)
        {
            var smtpProfiles = await _smtpProfiles.ListProfilesAsync(cancellation.Token);
            foreach (var profile in smtpProfiles)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var probe = await _smtpProfiles.GetProbeReceiptAsync(profile.Id, cancellation.Token);
                var credentialExists = await _credentials.ExistsAsync(
                    profile.Id,
                    ConnectionCredentialKind.SmtpAuth,
                    cancellation.Token);
                items.Add(ConnectionProfileListItem.CreateSmtp(profile, probe, credentialExists));
            }
        }

        items.Sort(ConnectionProfileListItemComparer.Instance);
        Interlocked.CompareExchange(ref _refreshCancellation, null, cancellation);
        cancellation.Token.ThrowIfCancellationRequested();
        if (version != Volatile.Read(ref _refreshVersion)) return;

        ConnectionList.ItemsSource = items;
        EmptyMessage.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetPageStatus(completionMessage ?? $"연결 설정 {items.Count}개", isError: false);

        var targetId = focusProfileId ?? _editingProfile?.Id;
        var targetKind = focusProfileKind ?? _editingProfile?.Kind;
        var target = FindFocusTarget(items, targetId, targetKind);
        if (target is not null)
        {
            ConnectionList.SelectedItem = target;
            ConnectionList.ScrollIntoView(target);
            PopulateEditor(target);
        }
        else if (items.Count == 0 && _editingProfile is not null)
        {
            BeginNewProfile(ConnectionProfileKind.Http);
        }
    }

    private static ConnectionProfileListItem? FindFocusTarget(
        IReadOnlyList<ConnectionProfileListItem> items,
        ConnectionProfileId? targetId,
        ConnectionProfileKind? targetKind)
    {
        if (targetId is null) return null;
        if (targetKind is not null)
            return items.FirstOrDefault(item =>
                item.Id.Equals(targetId) && item.Kind == targetKind);

        ConnectionProfileListItem? unique = null;
        foreach (var item in items.Where(item => item.Id.Equals(targetId)))
        {
            if (unique is not null) return null;
            unique = item;
        }
        return unique;
    }

    private void BeginNewProfile(ConnectionProfileKind kind)
    {
        if (kind == ConnectionProfileKind.MailGateway && !HasMailGatewaySupport)
            kind = ConnectionProfileKind.Http;
        if (kind == ConnectionProfileKind.Smtp && !HasSmtpSupport)
            kind = ConnectionProfileKind.Http;
        _populatingEditor = true;
        try
        {
            _editingProfile = null;
            _newProfileKind = kind;
            EditorTitleText.Text = "새 연결 설정";
            ProfileKindBox.SelectedValue = kind;
            ProfileIdTextBox.Text = kind switch
            {
                ConnectionProfileKind.MailGateway => $"mail-gateway-{Guid.NewGuid():N}",
                ConnectionProfileKind.Smtp => $"smtp-{Guid.NewGuid():N}",
                _ => $"connection-{Guid.NewGuid():N}",
            };
            DisplayNameTextBox.Clear();
            BaseUrlTextBox.Clear();
            SmtpHostTextBox.Clear();
            SmtpPortTextBox.Text = "587";
            SmtpTlsModeBox.SelectedValue = SmtpTlsMode.StartTlsRequired;
            SenderTextBox.Clear();
            AuthenticationBox.SelectedValue = kind != ConnectionProfileKind.Http
                ? ConnectionAuthenticationKind.Bearer
                : ConnectionAuthenticationKind.None;
            EnabledCheckBox.IsChecked = true;
            SecretMutationBox.SelectedValue = kind != ConnectionProfileKind.Http
                ? ConnectionSecretMutation.Replace
                : ConnectionSecretMutation.Keep;
            ClearSecretInputs();
            _storedCredentialStatus = kind switch
            {
                ConnectionProfileKind.MailGateway =>
                    "새 사내 메일 중계 서버에는 전용 접속 토큰을 입력해야 합니다. 저장 후 값은 표시되지 않습니다.",
                ConnectionProfileKind.Smtp =>
                    "새 표준 메일 서버에는 사용자명과 암호를 입력해야 합니다. 저장 후 두 값은 표시되지 않습니다.",
                _ => "인증 없음을 사용합니다.",
            };
            CredentialStatusText.Text = _storedCredentialStatus;
        }
        finally
        {
            _populatingEditor = false;
        }
        ApplyKindPresentation(kind);
        UpdateSecretControls();
        UpdateActionState();
    }

    private void PopulateEditor(ConnectionProfileListItem item)
    {
        _populatingEditor = true;
        try
        {
            _editingProfile = item;
            EditorTitleText.Text = "연결 설정 수정";
            ProfileKindBox.SelectedValue = item.Kind;
            ProfileIdTextBox.Text = item.Id.Value;
            DisplayNameTextBox.Text = item.DisplayName;
            BaseUrlTextBox.Text = item.HttpProfile?.BaseUrl ?? item.MailGatewayProfile?.BaseUrl ?? string.Empty;
            SmtpHostTextBox.Text = item.SmtpProfile?.Host ?? string.Empty;
            SmtpPortTextBox.Text = item.SmtpProfile?.Port.ToString(CultureInfo.InvariantCulture) ?? "587";
            SmtpTlsModeBox.SelectedValue = item.SmtpProfile?.TlsMode ?? SmtpTlsMode.StartTlsRequired;
            EnabledCheckBox.IsChecked = item.Enabled;
            SenderTextBox.Text = item.MailGatewayProfile?.Sender ?? item.SmtpProfile?.Sender ?? string.Empty;
            AuthenticationBox.SelectedValue = item.Kind != ConnectionProfileKind.Http
                ? ConnectionAuthenticationKind.Bearer
                : item.HttpProfile!.Authentication;
            SecretMutationBox.SelectedValue = item.RequiresCredentialRepair
                ? ConnectionSecretMutation.Replace
                : ConnectionSecretMutation.Keep;
            ClearSecretInputs();
            _storedCredentialStatus = item.CredentialText;
            CredentialStatusText.Text = _storedCredentialStatus;
        }
        finally
        {
            _populatingEditor = false;
        }
        ApplyKindPresentation(item.Kind);
        UpdateSecretControls();
        UpdateActionState();
    }

    private void ApplyKindPresentation(ConnectionProfileKind kind)
    {
        var isMailGateway = kind == ConnectionProfileKind.MailGateway;
        var isSmtp = kind == ConnectionProfileKind.Smtp;
        EditorKindEyebrow.Text = kind switch
        {
            ConnectionProfileKind.MailGateway => "연결 설정 / 사내 메일 중계 서버",
            ConnectionProfileKind.Smtp => "연결 설정 / 표준 메일 서버",
            _ => "연결 설정 / 웹 서버",
        };
        BaseUrlPanel.Visibility = isSmtp ? Visibility.Collapsed : Visibility.Visible;
        SmtpEndpointPanel.Visibility = isSmtp ? Visibility.Visible : Visibility.Collapsed;
        BaseUrlLabelText.Text = isMailGateway ? "메일 중계 서버 기본 주소" : "서버 기본 주소";
        BaseUrlHelpText.Text = isMailGateway
            ? "암호화된 서버의 기본 주소만 허용합니다. 세부 경로는 앱이 정하며 인공지능 모델에 전달되지 않습니다."
            : "세부 경로와 검색 조건은 기능이 허용된 형식으로 추가하며, 이 주소는 인공지능 모델에 전달되지 않습니다.";
        SenderPanel.Visibility = isMailGateway || isSmtp ? Visibility.Visible : Visibility.Collapsed;
        SenderHelpText.Text = isSmtp
            ? "메일 발신자로 고정할 영문 주소입니다. 실제 전송 때 변경할 수 없습니다."
            : "메일 중계 서버에 등록된 영문 주소를 입력합니다. 실제 전송 때 변경할 수 없습니다.";
        AutomationProperties.SetName(
            SenderTextBox,
            isSmtp ? "메일 서버 고정 발신자" : "메일 중계 서버 고정 발신자");
        HttpAuthenticationPanel.Visibility = isSmtp ? Visibility.Collapsed : Visibility.Visible;
        SmtpSecurityPanel.Visibility = isSmtp ? Visibility.Visible : Visibility.Collapsed;
        AuthenticationLabelText.Text = isMailGateway ? "인증 방식 · 고정" : "인증 방식";
        SecretLabelText.Text = kind switch
        {
            ConnectionProfileKind.MailGateway => "메일 중계 서버 전용 접속 토큰 처리",
            ConnectionProfileKind.Smtp => "메일 서버 로그인 정보 처리",
            _ => "접속 토큰 처리",
        };
        BearerTokenBox.Visibility = isSmtp ? Visibility.Collapsed : Visibility.Visible;
        SmtpCredentialPanel.Visibility = isSmtp ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(
            SecretMutationBox,
            isSmtp ? "메일 서버 로그인 정보 변경 방식" : "접속 토큰 변경 방식");
        AutomationProperties.SetName(
            BaseUrlTextBox,
            isMailGateway ? "메일 중계 서버 기본 주소" : "연결 서버 기본 주소");
        AutomationProperties.SetName(
            ProbeButton,
            kind switch
            {
                ConnectionProfileKind.MailGateway => "메일 중계 서버 실제 연결 확인",
                ConnectionProfileKind.Smtp => "메일 서버 암호화 연결과 로그인 확인",
                _ => "실제 연결 확인",
            });
        ProbeButton.Content = kind switch
        {
            ConnectionProfileKind.MailGateway => "메일 중계 서버 확인",
            ConnectionProfileKind.Smtp => "메일 서버 확인",
            _ => "연결 확인",
        };
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
            SetPageStatus("연결 설정 작업을 완료하지 못했습니다. 다시 시도하세요.", isError: true);
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
        NewProfileButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        ConnectionList.IsEnabled = !busy;
        EditorPanel.IsEnabled = !busy;
        UpdateSecretControls();
        UpdateActionState();
    }

    private void UpdateSecretControls()
    {
        var isMailGateway = SelectedProfileKind == ConnectionProfileKind.MailGateway;
        var isSmtp = SelectedProfileKind == ConnectionProfileKind.Smtp;
        var requiresCredential = isMailGateway || isSmtp ||
                                 SelectedAuthentication == ConnectionAuthenticationKind.Bearer;
        ProfileKindBox.IsEnabled = !_busy && _editingProfile is null && ProfileKindBox.Items.Count > 1;
        AuthenticationBox.IsEnabled = !_busy && !isMailGateway && !isSmtp;
        SecretMutationBox.IsEnabled = requiresCredential && !_busy;
        BearerTokenBox.IsEnabled = requiresCredential && !isSmtp &&
                                    SelectedSecretMutation == ConnectionSecretMutation.Replace &&
                                    !_busy;
        SmtpUsernameBox.IsEnabled = isSmtp &&
                                    SelectedSecretMutation == ConnectionSecretMutation.Replace &&
                                    !_busy;
        SmtpPasswordBox.IsEnabled = SmtpUsernameBox.IsEnabled;
        if (!requiresCredential)
            CredentialStatusText.Text =
                "로그인 없음을 사용합니다. 저장할 때 기존 웹 서버 접속 토큰이 있다면 삭제합니다.";
        else if (_editingProfile is null)
            CredentialStatusText.Text = SelectedProfileKind switch
            {
                ConnectionProfileKind.MailGateway =>
                    "Mail Gateway 전용 token은 저장 후 화면에서 다시 표시되지 않습니다.",
                ConnectionProfileKind.Smtp =>
                    "메일 서버 사용자명과 암호는 저장 후 화면에서 다시 표시되지 않습니다.",
                _ => "token 값은 저장 후 화면에서 다시 표시되지 않습니다.",
            };
        else
            CredentialStatusText.Text = _storedCredentialStatus;
    }

    private void UpdateActionState()
    {
        SaveButton.IsEnabled = !_busy &&
                               (SelectedProfileKind != ConnectionProfileKind.MailGateway ||
                                 HasMailGatewaySupport) &&
                               (SelectedProfileKind != ConnectionProfileKind.Smtp ||
                                HasSmtpSupport);
        ProbeButton.IsEnabled = !_busy && _editingProfile is
        {
            Enabled: true,
            State: ConnectionProfileLifecycleState.Stable,
        };
    }

    private void SetPageStatus(string message, bool isError)
    {
        PageStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "TextSecondaryBrush");
        PageStatusText.Text = BoundStatus(message);
    }

    private static string BoundStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "연결 상태를 확인할 수 없습니다.";
        var safe = new string(message
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray()).Trim();
        return safe.Length <= MaximumStatusCharacters
            ? safe
            : $"{safe[..(MaximumStatusCharacters - 1)]}…";
    }

    private ConnectionProfileKind SelectedProfileKind =>
        ProfileKindBox.SelectedValue is ConnectionProfileKind kind
            ? kind
            : ConnectionProfileKind.Http;

    private ConnectionAuthenticationKind SelectedAuthentication =>
        AuthenticationBox.SelectedValue is ConnectionAuthenticationKind authentication
            ? authentication
            : ConnectionAuthenticationKind.None;

    private ConnectionSecretMutation SelectedSecretMutation =>
        SecretMutationBox.SelectedValue is ConnectionSecretMutation mutation
            ? mutation
            : ConnectionSecretMutation.Keep;

    private SmtpTlsMode SelectedSmtpTlsMode =>
        SmtpTlsModeBox.SelectedValue is SmtpTlsMode tlsMode
            ? tlsMode
            : SmtpTlsMode.StartTlsRequired;

    private void ClearSecretInputs()
    {
        BearerTokenBox.Clear();
        SmtpUsernameBox.Clear();
        SmtpPasswordBox.Clear();
    }

    private static string DescribeMutationFailure(
        ConnectionManagementOutcome outcome,
        string? errorCode,
        ConnectionProfileKind profileKind)
    {
        var profileType = profileKind switch
        {
            ConnectionProfileKind.MailGateway => "사내 메일 중계 서버",
            ConnectionProfileKind.Smtp => "표준 메일 서버",
            _ => "웹 서버 연결",
        };
        return errorCode switch
        {
            "connection.credential.retry_required" =>
                $"완료되지 않은 {profileType} 자격 증명 변경이 있습니다. 인증정보를 다시 입력하거나 명시적으로 삭제해 주세요.",
            _ => outcome switch
            {
                ConnectionManagementOutcome.Invalid => profileKind switch
                {
                    ConnectionProfileKind.MailGateway =>
                        "표시 이름, 암호화된 서버 기본 주소, 영문 발신자와 전용 접속 토큰을 확인해 주세요.",
                    ConnectionProfileKind.Smtp =>
                        "표시 이름, 서버 주소, 포트, 암호화 방식, 영문 발신자와 사용자명·암호를 확인해 주세요.",
                    _ => "표시 이름, 서버 주소, 로그인 방식과 접속 토큰 입력을 확인해 주세요.",
                },
                ConnectionManagementOutcome.NotFound =>
                    $"{profileType} 프로필을 찾지 못했습니다. 목록을 새로 고치세요.",
                ConnectionManagementOutcome.Stale =>
                    "프로필이 다른 작업에서 변경됐습니다. 최신 내용을 다시 불러왔습니다.",
                ConnectionManagementOutcome.CredentialFailure =>
                    "윈도우 보안 저장소에 로그인 정보를 안전하게 저장하지 못했습니다.",
                ConnectionManagementOutcome.StoreFailure => "연결 설정 저장소를 사용할 수 없습니다.",
                _ => $"{profileType} 프로필을 저장할 준비가 되지 않았습니다.",
            },
        };
    }

    private static string DescribeProbeOperationFailure(
        ConnectionManagementOutcome outcome,
        string? errorCode,
        ConnectionProfileKind profileKind) => errorCode switch
        {
            "connection.secret.missing" => profileKind switch
            {
                ConnectionProfileKind.MailGateway =>
                    "저장된 메일 중계 서버 전용 접속 토큰이 없습니다. 연결 설정에서 새 토큰으로 교체해 주세요.",
                ConnectionProfileKind.Smtp =>
                    "저장된 메일 서버 로그인 정보가 없습니다. 연결 설정에서 사용자명과 암호를 새로 입력해 주세요.",
                _ => "저장된 웹 서버 접속 토큰이 없습니다. 연결 설정에서 새 토큰으로 교체해 주세요.",
            },
            "connection.secret.unavailable" =>
                "윈도우 보안 저장소의 로그인 정보를 읽지 못했습니다.",
            "connection.probe.unavailable" => "고정 연결 확인 서비스를 사용할 수 없습니다.",
            _ => outcome switch
            {
                ConnectionManagementOutcome.NotFound =>
                    "연결 설정을 찾지 못했습니다. 목록을 새로 고치세요.",
                ConnectionManagementOutcome.NotReady =>
                    "사용 중인 저장된 프로필만 연결을 확인할 수 있습니다.",
                ConnectionManagementOutcome.Stale =>
                    "확인 중 프로필이 변경되어 결과를 사용하지 않았습니다.",
                ConnectionManagementOutcome.CredentialFailure =>
                    "윈도우 보안 저장소의 로그인 정보를 읽지 못했습니다.",
                ConnectionManagementOutcome.StoreFailure =>
                    "연결 확인 기능 또는 저장소를 사용할 수 없습니다.",
                _ => "연결 상태를 확인할 수 없습니다.",
            },
        };

    private static string DescribeHttpProbeFailure(string? errorCode) => errorCode switch
    {
        "connection.probe.credential_unavailable" => "저장된 웹 서버 접속 토큰을 사용할 수 없습니다.",
        "connection.probe.redirect_denied" => "서버가 다른 주소로 이동을 요청해 연결을 허용하지 않았습니다.",
        "connection.probe.status_rejected" => "서버가 성공 응답을 보내지 않았습니다.",
        "connection.probe.timeout" => "웹 서버 연결 확인 시간이 초과됐습니다.",
        "connection.probe.profile_invalid" => "저장된 웹 서버 연결 설정이 올바르지 않습니다.",
        _ => "웹 서버에 연결하지 못했습니다.",
    };

    private static string DescribeMailGatewayProbeFailure(string? errorCode) => errorCode switch
    {
        "mail.gateway.probe.profile_invalid" or "mail.gateway.probe.base_uri_invalid" =>
            "저장된 메일 중계 서버 설정이 올바르지 않습니다.",
        "mail.gateway.probe.credential_unavailable" or "mail.gateway.probe.credential_invalid" =>
            "저장된 메일 중계 서버 전용 접속 토큰을 사용할 수 없습니다.",
        "mail.gateway.probe.redirect_denied" => "메일 중계 서버가 다른 주소로 이동을 요청해 연결을 허용하지 않았습니다.",
        "mail.gateway.probe.status_rejected" => "메일 중계 서버가 성공 응답을 보내지 않았습니다.",
        "mail.gateway.probe.response_invalid" => "메일 중계 서버의 응답 형식이 예상과 다릅니다.",
        "mail.gateway.probe.response_too_large" => "메일 중계 서버의 확인 응답이 허용 크기를 넘었습니다.",
        "mail.gateway.probe.timeout" => "메일 중계 서버 연결 확인 시간이 초과됐습니다.",
        _ => "메일 중계 서버에 연결하지 못했습니다.",
    };

    private static string DescribeSmtpProbeFailure(string? errorCode) => errorCode switch
    {
        "smtp.probe.profile_invalid" or "smtp.probe.input_invalid" =>
            "저장된 메일 서버 설정이 올바르지 않습니다.",
        "smtp.probe.credential_unavailable" => "저장된 메일 서버 로그인 정보를 사용할 수 없습니다.",
        "smtp.probe.starttls_required" => "메일 서버가 필수 암호화 연결을 제공하지 않았습니다.",
        "smtp.probe.tls_required" => "메일 서버와 암호화 연결을 설정하지 못했습니다.",
        "smtp.probe.auth_not_supported" => "메일 서버가 필요한 로그인 방식을 지원하지 않습니다.",
        "smtp.probe.auth_rejected" => "메일 서버가 저장된 로그인 정보를 거부했습니다.",
        "smtp.probe.cancelled" => "메일 서버 연결 확인이 취소됐습니다.",
        _ => "메일 서버의 암호화 연결 또는 로그인을 확인하지 못했습니다.",
    };

    private sealed record ProfileKindChoice(string Label, ConnectionProfileKind Value);
    private sealed record AuthenticationChoice(string Label, ConnectionAuthenticationKind Value);
    private sealed record SecretMutationChoice(string Label, ConnectionSecretMutation Value);
    private sealed record SmtpTlsModeChoice(string Label, SmtpTlsMode Value);

    private sealed record ConnectionProfileListItem(
        ConnectionProfileKind Kind,
        HttpConnectionProfile? HttpProfile,
        MailGatewayConnectionProfile? MailGatewayProfile,
        SmtpConnectionProfile? SmtpProfile,
        string DisplayName,
        string IdentityText,
        string EndpointText,
        string SecurityText,
        string CredentialText,
        string ReadinessText)
    {
        public ConnectionProfileId Id => HttpProfile?.Id ?? MailGatewayProfile?.Id ?? SmtpProfile!.Id;
        public bool Enabled => HttpProfile?.Enabled ?? MailGatewayProfile?.Enabled ?? SmtpProfile!.Enabled;
        public ConnectionProfileLifecycleState State =>
            HttpProfile?.State ?? MailGatewayProfile?.State ?? SmtpProfile!.State;
        public long Revision => HttpProfile?.Revision ?? MailGatewayProfile?.Revision ?? SmtpProfile!.Revision;
        public bool RequiresCredentialRepair => State != ConnectionProfileLifecycleState.Stable;

        public static ConnectionProfileListItem CreateHttp(
            HttpConnectionProfile profile,
            ConnectionProbeReceipt? probe,
            bool credentialExists)
        {
            var enabled = profile.Enabled ? "사용" : "사용 안 함";
            var authentication = profile.Authentication == ConnectionAuthenticationKind.Bearer
                ? "접속 토큰"
                : "인증 없음";
            var credential = profile.Authentication switch
            {
                _ when profile.State != ConnectionProfileLifecycleState.Stable =>
                    "완료되지 않은 웹 서버 로그인 정보 변경이 있습니다. 저장을 다시 완료해 주세요.",
                ConnectionAuthenticationKind.Bearer when credentialExists =>
                    "웹 서버 접속 토큰이 윈도우 보안 저장소에 저장돼 있습니다. 값은 표시하지 않습니다.",
                ConnectionAuthenticationKind.Bearer => "저장된 웹 서버 접속 토큰이 없습니다.",
                _ => "인증 없음을 사용합니다.",
            };
            return new(
                ConnectionProfileKind.Http,
                profile,
                null,
                null,
                profile.DisplayName,
                $"{profile.Id.Value} · 웹 서버 · {enabled}",
                profile.BaseUrl,
                $"{authentication} · {CredentialSummary(profile.State, credentialExists)}",
                credential,
                Readiness(profile.Enabled, profile.State, probe));
        }

        public static ConnectionProfileListItem CreateMailGateway(
            MailGatewayConnectionProfile profile,
            ConnectionProbeReceipt? probe,
            bool credentialExists)
        {
            var enabled = profile.Enabled ? "사용" : "사용 안 함";
            var credential = profile.State switch
            {
                not ConnectionProfileLifecycleState.Stable =>
                    "완료되지 않은 메일 중계 서버 로그인 정보 변경이 있습니다. 저장을 다시 완료해 주세요.",
                _ when credentialExists =>
                    "메일 중계 서버 전용 접속 토큰이 윈도우 보안 저장소에 저장돼 있습니다. 값은 표시하지 않습니다.",
                _ => "저장된 메일 중계 서버 전용 접속 토큰이 없습니다.",
            };
            return new(
                ConnectionProfileKind.MailGateway,
                null,
                profile,
                null,
                profile.DisplayName,
                $"{profile.Id.Value} · 사내 메일 중계 서버 · {enabled}",
                $"{profile.BaseUrl} · 발신자 {profile.Sender}",
                $"접속 토큰 고정 · {CredentialSummary(profile.State, credentialExists)}",
                credential,
                Readiness(profile.Enabled, profile.State, probe));
        }

        public static ConnectionProfileListItem CreateSmtp(
            SmtpConnectionProfile profile,
            ConnectionProbeReceipt? probe,
            bool credentialExists)
        {
            var enabled = profile.Enabled ? "사용" : "사용 안 함";
            var credential = profile.State switch
            {
                not ConnectionProfileLifecycleState.Stable =>
                    "완료되지 않은 메일 서버 로그인 정보 변경이 있습니다. 저장을 다시 완료해 주세요.",
                _ when credentialExists =>
                    "메일 서버 사용자명과 암호가 윈도우 보안 저장소에 저장돼 있습니다. 값은 표시하지 않습니다.",
                _ => "저장된 메일 서버 로그인 정보가 없습니다.",
            };
            var tls = profile.TlsMode == SmtpTlsMode.StartTlsRequired
                ? "연결 후 암호화 필수"
                : "처음부터 암호화";
            return new(
                ConnectionProfileKind.Smtp,
                null,
                null,
                profile,
                profile.DisplayName,
                $"{profile.Id.Value} · 표준 메일 서버 · {enabled}",
                $"{profile.Host}:{profile.Port} · 발신자 {profile.Sender}",
                $"{tls} · 사용자명과 암호로 로그인 · 윈도우 인증서 확인 · {CredentialSummary(profile.State, credentialExists)}",
                credential,
                Readiness(profile.Enabled, profile.State, probe));
        }

        private static string CredentialSummary(
            ConnectionProfileLifecycleState state,
            bool credentialExists) => state != ConnectionProfileLifecycleState.Stable
            ? "자격 증명 변경 미완료"
            : credentialExists
                ? "자격 증명 저장됨"
                : "저장된 자격 증명 없음";

        private static string Readiness(
            bool enabled,
            ConnectionProfileLifecycleState state,
            ConnectionProbeReceipt? probe) => (enabled, state, probe) switch
            {
                (false, _, _) => "사용 안 함",
                (_, not ConnectionProfileLifecycleState.Stable, _) => "저장 상태 확인 필요",
                (_, _, null) => "연결 확인 필요",
                (_, _, { Succeeded: true }) => $"연결 확인됨 · {FormatTime(probe.CheckedAtUtc)}",
                _ => $"연결 실패 · {FormatTime(probe!.CheckedAtUtc)}",
            };

        private static string FormatTime(DateTimeOffset value) =>
            value.ToLocalTime().ToString("yyyy.MM.dd HH:mm", CultureInfo.CurrentCulture);
    }

    private sealed class ConnectionProfileListItemComparer : IComparer<ConnectionProfileListItem>
    {
        public static ConnectionProfileListItemComparer Instance { get; } = new();

        public int Compare(ConnectionProfileListItem? left, ConnectionProfileListItem? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var displayName = StringComparer.CurrentCultureIgnoreCase.Compare(
                left.DisplayName,
                right.DisplayName);
            if (displayName != 0) return displayName;
            var kind = left.Kind.CompareTo(right.Kind);
            return kind != 0
                ? kind
                : StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
        }
    }
}
