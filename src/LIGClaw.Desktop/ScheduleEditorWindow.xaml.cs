using System.Globalization;
using System.Windows;
using LIGClaw.Application.Scheduling;
using LIGClaw.Domain;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace LIGClaw.Desktop;

public partial class ScheduleEditorWindow : Window
{
    private readonly IScheduleRepository _schedules;
    private readonly ScheduledNotification _job;

    internal ScheduleEditorWindow(IScheduleRepository schedules, ScheduledNotification job)
    {
        _schedules = schedules;
        _job = job;
        InitializeComponent();
        TitleBox.Text = job.Title;
        MessageBox.Text = job.Message;
        StartLocalBox.Text = job.StartLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        TimeZoneBox.Text = job.TimeZoneId;
        SelectTag(RecurrenceBox, job.Recurrence);
        IntervalBox.Text = job.Interval.ToString(CultureInfo.InvariantCulture);
        SelectTag(MisfireBox, job.MisfirePolicy);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationMessage.Text = string.Empty;
        if (!NotificationSchedulePolicy.TryParseLocal(StartLocalBox.Text.Trim(), out var local) ||
            !int.TryParse(IntervalBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var interval) ||
            RecurrenceBox.SelectedItem is not ComboBoxItem { Tag: string recurrence } ||
            MisfireBox.SelectedItem is not ComboBoxItem { Tag: string misfire })
        {
            ValidationMessage.Text = "시각, 반복 및 간격을 확인해 주세요.";
            return;
        }
        var draft = new NotificationScheduleDraft(
            TitleBox.Text.Trim(), MessageBox.Text.Trim(), local, TimeZoneBox.Text.Trim(),
            recurrence, interval, misfire, "local-management");
        if (!NotificationSchedulePolicy.IsValid(draft))
        {
            ValidationMessage.Text = "입력 길이, 시간대 ID 또는 반복 범위를 확인해 주세요.";
            return;
        }
        try
        {
            var updated = await _schedules.UpdateAsync(_job.Id, draft, DateTimeOffset.UtcNow, CancellationToken.None);
            if (updated is null)
            {
                ValidationMessage.Text = "실행 중인 예약은 수정할 수 없습니다.";
                return;
            }
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ValidationMessage.Text = exception.Message;
        }
        catch (Exception)
        {
            ValidationMessage.Text = "예약을 저장하지 못했습니다. 잠시 후 다시 시도해 주세요.";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void SelectTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => StringComparer.Ordinal.Equals(item.Tag, tag));
    }
}
