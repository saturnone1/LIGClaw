using System.Windows;
using LIGClaw.Desktop.Infrastructure.Shell;

namespace LIGClaw.Desktop;

public partial class SettingsWindow : Window
{
    private readonly StartupRegistrationService _startupRegistration = new();

    public SettingsWindow(bool isQuickAccessAvailable)
    {
        InitializeComponent();
        if (!isQuickAccessAvailable)
        {
            ShortcutStatusText.Text = "다른 프로그램이 이 단축키를 사용 중이에요.";
            ShortcutStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }

        Loaded += SettingsWindow_Loaded;
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StartWithWindowsCheckBox.IsChecked = _startupRegistration.IsEnabled();
        }
        catch (Exception)
        {
            SettingsStatusText.Text = "시작 설정을 불러오지 못했어요.";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _startupRegistration.SetEnabled(StartWithWindowsCheckBox.IsChecked == true);
            DialogResult = true;
        }
        catch (Exception)
        {
            SettingsStatusText.Text = "설정을 저장하지 못했어요. 잠시 후 다시 시도해 주세요.";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
