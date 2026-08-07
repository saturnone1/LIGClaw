using System.Globalization;
using System.Windows;
using LIGClaw.Application.Scheduling;
using LIGClaw.Desktop.Infrastructure.Shell;
using LIGClaw.Domain;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace LIGClaw.Desktop;

public partial class AgentJobEditorWindow : Window
{
    private readonly IAgentJobRepository _jobs;
    private readonly string _source;

    internal AgentJobEditorWindow(IAgentJobRepository jobs, RoutineSuggestionCandidate? suggestion = null)
    {
        _jobs = jobs;
        _source = suggestion is null ? "local-management" : "routine-suggestion";
        InitializeComponent();
        var local = suggestion?.SuggestedStartLocal ??
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddMinutes(5), TimeZoneInfo.Local).DateTime;
        StartLocalBox.Text = local.AddTicks(-(local.Ticks % TimeSpan.TicksPerSecond))
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        TimeZoneBox.Text = TimeZoneInfo.Local.Id;
        RecurrenceBox.SelectedIndex = suggestion?.Recurrence switch
        {
            ScheduleValues.Daily => 1,
            ScheduleValues.Weekly => 2,
            _ => 0,
        };
        MisfireBox.SelectedIndex = 1;
        if (suggestion is not null)
        {
            PromptBox.Text = suggestion.Prompt;
            TitleBox.Text = suggestion.Prompt.Length <= 60
                ? suggestion.Prompt
                : string.Concat(suggestion.Prompt.AsSpan(0, 60), "…");
        }
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
            _source);
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
