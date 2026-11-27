using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LIGClaw.Application.Capabilities;
using LIGClaw.Application.Connections;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Desktop.Infrastructure.Tools;
using LIGClaw.Domain;

namespace LIGClaw.Desktop.Tests;

public sealed class UiConstructionTests
{
    [Fact]
    public void CriticalUiConstructsAndLaysOutOnAnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            App? app = null;
            MainWindow? mainWindow = null;
            ToolApprovalWindow? window = null;
            ToolApprovalWindow? mailWindow = null;
            SensitiveContextPreviewWindow? sensitiveWindow = null;
            SensitiveContextPreviewWindow? invalidSensitiveWindow = null;
            PreparedSensitiveContextStore? sensitiveStore = null;
            PreparedSensitiveContext? sensitiveContext = null;
            PreparedSensitiveContext? invalidSensitiveContext = null;
            byte[]? approvedOcr = null;
            DiagnosticsWindow? diagnosticsWindow = null;
            CapabilityReviewWindow? capabilityReviewWindow = null;
            CapabilityReviewWindow? connectionCapabilityReviewWindow = null;
            CapabilityReviewWindow? connectionRollbackReviewWindow = null;
            CapabilityReviewWindow? mixedConnectionCapabilityReviewWindow = null;
            CapabilityReviewWindow? smtpConnectionCapabilityReviewWindow = null;
            try
            {
                app = new App();
                app.InitializeComponent();
                mainWindow = new MainWindow(new NoOpNotificationService());
                var builderOpenControllerField = typeof(MainWindow).GetField(
                    "_capabilityBuilderOpenController",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(builderOpenControllerField);
                Assert.IsType<CapabilityBuilderOpenController>(
                    builderOpenControllerField.GetValue(mainWindow));
                var mainContent = Assert.IsAssignableFrom<FrameworkElement>(mainWindow.Content);
                mainContent.Measure(new Size(1060, 650));
                mainContent.Arrange(new Rect(0, 0, 1060, 650));
                mainContent.UpdateLayout();
                Assert.True(mainContent.DesiredSize.Width > 0);
                mainWindow.Width = 760;
                mainWindow.Height = 500;
                mainWindow.Show();
                mainWindow.UpdateLayout();
                foreach (var navigation in new[]
                         {
                             mainWindow.HomeNavigationButton,
                             mainWindow.ActivityNavigationButton,
                             mainWindow.CapabilitiesNavigationButton,
                             mainWindow.ConnectionsNavigationButton,
                             mainWindow.MemoryNavigationButton,
                             mainWindow.ScheduleNavigationButton,
                             mainWindow.AgentJobsNavigationButton,
                             mainWindow.SettingsNavigationButton,
                         })
                {
                    var bottom = navigation.TranslatePoint(
                        new Point(0, navigation.ActualHeight),
                        mainWindow.SidebarChrome).Y;
                    Assert.InRange(bottom, 0, mainWindow.SidebarChrome.ActualHeight);
                }
                Assert.True(mainWindow.MakeCapabilityButton.Focusable);
                var capabilityButtonTopLeft = mainWindow.MakeCapabilityButton.TranslatePoint(
                    new Point(0, 0),
                    mainWindow.ComposerHost);
                Assert.InRange(capabilityButtonTopLeft.X, 0, mainWindow.ComposerHost.ActualWidth);
                Assert.InRange(
                    capabilityButtonTopLeft.X + mainWindow.MakeCapabilityButton.ActualWidth,
                    0,
                    mainWindow.ComposerHost.ActualWidth);
                Assert.InRange(
                    capabilityButtonTopLeft.Y + mainWindow.MakeCapabilityButton.ActualHeight,
                    0,
                    mainWindow.ComposerHost.ActualHeight);

                mainWindow.ConversationInput.Clear();
                mainWindow.MakeCapabilityButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("기능으로 만들 요청을 먼저 입력해 주세요.", mainWindow.RunStatus.Text);
                var oversizedRequirement = new string('가', FeatureBuilderPolicy.MaximumRequirementCharacters + 1);
                mainWindow.ConversationInput.Text = oversizedRequirement;
                mainWindow.MakeCapabilityButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("8,192자", mainWindow.RunStatus.Text, StringComparison.Ordinal);
                Assert.Equal(oversizedRequirement, mainWindow.ConversationInput.Text);
                const string secretRequirement = "메일 token=ordinary-secret-value 사용";
                mainWindow.ConversationInput.Text = secretRequirement;
                mainWindow.MakeCapabilityButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("비밀값처럼 보이는 내용을 제거", mainWindow.RunStatus.Text, StringComparison.Ordinal);
                Assert.Equal(secretRequirement, mainWindow.ConversationInput.Text);
                mainWindow.ConversationInput.Clear();

                var management = new EmptyCapabilityManagementService();
                var capabilitiesPage = new CapabilitiesPage(management);
                capabilitiesPage.Measure(new Size(688, 420));
                capabilitiesPage.Arrange(new Rect(0, 0, 688, 420));
                capabilitiesPage.UpdateLayout();
                Assert.InRange(capabilitiesPage.DesiredSize.Width, 1, 688);
                Assert.Equal(
                    "기능 상태 필터",
                    System.Windows.Automation.AutomationProperties.GetName(capabilitiesPage.CapabilityFilterBox));
                var builderRequested = false;
                capabilitiesPage.BuilderRequested += (_, _) => builderRequested = true;
                capabilitiesPage.BuilderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(builderRequested);

                var featureBuilderPage = new FeatureBuilderPage(
                    new EmptyFeatureBuilderManagementService(),
                    _ => Task.FromResult(new FeatureBuilderInventory([], ["http"], [])),
                    (_, _) => Task.FromResult(new FeatureBuilderManagementResult(
                        FeatureBuilderManagementOutcome.NotReady)));
                featureBuilderPage.Measure(new Size(688, 420));
                featureBuilderPage.Arrange(new Rect(0, 0, 688, 420));
                featureBuilderPage.UpdateLayout();
                Assert.InRange(featureBuilderPage.DesiredSize.Width, 1, 688);
                Assert.InRange(featureBuilderPage.DesiredSize.Height, 1, 420);
                Assert.Equal(8_192, featureBuilderPage.RequirementTextBox.MaxLength);
                Assert.False(featureBuilderPage.ApplyButton.IsEnabled);
                Assert.Equal(
                    "새 기능 요구사항",
                    System.Windows.Automation.AutomationProperties.GetName(
                        featureBuilderPage.RequirementTextBox));
                var builderScroll = Assert.IsType<ScrollViewer>(featureBuilderPage.Content);
                Assert.Equal(ScrollBarVisibility.Auto, builderScroll.VerticalScrollBarVisibility);
                var backRequested = false;
                featureBuilderPage.BackRequested += (_, _) => backRequested = true;
                featureBuilderPage.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(backRequested);

                const string prefilledRequirement = "파일 메타데이터를 확인하고 로컬 알림으로 알려 줘.";
                var prefilledBuilderPage = new FeatureBuilderPage(
                    new EmptyFeatureBuilderManagementService(),
                    _ => Task.FromResult(new FeatureBuilderInventory([], [], [])),
                    (_, _) => Task.FromResult(new FeatureBuilderManagementResult(
                        FeatureBuilderManagementOutcome.NotReady)),
                    prefilledRequirement);
                Assert.Equal(prefilledRequirement, prefilledBuilderPage.RequirementTextBox.Text);
                Assert.True(prefilledBuilderPage.GenerateButton.IsEnabled);
                Assert.False(prefilledBuilderPage.ApplyButton.IsEnabled);
                Assert.Contains("직접 초안 생성을 눌러", prefilledBuilderPage.PageStatusText.Text, StringComparison.Ordinal);

                var invalidPrefilledBuilderPage = new FeatureBuilderPage(
                    new EmptyFeatureBuilderManagementService(),
                    _ => Task.FromResult(new FeatureBuilderInventory([], [], [])),
                    (_, _) => Task.FromResult(new FeatureBuilderManagementResult(
                        FeatureBuilderManagementOutcome.NotReady)),
                    oversizedRequirement);
                Assert.Empty(invalidPrefilledBuilderPage.RequirementTextBox.Text);
                Assert.Contains("8,192자", invalidPrefilledBuilderPage.PageStatusText.Text, StringComparison.Ordinal);

                var readyDraft = CreateCapabilityReview().Draft;
                var readyDraftManagement = new ReadyDraftFeatureBuilderManagementService(readyDraft);
                var readyDraftPage = new FeatureBuilderPage(
                    readyDraftManagement,
                    _ => Task.FromResult(new FeatureBuilderInventory([], [], [])),
                    (proposal, _) => Task.FromResult(new FeatureBuilderManagementResult(
                        FeatureBuilderManagementOutcome.Succeeded,
                        TestRun: new FeatureBuilderTestRun(
                            $"fbtest_{new string('a', 32)}",
                            proposal.ProposalId,
                            FeatureBuilderTestRunKind.DryRun,
                            1,
                            proposal.CandidateContentHash!,
                            "ui-navigation-v1",
                            FeatureBuilderTestRunState.Passed,
                            1,
                            "{}",
                            null,
                            DateTimeOffset.UnixEpoch,
                            DateTimeOffset.UnixEpoch))));
                CapabilityVersionRef? requestedReviewVersion = null;
                var readyDraftRequestCount = 0;
                readyDraftPage.DraftReadyForReview += (_, exactVersion) =>
                {
                    requestedReviewVersion = exactVersion;
                    readyDraftRequestCount++;
                };
                readyDraftPage.ApplyAsync().GetAwaiter().GetResult();
                Assert.Equal(0, readyDraftRequestCount);
                readyDraftPage.RequirementTextBox.Text = "로컬 상태를 안전하게 확인하는 기능을 만들어 줘.";
                readyDraftPage.StartGenerationAsync().GetAwaiter().GetResult();
                readyDraftPage.RunTestsAsync().GetAwaiter().GetResult();
                readyDraftPage.ApplyAsync().GetAwaiter().GetResult();
                Assert.Equal(1, readyDraftRequestCount);
                Assert.Equal(readyDraft.ExactVersion, requestedReviewVersion);

                var connectionRepository = new EmptyConnectionProfileRepository();
                var connectionCredentials = new EmptyConnectionCredentialStore();
                var connectionsPage = new ConnectionsPage(
                    new ConnectionManagementService(connectionRepository, connectionCredentials),
                    connectionRepository,
                    connectionCredentials);
                connectionsPage.Measure(new Size(688, 420));
                connectionsPage.Arrange(new Rect(0, 0, 688, 420));
                connectionsPage.UpdateLayout();
                Assert.InRange(connectionsPage.DesiredSize.Width, 1, 688);
                Assert.True(connectionsPage.ProfileIdTextBox.IsReadOnly);
                Assert.StartsWith("connection-", connectionsPage.ProfileIdTextBox.Text, StringComparison.Ordinal);
                connectionsPage.AuthenticationBox.SelectedValue = ConnectionAuthenticationKind.Bearer;
                connectionsPage.SecretMutationBox.SelectedValue = ConnectionSecretMutation.Replace;
                connectionsPage.DisplayNameTextBox.Text = "폐쇄망 API";
                connectionsPage.BaseUrlTextBox.Text = "https://internal.example.test";
                connectionsPage.BearerTokenBox.Password = "must-not-remain-in-ui";
                connectionsPage.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Empty(connectionsPage.BearerTokenBox.Password);

                var mailGatewayRepository = new EmptyMailGatewayConnectionRepository();
                var smtpRepository = new EmptySmtpConnectionRepository();
                var mailConnectionsPage = new ConnectionsPage(
                    new ConnectionManagementService(connectionRepository, connectionCredentials),
                    connectionRepository,
                    connectionCredentials,
                    new MailGatewayConnectionManagementService(
                        mailGatewayRepository,
                        connectionCredentials),
                    mailGatewayRepository,
                    new SmtpConnectionManagementService(
                        smtpRepository,
                        connectionCredentials),
                    smtpRepository);
                mailConnectionsPage.Measure(new Size(688, 420));
                mailConnectionsPage.Arrange(new Rect(0, 0, 688, 420));
                mailConnectionsPage.UpdateLayout();
                Assert.Equal(3, mailConnectionsPage.ProfileKindBox.Items.Count);
                mailConnectionsPage.ProfileKindBox.SelectedValue = ConnectionProfileKind.MailGateway;
                Assert.Equal(Visibility.Visible, mailConnectionsPage.SenderPanel.Visibility);
                Assert.Equal(
                    ConnectionAuthenticationKind.Bearer,
                    mailConnectionsPage.AuthenticationBox.SelectedValue);
                Assert.False(mailConnectionsPage.AuthenticationBox.IsEnabled);
                Assert.True(mailConnectionsPage.ProfileKindBox.IsEnabled);
                Assert.Equal(254, mailConnectionsPage.SenderTextBox.MaxLength);
                Assert.Equal(
                    "메일 중계 서버 실제 연결 확인",
                    System.Windows.Automation.AutomationProperties.GetName(
                        mailConnectionsPage.ProbeButton));
                mailConnectionsPage.DisplayNameTextBox.Text = "사내 메일 Gateway";
                mailConnectionsPage.BaseUrlTextBox.Text = "https://mail.example.test/";
                mailConnectionsPage.SenderTextBox.Text = "assistant@example.test";
                mailConnectionsPage.BearerTokenBox.Password = "must-not-remain-in-mail-ui";
                mailConnectionsPage.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Empty(mailConnectionsPage.BearerTokenBox.Password);

                mailConnectionsPage.ProfileKindBox.SelectedValue = ConnectionProfileKind.Smtp;
                Assert.Equal(Visibility.Visible, mailConnectionsPage.SmtpEndpointPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, mailConnectionsPage.BaseUrlPanel.Visibility);
                Assert.Equal(Visibility.Visible, mailConnectionsPage.SmtpCredentialPanel.Visibility);
                Assert.Equal(Visibility.Visible, mailConnectionsPage.SmtpSecurityPanel.Visibility);
                Assert.False(mailConnectionsPage.AuthenticationBox.IsEnabled);
                Assert.Equal(SmtpTlsMode.StartTlsRequired, mailConnectionsPage.SmtpTlsModeBox.SelectedValue);
                Assert.Equal(
                    "메일 서버 암호화 연결과 로그인 확인",
                    System.Windows.Automation.AutomationProperties.GetName(
                        mailConnectionsPage.ProbeButton));
                mailConnectionsPage.DisplayNameTextBox.Text = "사내 SMTP";
                mailConnectionsPage.SmtpHostTextBox.Text = "smtp.example.test";
                mailConnectionsPage.SmtpPortTextBox.Text = "587";
                mailConnectionsPage.SenderTextBox.Text = "assistant@example.test";
                mailConnectionsPage.SmtpUsernameBox.Password = "must-not-remain-smtp-user";
                mailConnectionsPage.SmtpPasswordBox.Password = "must-not-remain-smtp-password";
                mailConnectionsPage.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Empty(mailConnectionsPage.SmtpUsernameBox.Password);
                Assert.Empty(mailConnectionsPage.SmtpPasswordBox.Password);

                capabilityReviewWindow = new CapabilityReviewWindow(
                    management,
                    CreateCapabilityReview());
                var capabilityReviewContent = Assert.IsAssignableFrom<FrameworkElement>(capabilityReviewWindow.Content);
                capabilityReviewContent.Measure(new Size(700, 580));
                capabilityReviewContent.Arrange(new Rect(0, 0, 700, 580));
                capabilityReviewContent.UpdateLayout();
                Assert.True(capabilityReviewWindow.CloseButton.IsDefault);
                Assert.Equal(Visibility.Visible, capabilityReviewWindow.ActivateButton.Visibility);

                var connectionReview = CreateConnectionCapabilityReview();
                var reviewRepository = new ReviewConnectionProfileRepository();
                var reviewCredentials = new EmptyConnectionCredentialStore();
                var firstProfile = CreateHttpProfile("review-api", "검토 API", "https://review.example.test/");
                var secondProfile = CreateHttpProfile("backup-api", "대체 API", "https://backup.example.test/");
                reviewRepository.Profiles[firstProfile.Id] = firstProfile;
                reviewRepository.Profiles[secondProfile.Id] = secondProfile;
                reviewRepository.Probes[firstProfile.Id] = SuccessfulProbe(firstProfile);
                reviewRepository.Probes[secondProfile.Id] = SuccessfulProbe(secondProfile);
                var bindingManagement = new ConnectionManagementService(reviewRepository, reviewCredentials);
                connectionCapabilityReviewWindow = new CapabilityReviewWindow(
                    management,
                    connectionReview,
                    bindingManagement,
                    reviewRepository,
                    new ConnectionReadinessResolver(reviewRepository, reviewCredentials));
                connectionCapabilityReviewWindow.RefreshConnectionBindingsAsync().GetAwaiter().GetResult();
                var connectionReviewContent = Assert.IsAssignableFrom<FrameworkElement>(
                    connectionCapabilityReviewWindow.Content);
                connectionReviewContent.Measure(new Size(620, 500));
                connectionReviewContent.Arrange(new Rect(0, 0, 620, 500));
                connectionReviewContent.UpdateLayout();
                Assert.InRange(connectionReviewContent.DesiredSize.Width, 1, 620);
                Assert.Equal(Visibility.Visible, connectionCapabilityReviewWindow.ConnectionBindingSection.Visibility);
                Assert.False(connectionCapabilityReviewWindow.ActivateButton.IsEnabled);
                var unboundRow = Assert.Single(
                    connectionCapabilityReviewWindow.ConnectionBindingsList.Items.Cast<CapabilityConnectionBindingRow>());
                unboundRow.SelectedProfile = Assert.Single(
                    unboundRow.Profiles,
                    choice => choice.ProfileId.Equals(firstProfile.Id));
                connectionCapabilityReviewWindow.SaveConnectionBindingAsync(unboundRow).GetAwaiter().GetResult();
                Assert.True(connectionCapabilityReviewWindow.ActivateButton.IsEnabled);
                var boundRow = Assert.Single(
                    connectionCapabilityReviewWindow.ConnectionBindingsList.Items.Cast<CapabilityConnectionBindingRow>());
                Assert.Equal(1, boundRow.BindingRevision);
                Assert.Contains(firstProfile.Id.Value, boundRow.SelectedProfile?.DisplayText, StringComparison.Ordinal);
                Assert.Contains(firstProfile.BaseUrl, boundRow.SelectedProfile?.DisplayText, StringComparison.Ordinal);
                var backupChoice = Assert.Single(
                    boundRow.Profiles,
                    choice => choice.ProfileId.Equals(secondProfile.Id));
                var persistedChoice = Assert.IsType<LocalConnectionProfileChoice>(boundRow.SelectedProfile);
                connectionCapabilityReviewWindow.ApplyConnectionProfileSelection(boundRow, backupChoice);
                Assert.False(connectionCapabilityReviewWindow.ActivateButton.IsEnabled);
                connectionCapabilityReviewWindow.ApplyConnectionProfileSelection(boundRow, persistedChoice);
                Assert.True(connectionCapabilityReviewWindow.ActivateButton.IsEnabled);

                var externalRebind = bindingManagement.BindAsync(
                    connectionReview.Draft.ExactVersion,
                    "primary",
                    "http",
                    secondProfile.Id,
                    boundRow.BindingRevision,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.Equal(ConnectionManagementOutcome.Succeeded, externalRebind.Outcome);
                Assert.False(connectionCapabilityReviewWindow.RevalidateConnectionsBeforeMutationAsync()
                    .GetAwaiter().GetResult());
                Assert.Contains("연결 선택이 변경", connectionCapabilityReviewWindow.ConnectionBindingStatusText.Text,
                    StringComparison.Ordinal);
                connectionCapabilityReviewWindow.SaveConnectionBindingAsync(boundRow).GetAwaiter().GetResult();
                var reloadedRow = Assert.Single(
                    connectionCapabilityReviewWindow.ConnectionBindingsList.Items.Cast<CapabilityConnectionBindingRow>());
                Assert.Equal(2, reloadedRow.BindingRevision);
                Assert.Equal(secondProfile.Id, reloadedRow.SelectedProfile?.ProfileId);
                Assert.Contains("최신 상태를 다시 불러왔습니다", connectionCapabilityReviewWindow.ConnectionBindingStatusText.Text,
                    StringComparison.Ordinal);
                Assert.True(connectionCapabilityReviewWindow.ActivateButton.IsEnabled);

                connectionRollbackReviewWindow = new CapabilityReviewWindow(
                    management,
                    CreateConnectionRollbackReview(connectionReview),
                    bindingManagement,
                    reviewRepository,
                    new ConnectionReadinessResolver(reviewRepository, reviewCredentials));
                connectionRollbackReviewWindow.RefreshConnectionBindingsAsync().GetAwaiter().GetResult();
                Assert.Equal(Visibility.Visible, connectionRollbackReviewWindow.ConnectionBindingSection.Visibility);
                Assert.Equal(Visibility.Visible, connectionRollbackReviewWindow.RollbackButton.Visibility);
                Assert.True(connectionRollbackReviewWindow.RollbackButton.IsEnabled);

                var mixedReview = CreateMixedConnectionCapabilityReview();
                var mixedHttpRepository = new ReviewConnectionProfileRepository();
                var mixedMailRepository = new ReviewMailGatewayConnectionRepository();
                var mixedCredentials = new ReviewConnectionCredentialStore();
                var mixedHttpProfile = CreateHttpProfile(
                    "mixed-http",
                    "혼합 조회 API",
                    "https://mixed-http.example.test/");
                var mixedMailProfile = CreateMailGatewayProfile(
                    "mixed-mail",
                    "운영 Mail Gateway",
                    "https://mail.example.test/",
                    "assistant@example.test");
                var backupMailProfile = CreateMailGatewayProfile(
                    "mixed-mail-backup",
                    "대체 Mail Gateway",
                    "https://mail-backup.example.test/",
                    "backup@example.test");
                mixedHttpRepository.Profiles[mixedHttpProfile.Id] = mixedHttpProfile;
                mixedHttpRepository.Probes[mixedHttpProfile.Id] = SuccessfulProbe(mixedHttpProfile);
                mixedMailRepository.Profiles[mixedMailProfile.Id] = mixedMailProfile;
                mixedMailRepository.Profiles[backupMailProfile.Id] = backupMailProfile;
                mixedMailRepository.Probes[mixedMailProfile.Id] = SuccessfulProbe(mixedMailProfile);
                mixedMailRepository.Probes[backupMailProfile.Id] = SuccessfulProbe(backupMailProfile);
                mixedCredentials.Existing.Add(
                    (mixedMailProfile.Id, ConnectionCredentialKind.MailGatewayBearerToken));
                mixedCredentials.Existing.Add(
                    (backupMailProfile.Id, ConnectionCredentialKind.MailGatewayBearerToken));
                var mixedHttpManagement = new ConnectionManagementService(
                    mixedHttpRepository,
                    mixedCredentials);
                var mixedMailManagement = new MailGatewayConnectionManagementService(
                    mixedMailRepository,
                    mixedCredentials);
                var mixedReadiness = new CompositeConnectionReadinessResolver(
                    new ConnectionReadinessResolver(mixedHttpRepository, mixedCredentials),
                    new MailGatewayConnectionReadinessResolver(mixedMailRepository, mixedCredentials));
                var mixedCapabilitiesPage = new CapabilitiesPage(
                    management,
                    mixedHttpManagement,
                    mixedHttpRepository,
                    mixedReadiness,
                    mixedMailManagement,
                    mixedMailRepository);
                Assert.NotNull(mixedCapabilitiesPage);
                mixedConnectionCapabilityReviewWindow = new CapabilityReviewWindow(
                    management,
                    mixedReview,
                    mixedHttpManagement,
                    mixedHttpRepository,
                    mixedReadiness,
                    mixedMailManagement,
                    mixedMailRepository);
                mixedConnectionCapabilityReviewWindow.RefreshConnectionBindingsAsync().GetAwaiter().GetResult();
                var mixedReviewContent = Assert.IsAssignableFrom<FrameworkElement>(
                    mixedConnectionCapabilityReviewWindow.Content);
                mixedReviewContent.Measure(new Size(620, 500));
                mixedReviewContent.Arrange(new Rect(0, 0, 620, 500));
                mixedReviewContent.UpdateLayout();
                var mixedRows = mixedConnectionCapabilityReviewWindow.ConnectionBindingsList.Items
                    .Cast<CapabilityConnectionBindingRow>()
                    .ToArray();
                var mixedHttpRow = Assert.Single(
                    mixedRows,
                    row => row.Kind == CapabilityConnectionKindPolicy.Http);
                var mixedMailRow = Assert.Single(
                    mixedRows,
                    row => row.Kind == CapabilityConnectionKindPolicy.MailApi);
                Assert.All(mixedHttpRow.Profiles, choice =>
                    Assert.Equal(CapabilityConnectionKindPolicy.Http, choice.Kind));
                Assert.All(mixedMailRow.Profiles, choice =>
                    Assert.Equal(CapabilityConnectionKindPolicy.MailApi, choice.Kind));
                Assert.Single(mixedHttpRow.Profiles);
                Assert.Equal(2, mixedMailRow.Profiles.Count);
                var mixedMailChoice = Assert.Single(
                    mixedMailRow.Profiles,
                    choice => choice.ProfileId.Equals(mixedMailProfile.Id));
                Assert.Contains(mixedMailProfile.BaseUrl, mixedMailChoice.DisplayText, StringComparison.Ordinal);
                Assert.Contains(mixedMailProfile.Sender, mixedMailChoice.DisplayText, StringComparison.Ordinal);
                Assert.DoesNotContain(mixedMailProfile.Sender, mixedHttpRow.Profiles[0].DisplayText,
                    StringComparison.Ordinal);
                mixedConnectionCapabilityReviewWindow.ApplyConnectionProfileSelection(
                    mixedMailRow,
                    Assert.Single(mixedHttpRow.Profiles));
                Assert.Null(mixedMailRow.SelectedProfile);

                mixedHttpRow.SelectedProfile = Assert.Single(mixedHttpRow.Profiles);
                mixedConnectionCapabilityReviewWindow.SaveConnectionBindingAsync(mixedHttpRow)
                    .GetAwaiter().GetResult();
                Assert.False(mixedConnectionCapabilityReviewWindow.ActivateButton.IsEnabled);
                mixedMailRow = Assert.Single(
                    mixedConnectionCapabilityReviewWindow.ConnectionBindingsList.Items
                        .Cast<CapabilityConnectionBindingRow>(),
                    row => row.Kind == CapabilityConnectionKindPolicy.MailApi);
                mixedMailRow.SelectedProfile = Assert.Single(
                    mixedMailRow.Profiles,
                    choice => choice.ProfileId.Equals(mixedMailProfile.Id));
                mixedConnectionCapabilityReviewWindow.SaveConnectionBindingAsync(mixedMailRow)
                    .GetAwaiter().GetResult();
                Assert.True(mixedConnectionCapabilityReviewWindow.ActivateButton.IsEnabled);
                Assert.Equal(
                    CapabilityConnectionKindPolicy.Http,
                    mixedHttpRepository.Bindings[(mixedReview.Draft.ExactVersion, "primary")].Kind);
                Assert.Equal(
                    CapabilityConnectionKindPolicy.MailApi,
                    mixedMailRepository.Bindings[(mixedReview.Draft.ExactVersion, "mail")].Kind);
                Assert.True(mixedConnectionCapabilityReviewWindow.RevalidateConnectionsBeforeMutationAsync()
                    .GetAwaiter().GetResult());

                var externalMailRebind = mixedMailManagement.BindAsync(
                    mixedReview.Draft.ExactVersion,
                    "mail",
                    backupMailProfile.Id,
                    1,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert.Equal(ConnectionManagementOutcome.Succeeded, externalMailRebind.Outcome);
                Assert.False(mixedConnectionCapabilityReviewWindow.RevalidateConnectionsBeforeMutationAsync()
                    .GetAwaiter().GetResult());
                Assert.Contains(
                    "연결 선택이 변경",
                    mixedConnectionCapabilityReviewWindow.ConnectionBindingStatusText.Text,
                    StringComparison.Ordinal);
                mixedMailRow = Assert.Single(
                    mixedConnectionCapabilityReviewWindow.ConnectionBindingsList.Items
                        .Cast<CapabilityConnectionBindingRow>(),
                    row => row.Kind == CapabilityConnectionKindPolicy.MailApi);
                Assert.Equal(backupMailProfile.Id, mixedMailRow.SelectedProfile?.ProfileId);

                var smtpReview = CreateSmtpConnectionCapabilityReview();
                var reviewSmtpRepository = new ReviewSmtpConnectionRepository();
                var reviewSmtpProfile = CreateSmtpProfile(
                    "review-smtp",
                    "검토 SMTP",
                    "smtp.review.example.test",
                    587,
                    SmtpTlsMode.StartTlsRequired,
                    "assistant@example.test");
                reviewSmtpRepository.Profiles[reviewSmtpProfile.Id] = reviewSmtpProfile;
                reviewSmtpRepository.Probes[reviewSmtpProfile.Id] = SuccessfulProbe(reviewSmtpProfile);
                mixedCredentials.Existing.Add(
                    (reviewSmtpProfile.Id, ConnectionCredentialKind.SmtpAuth));
                var reviewSmtpManagement = new SmtpConnectionManagementService(
                    reviewSmtpRepository,
                    mixedCredentials);
                var reviewSmtpReadiness = new SmtpConnectionReadinessResolver(
                    reviewSmtpRepository,
                    mixedCredentials);
                var smtpCapabilitiesPage = new CapabilitiesPage(
                    management,
                    connectionReadiness: reviewSmtpReadiness,
                    smtpConnectionManagement: reviewSmtpManagement,
                    smtpConnectionProfiles: reviewSmtpRepository);
                Assert.NotNull(smtpCapabilitiesPage);
                smtpConnectionCapabilityReviewWindow = new CapabilityReviewWindow(
                    management,
                    smtpReview,
                    null,
                    null,
                    reviewSmtpReadiness,
                    null,
                    null,
                    reviewSmtpManagement,
                    reviewSmtpRepository);
                smtpConnectionCapabilityReviewWindow.RefreshConnectionBindingsAsync().GetAwaiter().GetResult();
                var smtpRow = Assert.Single(
                    smtpConnectionCapabilityReviewWindow.ConnectionBindingsList.Items
                        .Cast<CapabilityConnectionBindingRow>());
                Assert.Equal(CapabilityConnectionKindPolicy.Smtp, smtpRow.Kind);
                var smtpChoice = Assert.Single(smtpRow.Profiles);
                Assert.Contains("smtp.review.example.test:587", smtpChoice.DisplayText, StringComparison.Ordinal);
                Assert.Contains("연결 후 암호화 필수", smtpChoice.DisplayText, StringComparison.Ordinal);
                Assert.Contains("사용자명과 암호로 로그인 · 윈도우 인증서 확인", smtpChoice.DisplayText, StringComparison.Ordinal);
                Assert.Contains(reviewSmtpProfile.Sender, smtpChoice.DisplayText, StringComparison.Ordinal);
                smtpRow.SelectedProfile = smtpChoice;
                smtpConnectionCapabilityReviewWindow.SaveConnectionBindingAsync(smtpRow)
                    .GetAwaiter().GetResult();
                Assert.True(smtpConnectionCapabilityReviewWindow.ActivateButton.IsEnabled);
                Assert.Equal(
                    CapabilityConnectionKindPolicy.Smtp,
                    reviewSmtpRepository.Bindings[(smtpReview.Draft.ExactVersion, "mail")].Kind);

                window = new ToolApprovalWindow(new WindowsToolApprovalPrompt(
                    "파일을 이동할까요?",
                    "보고서 파일을 보관 폴더로 이동합니다.",
                    "대상 C:\\Work\\report.txt → C:\\Archive\\report.txt",
                    "R2",
                    CanUndo: true,
                    GrantScope: "fixture:scope"),
                    allowAlways: true);

                var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                content.Measure(new Size(500, 640));
                content.Arrange(new Rect(0, 0, 500, Math.Max(300, content.DesiredSize.Height)));
                content.UpdateLayout();

                Assert.Equal("동작 승인", window.Title);
                Assert.True(content.DesiredSize.Width > 0);
                Assert.True(content.DesiredSize.Height > 0);
                Assert.Equal(Visibility.Visible, window.AllowAlwaysButton.Visibility);
                Assert.Equal(ScrollBarVisibility.Auto, window.DetailsScrollViewer.VerticalScrollBarVisibility);
                Assert.True(window.DetailsScrollViewer.Focusable);
                Assert.True(window.DetailsScrollViewer.IsTabStop);

                var mailBody = new string('가', 8_192);
                var mailDetails =
                    "연결: 폐쇄망 Mail Gateway\n" +
                    "보낸 사람: assistant@example.test\n" +
                    "받는 사람: reviewer@example.test\n" +
                    "참조: audit@example.test\n" +
                    "제목: 전체 메일 승인 미리보기\n\n" +
                    $"본문:\n{mailBody}";
                mailWindow = new ToolApprovalWindow(new WindowsToolApprovalPrompt(
                    "메일을 전송할까요?",
                    "표시된 수신자에게 이 메일을 한 번 전송합니다.",
                    mailDetails,
                    "R3"));
                var mailContent = Assert.IsAssignableFrom<FrameworkElement>(mailWindow.Content);
                mailContent.Measure(new Size(mailWindow.Width, mailWindow.Height));
                mailContent.Arrange(new Rect(0, 0, mailWindow.Width, mailWindow.Height));
                mailContent.UpdateLayout();

                Assert.InRange(mailWindow.Width, 1, 760);
                Assert.InRange(mailWindow.Height, 1, 500);
                Assert.Equal(760d, mailWindow.MaxWidth);
                Assert.Equal(500d, mailWindow.MaxHeight);
                Assert.Equal(Visibility.Collapsed, mailWindow.AllowConversationButton.Visibility);
                Assert.Equal(Visibility.Collapsed, mailWindow.AllowAlwaysButton.Visibility);
                Assert.True(mailWindow.DetailsScrollViewer.ExtentHeight >
                            mailWindow.DetailsScrollViewer.ViewportHeight);
                Assert.True(mailWindow.DetailsScrollViewer.ScrollableHeight > 0);
                Assert.True(mailWindow.DetailsScrollViewer.Focusable);
                Assert.True(mailWindow.DetailsScrollViewer.IsTabStop);
                Assert.Equal(
                    "승인할 동작 전체 미리보기",
                    System.Windows.Automation.AutomationProperties.GetName(mailWindow.DetailsScrollViewer));
                Assert.Contains(
                    "Page Down",
                    System.Windows.Automation.AutomationProperties.GetHelpText(mailWindow.DetailsScrollViewer),
                    StringComparison.Ordinal);
                Assert.Equal(mailDetails, mailWindow.DetailsText.Text);
                Assert.Equal(mailDetails, new TextBlockAutomationPeer(mailWindow.DetailsText).GetName());

                sensitiveStore = new PreparedSensitiveContextStore();
                var identity = new PreparedSensitiveContextIdentity("conversation", "run", "tool-call");
                var image = CreatePng(16, 16);
                var ocr = Encoding.UTF8.GetBytes("비밀 공개 문장");
                var prepared = sensitiveStore.Prepare(identity, new PreparedSensitiveContextDraft(
                    "window", 16, 16, image, ocr, 0));
                Assert.True(prepared.Success, prepared.Error);
                sensitiveContext = Assert.IsType<PreparedSensitiveContext>(
                    sensitiveStore.Take(identity, prepared.Token!).Context);
                sensitiveWindow = new SensitiveContextPreviewWindow(
                    sensitiveContext,
                    new StubScreenTextRecognizer("비밀 공개 문장"));
                sensitiveContext = null;
                var sensitiveContent = Assert.IsAssignableFrom<FrameworkElement>(sensitiveWindow.Content);
                sensitiveContent.Measure(new Size(920, 700));
                sensitiveContent.Arrange(new Rect(0, 0, 920, 700));
                sensitiveContent.UpdateLayout();
                Assert.NotNull(sensitiveWindow.PreviewImage.Source);
                Assert.True(sensitiveWindow.ApproveButton.IsEnabled);
                Assert.True(sensitiveWindow.ApplyCropAsync(new PixelCropRectangle(0, 0, 8, 8)).GetAwaiter().GetResult());
                var croppedSource = Assert.IsAssignableFrom<BitmapSource>(sensitiveWindow.PreviewImage.Source);
                Assert.Equal(8, croppedSource.PixelWidth);
                Assert.Equal(8, croppedSource.PixelHeight);
                sensitiveWindow.OcrTextBox.Select(0, 2);
                sensitiveWindow.MaskSelectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("[가림] 공개 문장", sensitiveWindow.OcrTextBox.Text);
                approvedOcr = sensitiveWindow.CreateApprovedText();
                Assert.Equal("[가림] 공개 문장", Encoding.UTF8.GetString(Assert.IsType<byte[]>(approvedOcr)));
                sensitiveWindow.Close();
                sensitiveWindow = null;
                Assert.True(image.All(value => value == 0));
                Assert.True(ocr.All(value => value == 0));

                var invalidPrepared = sensitiveStore.Prepare(identity with { ToolCallId = "invalid" },
                    new PreparedSensitiveContextDraft("window", 1, 1, [1, 2, 3], Encoding.UTF8.GetBytes("글자"), 0));
                Assert.True(invalidPrepared.Success, invalidPrepared.Error);
                invalidSensitiveContext = Assert.IsType<PreparedSensitiveContext>(sensitiveStore.Take(
                    identity with { ToolCallId = "invalid" }, invalidPrepared.Token!).Context);
                invalidSensitiveWindow = new SensitiveContextPreviewWindow(invalidSensitiveContext);
                invalidSensitiveContext = null;
                Assert.Null(invalidSensitiveWindow.PreviewImage.Source);
                Assert.False(invalidSensitiveWindow.ApproveButton.IsEnabled);
                Assert.Contains("미리보기", invalidSensitiveWindow.ValidationText.Text, StringComparison.Ordinal);

                diagnosticsWindow = new DiagnosticsWindow(new ObservableCollection<string> { "12:00:00 test" });
                var diagnosticsContent = Assert.IsAssignableFrom<FrameworkElement>(diagnosticsWindow.Content);
                diagnosticsContent.Measure(new Size(820, 560));
                diagnosticsContent.Arrange(new Rect(0, 0, 820, 560));
                diagnosticsContent.UpdateLayout();
                Assert.Equal("문제 해결 정보", diagnosticsWindow.Title);
                Assert.True(diagnosticsContent.DesiredSize.Height > 0);

                VerifyLargeActivityList();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                mainWindow?.Close();
                window?.Close();
                sensitiveContext?.Dispose();
                invalidSensitiveContext?.Dispose();
                sensitiveWindow?.Close();
                invalidSensitiveWindow?.Close();
                sensitiveStore?.Dispose();
                if (approvedOcr is { Length: > 0 }) CryptographicOperations.ZeroMemory(approvedOcr);
                diagnosticsWindow?.Close();
                mailWindow?.Close();
                capabilityReviewWindow?.Close();
                connectionCapabilityReviewWindow?.Close();
                connectionRollbackReviewWindow?.Close();
                mixedConnectionCapabilityReviewWindow?.Close();
                smtpConnectionCapabilityReviewWindow?.Close();
                app?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "승인 창 구성 테스트가 제한 시간 안에 끝나지 않았습니다.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class NoOpNotificationService : IUserNotificationService
    {
        public Task ShowAsync(string title, string message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyConnectionProfileRepository : IConnectionProfileRepository
    {
        public Task<HttpConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult<HttpConnectionProfile?>(null);

        public Task<IReadOnlyList<HttpConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HttpConnectionProfile>>([]);

        public Task<bool> TrySaveProfileAsync(
            HttpConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult<ConnectionProbeReceipt?>(null);

        public Task SaveProbeReceiptAsync(
            ConnectionProbeReceipt receipt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CapabilityConnectionBinding?> GetBindingAsync(
            CapabilityVersionRef exactVersion,
            string slot,
            CancellationToken cancellationToken) => Task.FromResult<CapabilityConnectionBinding?>(null);

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>([]);

        public Task<bool> TrySaveBindingAsync(
            CapabilityConnectionBinding binding,
            long expectedRevision,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class EmptyConnectionCredentialStore : IConnectionCredentialStore
    {
        public Task<bool> ExistsAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task ReplaceAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            string secret,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyMailGatewayConnectionRepository : IMailGatewayConnectionRepository
    {
        public Task<MailGatewayConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult<MailGatewayConnectionProfile?>(null);

        public Task<IReadOnlyList<MailGatewayConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailGatewayConnectionProfile>>([]);

        public Task<bool> TrySaveProfileAsync(
            MailGatewayConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult<ConnectionProbeReceipt?>(null);

        public Task SaveProbeReceiptAsync(
            ConnectionProbeReceipt receipt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CapabilityConnectionBinding?> GetBindingAsync(
            CapabilityVersionRef exactVersion,
            string slot,
            CancellationToken cancellationToken) => Task.FromResult<CapabilityConnectionBinding?>(null);

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>([]);

        public Task<bool> TrySaveBindingAsync(
            CapabilityConnectionBinding binding,
            long expectedRevision,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class EmptySmtpConnectionRepository : ISmtpConnectionRepository
    {
        public Task<SmtpConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult<SmtpConnectionProfile?>(null);

        public Task<IReadOnlyList<SmtpConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SmtpConnectionProfile>>([]);

        public Task<bool> TrySaveProfileAsync(
            SmtpConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult<ConnectionProbeReceipt?>(null);

        public Task SaveProbeReceiptAsync(
            ConnectionProbeReceipt receipt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CapabilityConnectionBinding?> GetBindingAsync(
            CapabilityVersionRef exactVersion,
            string slot,
            CancellationToken cancellationToken) => Task.FromResult<CapabilityConnectionBinding?>(null);

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>([]);

        public Task<bool> TrySaveBindingAsync(
            CapabilityConnectionBinding binding,
            long expectedRevision,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class ReviewConnectionProfileRepository : IConnectionProfileRepository
    {
        public Dictionary<ConnectionProfileId, HttpConnectionProfile> Profiles { get; } = [];
        public Dictionary<ConnectionProfileId, ConnectionProbeReceipt> Probes { get; } = [];
        public Dictionary<(CapabilityVersionRef ExactVersion, string Slot), CapabilityConnectionBinding> Bindings { get; } = [];

        public Task<HttpConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult(Profiles.GetValueOrDefault(id));

        public Task<IReadOnlyList<HttpConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HttpConnectionProfile>>(Profiles.Values.ToArray());

        public Task<bool> TrySaveProfileAsync(
            HttpConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var current = Profiles.GetValueOrDefault(profile.Id);
            if (current is null && expectedRevision != 0 || current is not null && current.Revision != expectedRevision)
                return Task.FromResult(false);
            Profiles[profile.Id] = profile;
            return Task.FromResult(true);
        }

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult(Probes.GetValueOrDefault(id));

        public Task SaveProbeReceiptAsync(
            ConnectionProbeReceipt receipt,
            CancellationToken cancellationToken)
        {
            Probes[receipt.ProfileId] = receipt;
            return Task.CompletedTask;
        }

        public Task<CapabilityConnectionBinding?> GetBindingAsync(
            CapabilityVersionRef exactVersion,
            string slot,
            CancellationToken cancellationToken) =>
            Task.FromResult(Bindings.GetValueOrDefault((exactVersion, slot)));

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>(Bindings.Values
                .Where(binding => binding.ExactVersion.Equals(exactVersion)).ToArray());

        public Task<bool> TrySaveBindingAsync(
            CapabilityConnectionBinding binding,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var key = (binding.ExactVersion, binding.Slot);
            var current = Bindings.GetValueOrDefault(key);
            if (current is null && expectedRevision != 0 || current is not null && current.Revision != expectedRevision)
                return Task.FromResult(false);
            Bindings[key] = binding;
            return Task.FromResult(true);
        }
    }

    private sealed class ReviewMailGatewayConnectionRepository : IMailGatewayConnectionRepository
    {
        public Dictionary<ConnectionProfileId, MailGatewayConnectionProfile> Profiles { get; } = [];
        public Dictionary<ConnectionProfileId, ConnectionProbeReceipt> Probes { get; } = [];
        public Dictionary<(CapabilityVersionRef ExactVersion, string Slot), CapabilityConnectionBinding> Bindings
        {
            get;
        } = [];

        public Task<MailGatewayConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult(Profiles.GetValueOrDefault(id));

        public Task<IReadOnlyList<MailGatewayConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailGatewayConnectionProfile>>(Profiles.Values.ToArray());

        public Task<bool> TrySaveProfileAsync(
            MailGatewayConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var current = Profiles.GetValueOrDefault(profile.Id);
            if (current is null && expectedRevision != 0 || current is not null && current.Revision != expectedRevision)
                return Task.FromResult(false);
            Profiles[profile.Id] = profile;
            return Task.FromResult(true);
        }

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult(Probes.GetValueOrDefault(id));

        public Task SaveProbeReceiptAsync(
            ConnectionProbeReceipt receipt,
            CancellationToken cancellationToken)
        {
            Probes[receipt.ProfileId] = receipt;
            return Task.CompletedTask;
        }

        public Task<CapabilityConnectionBinding?> GetBindingAsync(
            CapabilityVersionRef exactVersion,
            string slot,
            CancellationToken cancellationToken) =>
            Task.FromResult(Bindings.GetValueOrDefault((exactVersion, slot)));

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>(Bindings.Values
                .Where(binding => binding.ExactVersion.Equals(exactVersion)).ToArray());

        public Task<bool> TrySaveBindingAsync(
            CapabilityConnectionBinding binding,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var key = (binding.ExactVersion, binding.Slot);
            var current = Bindings.GetValueOrDefault(key);
            if (current is null && expectedRevision != 0 || current is not null && current.Revision != expectedRevision)
                return Task.FromResult(false);
            Bindings[key] = binding;
            return Task.FromResult(true);
        }
    }

    private sealed class ReviewSmtpConnectionRepository : ISmtpConnectionRepository
    {
        public Dictionary<ConnectionProfileId, SmtpConnectionProfile> Profiles { get; } = [];
        public Dictionary<ConnectionProfileId, ConnectionProbeReceipt> Probes { get; } = [];
        public Dictionary<(CapabilityVersionRef ExactVersion, string Slot), CapabilityConnectionBinding> Bindings
        {
            get;
        } = [];

        public Task<SmtpConnectionProfile?> GetProfileAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult(Profiles.GetValueOrDefault(id));

        public Task<IReadOnlyList<SmtpConnectionProfile>> ListProfilesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SmtpConnectionProfile>>(Profiles.Values.ToArray());

        public Task<bool> TrySaveProfileAsync(
            SmtpConnectionProfile profile,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var current = Profiles.GetValueOrDefault(profile.Id);
            if (current is null && expectedRevision != 0 || current is not null && current.Revision != expectedRevision)
                return Task.FromResult(false);
            Profiles[profile.Id] = profile;
            return Task.FromResult(true);
        }

        public Task<ConnectionProbeReceipt?> GetProbeReceiptAsync(
            ConnectionProfileId id,
            CancellationToken cancellationToken) => Task.FromResult(Probes.GetValueOrDefault(id));

        public Task SaveProbeReceiptAsync(
            ConnectionProbeReceipt receipt,
            CancellationToken cancellationToken)
        {
            Probes[receipt.ProfileId] = receipt;
            return Task.CompletedTask;
        }

        public Task<CapabilityConnectionBinding?> GetBindingAsync(
            CapabilityVersionRef exactVersion,
            string slot,
            CancellationToken cancellationToken) =>
            Task.FromResult(Bindings.GetValueOrDefault((exactVersion, slot)));

        public Task<IReadOnlyList<CapabilityConnectionBinding>> GetBindingsAsync(
            CapabilityVersionRef exactVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityConnectionBinding>>(Bindings.Values
                .Where(binding => binding.ExactVersion.Equals(exactVersion)).ToArray());

        public Task<bool> TrySaveBindingAsync(
            CapabilityConnectionBinding binding,
            long expectedRevision,
            CancellationToken cancellationToken)
        {
            var key = (binding.ExactVersion, binding.Slot);
            var current = Bindings.GetValueOrDefault(key);
            if (current is null && expectedRevision != 0 || current is not null && current.Revision != expectedRevision)
                return Task.FromResult(false);
            Bindings[key] = binding;
            return Task.FromResult(true);
        }
    }

    private sealed class ReviewConnectionCredentialStore : IConnectionCredentialStore
    {
        public HashSet<(ConnectionProfileId ProfileId, ConnectionCredentialKind Kind)> Existing { get; } = [];

        public Task<bool> ExistsAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken) => Task.FromResult(Existing.Contains((profileId, kind)));

        public Task ReplaceAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            string secret,
            CancellationToken cancellationToken)
        {
            Existing.Add((profileId, kind));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            ConnectionProfileId profileId,
            ConnectionCredentialKind kind,
            CancellationToken cancellationToken)
        {
            Existing.Remove((profileId, kind));
            return Task.CompletedTask;
        }
    }

    private static CapabilityActivationReview CreateCapabilityReview()
    {
        var id = CapabilityId.Parse("company.summary");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "문서 요약",
            "문서의 핵심을 요약합니다.",
            CapabilityKind.Prompt,
            ["요약해 줘"],
            [],
            [],
            "R0",
            "instructions.md");
        var payload = new CapabilityVersionPayload(
            manifest,
            "{}",
            "핵심만 요약합니다.",
            "{\"type\":\"object\"}");
        var exact = new CapabilityVersionRef(id, version, CapabilityContentHasher.Compute(payload));
        var draft = new CapabilityDraft(
            "draft_1",
            exact,
            null,
            payload,
            CapabilityDraftState.ReadyForReview,
            0,
            "local_import",
            exact.ContentHash,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = new CapabilityCatalogEntry(
            id,
            null,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        return new CapabilityActivationReview(draft, catalog);
    }

    private static CapabilityActivationReview CreateConnectionCapabilityReview()
    {
        var id = CapabilityId.Parse("company.connection-review");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "폐쇄망 조회",
            "로컬 binding을 사용하는 조회 Workflow입니다.",
            CapabilityKind.Workflow,
            ["내부 상태를 확인해 줘"],
            ["connection.http.request.v1"],
            [new CapabilityConnectionRequirement("primary", "http")],
            "R3",
            "workflow.json");
        var payload = new CapabilityVersionPayload(
            manifest,
            "{}",
            string.Empty,
            "{\"type\":\"object\"}",
            CanonicalWorkflowJson: "{}");
        var exact = new CapabilityVersionRef(id, version, CapabilityContentHasher.Compute(payload));
        var draft = new CapabilityDraft(
            "draft_connection_review",
            exact,
            null,
            payload,
            CapabilityDraftState.ReadyForReview,
            0,
            "local_import",
            exact.ContentHash,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = new CapabilityCatalogEntry(
            id,
            null,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        return new CapabilityActivationReview(draft, catalog);
    }

    private static CapabilityActivationReview CreateMixedConnectionCapabilityReview()
    {
        var id = CapabilityId.Parse("company.mixed-connection-review");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "폐쇄망 조회와 메일",
            "HTTP 조회와 Mail Gateway를 함께 사용하는 Workflow입니다.",
            CapabilityKind.Workflow,
            ["내부 상태를 확인하고 메일로 보내 줘"],
            ["connection.http.request.v1", "mail.send.v1"],
            [
                new CapabilityConnectionRequirement("primary", CapabilityConnectionKindPolicy.Http),
                new CapabilityConnectionRequirement("mail", CapabilityConnectionKindPolicy.MailApi),
            ],
            "R3",
            "workflow.json");
        var payload = new CapabilityVersionPayload(
            manifest,
            "{}",
            string.Empty,
            "{\"type\":\"object\"}",
            CanonicalWorkflowJson: "{}");
        var exact = new CapabilityVersionRef(id, version, CapabilityContentHasher.Compute(payload));
        var draft = new CapabilityDraft(
            "draft_mixed_connection_review",
            exact,
            null,
            payload,
            CapabilityDraftState.ReadyForReview,
            0,
            "local_import",
            exact.ContentHash,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = new CapabilityCatalogEntry(
            id,
            null,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        return new CapabilityActivationReview(draft, catalog);
    }

    private static CapabilityActivationReview CreateSmtpConnectionCapabilityReview()
    {
        var id = CapabilityId.Parse("company.smtp-connection-review");
        var version = CapabilitySemanticVersion.Parse("1.0.0");
        var manifest = new CapabilityManifest(
            1,
            id,
            version,
            "폐쇄망 SMTP 메일",
            "typed SMTP binding을 사용하는 메일 Workflow입니다.",
            CapabilityKind.Workflow,
            ["SMTP로 메일을 보내 줘"],
            ["mail.send.v1"],
            [new CapabilityConnectionRequirement("mail", CapabilityConnectionKindPolicy.Smtp)],
            "R3",
            "workflow.json");
        var payload = new CapabilityVersionPayload(
            manifest,
            "{}",
            string.Empty,
            "{\"type\":\"object\"}",
            CanonicalWorkflowJson: "{}");
        var exact = new CapabilityVersionRef(id, version, CapabilityContentHasher.Compute(payload));
        var draft = new CapabilityDraft(
            "draft_smtp_connection_review",
            exact,
            null,
            payload,
            CapabilityDraftState.ReadyForReview,
            0,
            "local_import",
            exact.ContentHash,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        var catalog = new CapabilityCatalogEntry(
            id,
            null,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        return new CapabilityActivationReview(draft, catalog);
    }

    private static CapabilityRollbackReview CreateConnectionRollbackReview(CapabilityActivationReview targetReview)
    {
        var target = new CapabilityVersion(
            targetReview.Draft.ExactVersion,
            null,
            targetReview.Draft.Payload,
            targetReview.Draft.SourceKind,
            targetReview.Draft.SourcePackageHash,
            DateTimeOffset.UnixEpoch);
        var currentExact = new CapabilityVersionRef(
            targetReview.Draft.ExactVersion.Id,
            CapabilitySemanticVersion.Parse("2.0.0"),
            CapabilityContentHash.Parse($"sha256:{new string('a', 64)}"));
        var current = new CapabilityVersion(
            currentExact,
            target.ExactVersion,
            target.Payload,
            "local_import",
            currentExact.ContentHash,
            DateTimeOffset.UnixEpoch);
        var catalog = new CapabilityCatalogEntry(
            currentExact.Id,
            currentExact,
            2,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        return new CapabilityRollbackReview(catalog, current, target);
    }

    private static HttpConnectionProfile CreateHttpProfile(string id, string displayName, string baseUrl) => new(
        ConnectionProfileId.Parse(id),
        displayName,
        baseUrl,
        ConnectionAuthenticationKind.None,
        true,
        ConnectionProfileLifecycleState.Stable,
        1,
        1,
        0,
        HttpConnectionProfilePolicy.ComputeTargetHash(baseUrl, ConnectionAuthenticationKind.None),
        null,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    private static MailGatewayConnectionProfile CreateMailGatewayProfile(
        string id,
        string displayName,
        string baseUrl,
        string sender) => new(
        ConnectionProfileId.Parse(id),
        displayName,
        baseUrl,
        sender,
        true,
        ConnectionProfileLifecycleState.Stable,
        1,
        1,
        1,
        MailGatewayConnectionProfilePolicy.ComputeTargetHash(baseUrl, sender),
        null,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    private static SmtpConnectionProfile CreateSmtpProfile(
        string id,
        string displayName,
        string host,
        int port,
        SmtpTlsMode tlsMode,
        string sender) => new(
        ConnectionProfileId.Parse(id),
        displayName,
        host,
        port,
        tlsMode,
        SmtpAuthenticationMechanism.Plain,
        SmtpCertificatePolicy.SystemTrust,
        sender,
        true,
        ConnectionProfileLifecycleState.Stable,
        1,
        1,
        1,
        SmtpConnectionProfilePolicy.ComputeTargetHash(
            host,
            port,
            tlsMode,
            SmtpAuthenticationMechanism.Plain,
            SmtpCertificatePolicy.SystemTrust,
            sender),
        null,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    private static ConnectionProbeReceipt SuccessfulProbe(HttpConnectionProfile profile) => new(
        profile.Id,
        profile.TargetRevision,
        profile.CredentialRevision,
        profile.TargetHash,
        true,
        null,
        DateTimeOffset.UnixEpoch);

    private static ConnectionProbeReceipt SuccessfulProbe(MailGatewayConnectionProfile profile) => new(
        profile.Id,
        profile.TargetRevision,
        profile.CredentialRevision,
        profile.TargetHash,
        true,
        null,
        DateTimeOffset.UnixEpoch);

    private static ConnectionProbeReceipt SuccessfulProbe(SmtpConnectionProfile profile) => new(
        profile.Id,
        profile.TargetRevision,
        profile.CredentialRevision,
        profile.TargetHash,
        true,
        null,
        DateTimeOffset.UnixEpoch);

    private sealed class EmptyCapabilityManagementService : ICapabilityManagementService
    {
        public Task<IReadOnlyList<CapabilityManagementItem>> ListAsync(
            CapabilityManagementFilter filter,
            CapabilityManagementSort sort,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CapabilityManagementItem>>([]);

        public Task<CapabilityImportResult> ImportPackageAsync(
            string packagePath,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CapabilityImportResult(CapabilityManagementOutcome.Invalid));

        public Task<CapabilityReviewResult> GetActivationReviewAsync(
            string draftId,
            CapabilityVersionRef expectedVersion,
            long expectedCatalogRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CapabilityReviewResult(CapabilityManagementOutcome.NotFound));

        public Task<CapabilityManagementMutationResult> ActivateAsync(
            CapabilityActivationReview review,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CapabilityManagementMutationResult(CapabilityManagementOutcome.Succeeded));

        public Task<CapabilityRollbackReviewResult> GetRollbackReviewAsync(
            CapabilityId id,
            CapabilityVersionRef expectedActiveVersion,
            long expectedCatalogRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CapabilityRollbackReviewResult(CapabilityManagementOutcome.NotFound));

        public Task<CapabilityManagementMutationResult> RollbackAsync(
            CapabilityRollbackReview review,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CapabilityManagementMutationResult(CapabilityManagementOutcome.Succeeded));

        public Task<CapabilityManagementMutationResult> ExportVersionAsync(
            CapabilityVersionRef exactVersion,
            string destinationPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CapabilityManagementMutationResult(CapabilityManagementOutcome.Succeeded));
    }

    private sealed class EmptyFeatureBuilderManagementService : IFeatureBuilderManagementService
    {
        public Task<FeatureBuilderSession?> GetSessionAsync(
            string sessionId,
            CancellationToken cancellationToken) => Task.FromResult<FeatureBuilderSession?>(null);

        public Task<FeatureBuilderProposal?> GetProposalAsync(
            string proposalId,
            CancellationToken cancellationToken) => Task.FromResult<FeatureBuilderProposal?>(null);

        public Task<FeatureBuilderManagementResult> StartSessionAsync(
            StartFeatureBuilderSessionCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        public Task<FeatureBuilderManagementResult> GenerateAsync(
            GenerateFeatureBuilderProposalCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        public Task<FeatureBuilderManagementResult> ApplyProposalAsync(
            ApplyFeatureBuilderProposalCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        public Task<FeatureBuilderManagementResult> SetProposalDispositionAsync(
            SetFeatureBuilderProposalDispositionCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        public Task<FeatureBuilderManagementResult> CancelSessionAsync(
            CancelFeatureBuilderSessionCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        public Task<FeatureBuilderManagementResult> StartTestRunAsync(
            StartFeatureBuilderTestRunCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        public Task<FeatureBuilderManagementResult> CompleteTestRunAsync(
            CompleteFeatureBuilderTestRunCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        private static FeatureBuilderManagementResult Invalid() =>
            new(FeatureBuilderManagementOutcome.Invalid);
    }

    private sealed class ReadyDraftFeatureBuilderManagementService(CapabilityDraft draft)
        : IFeatureBuilderManagementService
    {
        private FeatureBuilderSession? _session;
        private FeatureBuilderProposal? _proposal;

        public Task<FeatureBuilderSession?> GetSessionAsync(
            string sessionId,
            CancellationToken cancellationToken) => Task.FromResult(_session);

        public Task<FeatureBuilderProposal?> GetProposalAsync(
            string proposalId,
            CancellationToken cancellationToken) => Task.FromResult(_proposal);

        public Task<FeatureBuilderManagementResult> StartSessionAsync(
            StartFeatureBuilderSessionCommand command,
            CancellationToken cancellationToken)
        {
            var hash = draft.ExactVersion.ContentHash;
            _session = new FeatureBuilderSession(
                command.SessionId,
                FeatureBuilderMode.Create,
                FeatureBuilderSessionState.Collecting,
                0,
                command.Requirement,
                hash,
                [],
                [],
                null,
                0,
                hash,
                null,
                null,
                command.NowUtc,
                command.NowUtc);
            return Task.FromResult(new FeatureBuilderManagementResult(
                FeatureBuilderManagementOutcome.Succeeded,
                _session));
        }

        public Task<FeatureBuilderManagementResult> GenerateAsync(
            GenerateFeatureBuilderProposalCommand command,
            CancellationToken cancellationToken)
        {
            var hash = draft.ExactVersion.ContentHash;
            _session = _session! with
            {
                State = FeatureBuilderSessionState.ProposalReady,
                Revision = 1,
                UpdatedAtUtc = command.NowUtc,
            };
            _proposal = new FeatureBuilderProposal(
                $"proposal_{new string('b', 48)}",
                _session.SessionId,
                _session.Revision,
                command.GenerationId,
                hash,
                hash,
                hash,
                null,
                0,
                hash,
                hash,
                draft.Payload,
                [],
                null,
                FeatureBuilderProposalState.Pending,
                0,
                null,
                null,
                command.NowUtc,
                command.NowUtc,
                null);
            return Task.FromResult(new FeatureBuilderManagementResult(
                FeatureBuilderManagementOutcome.Succeeded,
                _session,
                _proposal));
        }

        public Task<FeatureBuilderManagementResult> ApplyProposalAsync(
            ApplyFeatureBuilderProposalCommand command,
            CancellationToken cancellationToken)
        {
            _proposal = _proposal! with
            {
                State = FeatureBuilderProposalState.Applied,
                Revision = _proposal.Revision + 1,
                AppliedDraftId = draft.DraftId,
                UpdatedAtUtc = command.NowUtc,
                TerminalAtUtc = command.NowUtc,
            };
            return Task.FromResult(new FeatureBuilderManagementResult(
                FeatureBuilderManagementOutcome.Succeeded,
                _session,
                _proposal,
                draft));
        }

        public Task<FeatureBuilderManagementResult> SetProposalDispositionAsync(
            SetFeatureBuilderProposalDispositionCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        public Task<FeatureBuilderManagementResult> CancelSessionAsync(
            CancelFeatureBuilderSessionCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        public Task<FeatureBuilderManagementResult> StartTestRunAsync(
            StartFeatureBuilderTestRunCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        public Task<FeatureBuilderManagementResult> CompleteTestRunAsync(
            CompleteFeatureBuilderTestRunCommand command,
            CancellationToken cancellationToken) => Task.FromResult(Invalid());

        private static FeatureBuilderManagementResult Invalid() =>
            new(FeatureBuilderManagementOutcome.Invalid);
    }

    private sealed class StubScreenTextRecognizer(string text) : IScreenTextRecognizer
    {
        public Task<ScreenTextRecognitionResult> RecognizeAsync(
            ReadOnlyMemory<byte> encodedImage,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(encodedImage.IsEmpty);
            return Task.FromResult(new ScreenTextRecognitionResult(
                ScreenTextRecognitionStatus.Recognized,
                Encoding.UTF8.GetBytes(text)));
        }
    }

    private static byte[] CreatePng(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x6D;
            pixels[index + 1] = 0x2F;
            pixels[index + 2] = 0x00;
            pixels[index + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void VerifyLargeActivityList()
    {
        var longSummary = string.Concat(Enumerable.Repeat(
            "아주 긴 한국어 실행 설명이 좁은 화면에서도 잘리지 않고 여러 줄로 표시되어야 합니다. ", 12));
        var activity = Enumerable.Range(0, 1_000)
            .Select(index => new ToolActivitySummary(
                index,
                "system.get_storage_status.v1",
                "R0",
                index % 7 == 0 ? "failed" : "succeeded",
                $"{index}: {longSummary}",
                true,
                DateTimeOffset.UtcNow.AddMinutes(-index)))
            .ToArray();
        var view = new ActivityView(activity, []);

        view.Measure(new Size(588, 500));
        view.Arrange(new Rect(0, 0, 588, 500));
        view.UpdateLayout();

        Assert.True(VirtualizingPanel.GetIsVirtualizing(view.ActivityList));
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(view.ActivityList));
        Assert.Equal(1_000, view.ActivityList.Items.Count);
        Assert.InRange(view.DesiredSize.Width, 1, 588);
        Assert.InRange(view.DesiredSize.Height, 1, 500);
        Assert.Null(view.ActivityList.ItemContainerGenerator.ContainerFromIndex(999));
    }
}
