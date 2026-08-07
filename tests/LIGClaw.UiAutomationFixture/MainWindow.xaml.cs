using System.Windows;

namespace LIGClaw.UiAutomationFixture;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void Save_Click(object sender, RoutedEventArgs e) =>
        FixtureStatus.Text = $"Saved: {FixtureInput.Text}";

}
