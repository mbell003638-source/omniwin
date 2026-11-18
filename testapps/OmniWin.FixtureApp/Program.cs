using System.Drawing;
using System.Windows.Forms;

namespace OmniWin.FixtureApp;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // With --scenario Fnn the app shows one of the fixture scenarios (see Scenarios/); otherwise it is the original window.
        var options = Scenarios.ScenarioOptions.Parse(args);
        if (options.Scenario is not null)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            return Scenarios.ScenarioRunner.Run(options);
        }

        var title = args is ["--title", var requested] && !string.IsNullOrWhiteSpace(requested)
            ? requested : "OmniWin desktop fixture";
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new FixtureWindow(title));
        return 0;
    }
}

internal sealed class FixtureWindow : Form
{
    private readonly InputTrace? _inputTrace;
    private readonly Button _apply;
    private readonly Label _applied;
    private readonly Label _deleteCount;
    private Form? _cover;
    private int _applications;
    private int _deletions;

    internal FixtureWindow(string title)
    {
        _inputTrace = InputTrace.TryCreate();
        Text = title;
        Name = "OmniWinFixtureWindow";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(70, 70);
        ClientSize = new Size(680, 610);
        AutoScaleMode = AutoScaleMode.Dpi;
        TopMost = true;

        var editor = _inputTrace?.CreateEditor() ?? new TextBox();
        editor.Name = "Editor";
        editor.AccessibleName = "Editor";
        editor.Multiline = true;
        editor.Location = new Point(24, 24);
        editor.Size = new Size(440, 70);
        var secret = new TextBox
        {
            Name = "Secret",
            AccessibleName = "Secret",
            UseSystemPasswordChar = true,
            Text = "fixture-secret-712",
            Location = new Point(24, 112),
            Size = new Size(300, 26),
        };
        var toggle = new CheckBox
        {
            Name = "Toggle",
            AccessibleName = "Toggle",
            Text = "Toggle",
            Location = new Point(24, 156),
            AutoSize = true,
        };
        _apply = Button("Apply", 24, 198);
        _applied = Status("Applied 0", 180, 205);
        _deleteCount = Status("Delete count 0", 180, 253);
        _apply.Click += (_, _) =>
        {
            _applications++;
            SetStatus(_applied, $"Applied {_applications}");
        };
        var delete = Button("Delete", 24, 246);
        delete.Click += (_, _) =>
        {
            _deletions++;
            SetStatus(_deleteCount, $"Delete count {_deletions}");
        };

        var open = Button("Open dialog", 24, 294);
        open.Click += (_, _) => BeginInvoke(new Action(OpenDialog));
        var disabled = Button("Disabled", 180, 294);
        disabled.Enabled = false;

        var cover = Button("Cover target", 24, 342);
        cover.Click += (_, _) => CoverTarget();
        var uncover = Button("Uncover target", 180, 342);
        uncover.Click += (_, _) =>
        {
            _cover?.Close();
            _cover = null;
        };
        var rename = Button("Rename target", 336, 342);
        rename.Click += (_, _) =>
        {
            _apply.AccessibleName = "Renamed apply";
            _apply.Text = "Renamed apply";
        };

        var items = new ListBox
        {
            Name = "Items",
            AccessibleName = "Items",
            Location = new Point(24, 408),
            Size = new Size(440, 170),
            IntegralHeight = false,
        };
        for (var i = 1; i <= 100; i++) items.Items.Add($"Item {i}");
        items.SelectedIndex = 0;

        Controls.AddRange([editor, secret, toggle, _apply, _applied, delete, _deleteCount, open, disabled,
            cover, uncover, rename, items]);
        Shown += (_, _) =>
        {
            BeginInvoke((Action)(() =>
            {
                TopMost = false;
                TopMost = true;
                BringToFront();
                Activate();
                editor.Focus();
                Console.WriteLine("OMNIWIN_FIXTURE_READY");
            }));
        };
        FormClosed += (_, _) => _cover?.Close();
        if (_inputTrace is not null) FormClosed += (_, _) => _inputTrace.WriteReport();
    }

    private Button Button(string name, int x, int y)
    {
        var button = _inputTrace?.CreateButton() ?? new Button();
        button.Name = name.Replace(" ", "", StringComparison.Ordinal);
        button.AccessibleName = name;
        button.Text = name;
        button.Location = new Point(x, y);
        button.Size = new Size(140, 34);
        return button;
    }

    private static Label Status(string text, int x, int y) => new()
    {
        Text = text,
        AccessibleName = text,
        Location = new Point(x, y),
        AutoSize = true,
    };

    private static void SetStatus(Label label, string text)
    {
        label.Text = text;
        label.AccessibleName = text;
    }

    private void OpenDialog()
    {
        using var dialog = new Form
        {
            Name = "FixtureDialog",
            Text = Text + " Dialog",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 140),
        };
        var entry = new TextBox
        {
            Name = "DialogEntry",
            AccessibleName = "Dialog entry",
            Location = new Point(20, 20),
            Size = new Size(270, 26),
        };
        var close = Button("Close dialog", 20, 70);
        close.Click += (_, _) => dialog.BeginInvoke(new Action(dialog.Close));
        dialog.Controls.AddRange([entry, close]);
        dialog.ShowDialog(this);
    }

    private void CoverTarget()
    {
        if (_cover is not null) return;
        var target = _apply.RectangleToScreen(_apply.ClientRectangle);
        target.Inflate(8, 8);
        _cover = new Form
        {
            Text = Text + " Cover",
            Name = "FixtureCover",
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = target,
            TopMost = true,
            ShowInTaskbar = false,
        };
        _cover.Controls.Add(new Label { Text = "Covered", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
        _cover.Show();
    }
}
