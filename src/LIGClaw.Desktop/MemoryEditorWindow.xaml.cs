using System.Globalization;
using System.Windows;
using LIGClaw.Application.Memory;
using LIGClaw.Domain;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace LIGClaw.Desktop;

public partial class MemoryEditorWindow : Window
{
    private readonly IMemoryRepository _memories;

    internal MemoryEditorWindow(IMemoryRepository memories, PersonalMemory? memory = null)
    {
        _memories = memories;
        InitializeComponent();
        if (memory is null)
        {
            KindBox.SelectedIndex = 0;
            SensitivityBox.SelectedIndex = 0;
            return;
        }
        Heading.Text = "기억 수정";
        SelectTag(KindBox, memory.Kind);
        KindBox.IsEnabled = false;
        KeyBox.Text = memory.Key;
        KeyBox.IsReadOnly = true;
        ValueBox.Text = memory.Value;
        SelectTag(SensitivityBox, memory.Sensitivity);
        ExpiryBox.Text = memory.ExpiresAtUtc?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationMessage.Text = string.Empty;
        if (KindBox.SelectedItem is not ComboBoxItem { Tag: string kind } ||
            SensitivityBox.SelectedItem is not ComboBoxItem { Tag: string sensitivity }) return;
        DateTimeOffset? expiry = null;
        if (!string.IsNullOrWhiteSpace(ExpiryBox.Text))
        {
            if (!DateTime.TryParseExact(
                    ExpiryBox.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var expiryDate))
            {
                ValidationMessage.Text = "만료 날짜 형식은 yyyy-MM-dd입니다.";
                return;
            }
            expiry = new DateTimeOffset(expiryDate.Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(expiryDate.Date.AddDays(1)))
                .ToUniversalTime();
        }
        var now = DateTimeOffset.UtcNow;
        var draft = new PersonalMemoryDraft(
            kind, KeyBox.Text.Trim(), ValueBox.Text.Trim(), sensitivity, "local-management", expiry);
        if (!PersonalMemoryPolicy.IsValid(draft, now))
        {
            ValidationMessage.Text = "입력 길이와 만료일을 확인해 주세요. 비밀번호·API 키·토큰은 기억에 저장할 수 없습니다.";
            return;
        }
        try
        {
            _ = await _memories.UpsertAsync(draft, now, CancellationToken.None);
            DialogResult = true;
        }
        catch (Exception)
        {
            ValidationMessage.Text = "기억을 저장하지 못했습니다. 잠시 후 다시 시도해 주세요.";
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
