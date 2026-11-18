using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ListBox = System.Windows.Controls.ListBox;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using ComboBox = System.Windows.Controls.ComboBox;
using Slider = System.Windows.Controls.Slider;
using StackPanel = System.Windows.Controls.StackPanel;
using ListBoxItem = System.Windows.Controls.ListBoxItem;

namespace OmniWin.FixtureApp.Scenarios;

/// <summary>Common shell for the WPF scenarios (F02, F08).</summary>
internal abstract class WpfScenarioWindow : Window
{
    protected readonly ScenarioLog Log;

    protected WpfScenarioWindow(string id, string defaultTitle, string? title, ScenarioLog log)
    {
        Log = log;
        Title = title ?? defaultTitle;
        Name = "OmniWinFixture" + id;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 70;
        Top = 70;
        Width = 720;
        Height = 640;
        ContentRendered += (_, _) =>
        {
            Activate();
            Log.Write(null, "window_shown", Title);
            Console.WriteLine("OMNIWIN_FIXTURE_READY");
            Console.WriteLine("OMNIWIN_FIXTURE_LOG " + Log.Path);
        };
        Closed += (_, _) => Log.Write(null, "window_closed", Title);
    }

    internal virtual void Command(string command) => Log.Write(null, "unknown_command", command);

    protected static T Identify<T>(T element, string automationId, string name) where T : UIElement
    {
        AutomationProperties.SetAutomationId(element, automationId);
        AutomationProperties.SetName(element, name);
        return element;
    }
}

internal static class WpfScenarios
{
    internal static WpfScenarioWindow? Create(ScenarioOptions o, ScenarioLog log) => o.Scenario switch
    {
        "F02" => new F02WpfControls(o.Title, log),
        "F08" => new F08VirtualList(o.Title, log),
        _ => null,
    };
}

/// <summary>F02: the same controls as F01, in WPF.</summary>
internal sealed class F02WpfControls : WpfScenarioWindow
{
    internal F02WpfControls(string? title, ScenarioLog log) : base("F02", "OmniWin fixture F02", title, log)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        var name = Identify(new TextBox { Width = 260, HorizontalAlignment = System.Windows.HorizontalAlignment.Left }, "Name", "Name");
        name.TextChanged += (_, _) => Log.Write("Name", "text_changed", name.Text);
        var apply = Identify(new Button { Content = "Apply", Width = 150, Height = 32, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left }, "Apply", "Apply");
        var applied = 0;
        apply.Click += (_, _) => { applied++; Log.Write("Apply", "click", applied.ToString(System.Globalization.CultureInfo.InvariantCulture)); };
        var agree = Identify(new CheckBox { Content = "Agree", Margin = new Thickness(0, 12, 0, 0) }, "Agree", "Agree");
        agree.Checked += (_, _) => Log.Write("Agree", "checked", "true");
        agree.Unchecked += (_, _) => Log.Write("Agree", "checked", "false");
        var a = Identify(new RadioButton { Content = "Choice A", GroupName = "choice", Margin = new Thickness(0, 8, 0, 0) }, "ChoiceA", "Choice A");
        var b = Identify(new RadioButton { Content = "Choice B", GroupName = "choice" }, "ChoiceB", "Choice B");
        a.Checked += (_, _) => Log.Write("ChoiceA", "checked", "true");
        b.Checked += (_, _) => Log.Write("ChoiceB", "checked", "true");
        var combo = Identify(new ComboBox { Width = 200, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) }, "Fruit", "Fruit");
        combo.Items.Add("Apple");
        combo.Items.Add("Banana");
        combo.Items.Add("Cherry");
        combo.SelectionChanged += (_, _) => Log.Write("Fruit", "selected", combo.SelectedItem?.ToString());
        var slider = Identify(new Slider { Minimum = 0, Maximum = 10, Width = 240, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) }, "Level", "Level");
        slider.ValueChanged += (_, e) => Log.Write("Level", "value", Math.Round(e.NewValue).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var list = Identify(new ListBox { Height = 120, Width = 260, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) }, "Items", "Items");
        for (var i = 1; i <= 20; i++) list.Items.Add($"Item {i}");
        list.SelectionChanged += (_, _) => Log.Write("Items", "selected", list.SelectedItem?.ToString());
        panel.Children.Add(name);
        panel.Children.Add(apply);
        panel.Children.Add(agree);
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(combo);
        panel.Children.Add(slider);
        panel.Children.Add(list);
        Content = panel;
    }
}

/// <summary>
/// F08: virtualized list of 100,000 rows with recycled containers. The log records the row each click really landed on, so a click
/// on a recycled container that now shows another row is visible as a wrong-target action.
/// </summary>
internal sealed class F08VirtualList : WpfScenarioWindow
{
    private readonly ListBox _list;
    private readonly string[] _rows = Enumerable.Range(1, 100_000).Select(i => $"Row {i}").ToArray();

    internal F08VirtualList(string? title, ScenarioLog log) : base("F08", "OmniWin fixture F08", title, log)
    {
        _list = Identify(new ListBox { ItemsSource = _rows, Margin = new Thickness(24) }, "Rows", "Rows");
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(_list, true);
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var container = ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) as ListBoxItem;
            Log.Write("Rows", "click", container?.Content?.ToString(), "the row the click really landed on");
        };
        _list.SelectionChanged += (_, _) => Log.Write("Rows", "selected", _list.SelectedItem?.ToString());
        Content = _list;
    }

    internal override void Command(string command)
    {
        // "scroll N": bring row N into view (recycles containers deterministically).
        if (command.StartsWith("scroll ", StringComparison.Ordinal) && int.TryParse(command.AsSpan(7), out var row) && row >= 1 && row <= _rows.Length)
        {
            _list.ScrollIntoView(_rows[row - 1]);
            Log.Write("Rows", "scrolled_to", _rows[row - 1]);
        }
        else
        {
            base.Command(command);
        }
    }
}
