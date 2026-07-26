using System.Globalization;
using System.Windows;
using LIGClaw.Application.Scheduling;
using LIGClaw.Domain;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace LIGClaw.Desktop;

public partial class AgentJobEditorWindow : Window
{
    private readonly IAgentJobRepository _jobs;

    internal AgentJobEditorWindow(IAgentJobRepository jobs)
    {
        _jobs = jobs;
        InitializeComponent();
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddMinutes(5), TimeZoneInfo.Local).DateTime;
        StartLocalBox.Text = local.AddTicks(-(local.Ticks % TimeSpan.TicksPerSecond))
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        TimeZoneBox.Text = TimeZoneInfo.Local.Id;
        RecurrenceBox.SelectedIndex = 0;
        MisfireBox.SelectedIndex = 1;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationMessage.Text = string.Empty;
        if (!NotificationSchedulePolicy.TryParseLocal(StartLocalBox.Text.Trim(), out var local) ||
            RecurrenceBox.SelectedItem is not ComboBoxItem { Tag: string recurrence } ||
            MisfireBox.SelectedItem is not ComboBoxItem { Tag: string misfire } ||
            !int.TryParse(IntervalBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var interval) ||
            !int.TryParse(RuntimeBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var runtime) ||
            !int.TryParse(AttemptsBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var attempts) ||
            !int.TryParse(ResultLimitBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var resultLimit))
        {
            ValidationMessage.Text = "시각과 숫자 설정을 확인해 주세요.";
            return;
        }
        var profile = ModelProfileBox.Text.Trim();
        var draft = new AgentJobDraft(
            TitleBox.Text.Trim(), PromptBox.Text.Trim(), local, TimeZoneBox.Text.Trim(), recurrence,
            interval, misfire, profile.Length == 0 ? null : profile, runtime, attempts, resultLimit,
            "local-management");
        if (!AgentJobPolicy.IsValid(draft))
        {
            ValidationMessage.Text = "제목·지시·시간대 또는 실행 제한 범위를 확인해 주세요.";
            return;
        }
        try
        {
            _ = await _jobs.CreateAgentJobAsync(draft, DateTimeOffset.UtcNow, CancellationToken.None);
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ValidationMessage.Text = exception.Message;
        }
        catch
        {
            ValidationMessage.Text = "Agent 작업을 저장하지 못했습니다.";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
