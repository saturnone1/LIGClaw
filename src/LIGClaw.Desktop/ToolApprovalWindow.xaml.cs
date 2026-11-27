using System.Windows;
using LIGClaw.Desktop.Infrastructure.Tools;

namespace LIGClaw.Desktop;

public partial class ToolApprovalWindow : Window
{
    internal ToolApprovalChoice Choice { get; private set; } = ToolApprovalChoice.Deny;

    internal ToolApprovalWindow(WindowsToolApprovalPrompt prompt, bool allowAlways = false)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        InitializeComponent();
        HeadingText.Text = prompt.Heading;
        ActionText.Text = prompt.Action;
        DetailsText.Text = prompt.Details;
        RiskText.Text = prompt.Risk switch
        {
            "R1" => "R1 · 낮은 위험",
            "R2" => "R2 · 변경 주의",
            "R3" => "R3 · 높은 영향",
            _ => $"{prompt.Risk} · 사용자 확인",
        };
        UndoText.Text = prompt.CanUndo ? "실행 후 되돌릴 수 있음" : "자동 되돌리기 없음";
        AllowAlwaysButton.Visibility = allowAlways ? Visibility.Visible : Visibility.Collapsed;
        AllowConversationButton.Visibility = allowAlways ? Visibility.Visible : Visibility.Collapsed;
        ScopeText.Text = allowAlways
            ? "이번 한 번, 현재 대화가 유지되는 동안, 또는 표시된 대상과 내용이 정확히 같은 동작을 30일 동안 허용할 수 있습니다. 30일 권한은 설정에서 언제든 철회할 수 있습니다."
            : "허용하면 이번 요청에서 이 동작을 한 번만 실행합니다.";
        Loaded += (_, _) => DenyButton.Focus();
    }

    private void AllowOnce_Click(object sender, RoutedEventArgs e)
    {
        Choice = ToolApprovalChoice.AllowOnce;
        DialogResult = true;
    }

    private void AllowAlways_Click(object sender, RoutedEventArgs e)
    {
        Choice = ToolApprovalChoice.AllowAlways;
        DialogResult = true;
    }

    private void AllowConversation_Click(object sender, RoutedEventArgs e)
    {
        Choice = ToolApprovalChoice.AllowConversation;
        DialogResult = true;
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        Choice = ToolApprovalChoice.Deny;
        DialogResult = false;
    }
}
