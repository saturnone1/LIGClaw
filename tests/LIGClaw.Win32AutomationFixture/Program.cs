namespace LIGClaw.Win32AutomationFixture;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new FixtureForm());
    }

    private sealed class FixtureForm : Form
    {
        private readonly TextBox _input = new() { Name = "Win32FixtureInput", AccessibleName = "Win32 fixture input", Width = 320 };
        private readonly Label _status = new() { Name = "Win32FixtureStatus", Text = "Win32 fixture idle", AutoSize = true };

        public FixtureForm()
        {
            Text = "LIGClaw Win32 UIA Fixture";
            Width = 440;
            Height = 250;
            StartPosition = FormStartPosition.CenterScreen;
            var save = new Button
            {
                Name = "Win32SaveButton",
                AccessibleName = "Save Win32 fixture",
                Text = "Save Win32 fixture",
                AutoSize = true,
            };
            save.Click += (_, _) => _status.Text = $"Saved Win32: {_input.Text}";
            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(24),
                WrapContents = false,
            };
            layout.Controls.Add(new Label { Text = "Native control fixture", AutoSize = true });
            layout.Controls.Add(_input);
            layout.Controls.Add(save);
            layout.Controls.Add(_status);
            Controls.Add(layout);
        }
    }
}
