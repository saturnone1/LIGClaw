using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;

namespace LIGClaw.Desktop;

public partial class DiagnosticsWindow : Window
{
    private readonly ObservableCollection<string> _diagnostics;

    internal DiagnosticsWindow(ObservableCollection<string> diagnostics)
    {
        _diagnostics = diagnostics;
        InitializeComponent();
        DataContext = diagnostics;
        Loaded += (_, _) => ScrollToLatest();
        _diagnostics.CollectionChanged += Diagnostics_CollectionChanged;
        Closed += (_, _) => _diagnostics.CollectionChanged -= Diagnostics_CollectionChanged;
    }

    private void Diagnostics_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToLatest();

    private void ScrollToLatest()
    {
        if (DiagnosticsList.Items.Count > 0) DiagnosticsList.ScrollIntoView(DiagnosticsList.Items[^1]);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, _diagnostics));
        }
        catch
        {
            // 다른 프로세스가 클립보드를 잠근 경우 창은 계속 사용할 수 있다.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
