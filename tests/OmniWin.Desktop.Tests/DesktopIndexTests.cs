using OmniWin.Core;

namespace OmniWin.Desktop.Tests;

public sealed class DesktopIndexTests
{
    [Fact]
    public void Discovery_keeps_window_ids_when_titles_and_order_change()
    {
        var index = new DesktopIndex();
        index.UpdateWindows([Window(100), Window(200, process: "other.exe")]);
        var first = index.ByHandle(100)!;
        var second = index.ByHandle(200)!;

        index.UpdateWindows([second.Sample with { Title = "Renamed" }, first.Sample with { Foreground = true }]);

        Assert.Same(first, index.ByHandle(100));
        Assert.Same(second, index.ByHandle(200));
        Assert.Equal(new[] { second.Id, first.Id }, index.Windows.Select(window => window.Id));
        Assert.Equal("Renamed", index.ById(second.Id)!.Info.Title);
    }

    [Fact]
    public void Duplicate_native_window_is_discovered_once()
    {
        var index = new DesktopIndex();
        index.UpdateWindows([Window(100), Window(100)]);

        Assert.Single(index.Windows);
        Assert.Equal("w1", index.Windows[0].Id);
    }

    [Fact]
    public void Closed_window_and_its_element_refs_are_retired_before_handle_reuse()
    {
        var index = IndexWithWindow(out var oldWindow);
        var oldElement = index.UpdateElements(oldWindow, [Root(), Button("button")], 0, 0).Elements[1];
        index.UpdateWindows([]);

        Assert.Null(index.ById(oldWindow.Id));
        Assert.Null(index.FindRef(oldElement.Ref, out _));
        Assert.True(index.IssuedWindow(oldWindow.Id));
        Assert.True(index.IssuedElement(oldElement.Ref));
        Assert.Equal(oldWindow.Sample, index.KnownWindow(oldWindow.Id));
        Assert.Equal(oldWindow.Sample, index.KnownElementWindow(oldElement.Ref));

        index.UpdateWindows([Window(100)]);
        var newWindow = index.ByHandle(100)!;
        var newElement = index.UpdateElements(newWindow, [Root(), Button("button")], 0, 0).Elements[1];
        Assert.NotEqual(oldWindow.Id, newWindow.Id);
        Assert.NotEqual(oldElement.Ref, newElement.Ref);
    }

    [Theory]
    [InlineData(123, 11)]
    [InlineData(124, 10)]
    public void Reused_handle_with_new_process_identity_gets_a_new_window_id(int pid, long processStart)
    {
        var index = IndexWithWindow(out var oldWindow);
        var oldRef = index.UpdateElements(oldWindow, [Root()], 0, 0).Elements[0].Ref;

        index.UpdateWindows([Window(100) with { Pid = pid, ProcessStart = processStart }]);

        Assert.NotEqual(oldWindow.Id, index.ByHandle(100)!.Id);
        Assert.Null(index.FindRef(oldRef, out _));
    }

    [Fact]
    public void Refs_are_unique_across_windows_and_are_stable_for_the_same_runtime_identity()
    {
        var index = new DesktopIndex();
        index.UpdateWindows([Window(100), Window(200)]);
        var first = index.ByHandle(100)!;
        var second = index.ByHandle(200)!;
        var firstObservation = index.UpdateElements(first, [Root(), Button("button")], 0, 0);
        var secondObservation = index.UpdateElements(second, [Root(), Button("button")], 0, 0);
        var changed = index.UpdateElements(first, [Root(), Button("button") with { Name = "Renamed" }, Button("new")], 0, 0);

        Assert.Equal(firstObservation.Elements[1].Ref, changed.Elements[1].Ref);
        Assert.NotEqual(firstObservation.Elements[0].Ref, secondObservation.Elements[0].Ref);
        Assert.Equal("e5", changed.Elements[2].Ref);
        Assert.Same(first, index.FindRef(changed.Elements[2].Ref, out var identity));
        Assert.Equal("new", identity);
    }

    [Fact]
    public void Foreground_and_unambiguous_selectors_resolve_without_guessing_between_background_windows()
    {
        var index = new DesktopIndex();
        index.UpdateWindows([
            Window(100) with { Title = "Report one" },
            Window(200) with { Title = "Report two" },
            Window(300, process: "other.exe") with { Title = "Only other", Foreground = true },
        ]);

        Assert.Null(index.ResolveWindow("fixture"));
        Assert.Null(index.ResolveWindow("Report"));
        Assert.Equal(index.ByHandle(300), index.ResolveWindow(null));
        Assert.Equal(index.ByHandle(100), index.ResolveWindow("Report one"));
        Assert.Equal(index.ByHandle(300), index.ResolveWindow("OTHER.EXE"));
        Assert.Equal(index.ByHandle(200), index.ResolveWindow(" W2 "));
        Assert.Null(index.ResolveWindow("w999"));
        Assert.Null(index.ResolveWindow(""));

        index.UpdateWindows([Window(100), Window(200) with { Foreground = true }]);
        Assert.Equal(index.ByHandle(200), index.ResolveWindow("fixture.exe"));
    }

    [Fact]
    public void Modal_relationships_follow_owner_ids_and_clear_when_dialog_closes()
    {
        var index = new DesktopIndex();
        index.UpdateWindows([Window(100), Window(200) with { Owner = 100, Modal = true, Foreground = true }]);
        var owner = index.ByHandle(100)!;
        var dialog = index.ByHandle(200)!;

        Assert.Equal(owner.Id, dialog.Info.OwnerId);
        Assert.Equal(dialog.Id, owner.BlockedBy);
        Assert.True(dialog.Info.IsModal);

        index.UpdateWindows([Window(100)]);
        Assert.Null(owner.BlockedBy);
    }

    [Fact]
    public void Minimized_modal_dialog_does_not_block_its_owner()
    {
        var index = new DesktopIndex();
        index.UpdateWindows([Window(100), Window(200) with { Owner = 100, Modal = true, Minimized = true }]);

        Assert.Null(index.ByHandle(100)!.BlockedBy);
    }

    [Fact]
    public void Identical_observations_reuse_generation_and_snapshot()
    {
        var index = IndexWithWindow(out var window);
        var first = index.UpdateElements(window, [Root(), Button("button")], 0, 0);
        var second = index.UpdateElements(window, [Root(), Button("button")], 0, 0);

        Assert.Same(first, second);
        Assert.Equal(1, second.Generation);
    }

    [Fact]
    public void Kept_properties_structure_and_window_changes_advance_generations()
    {
        var index = IndexWithWindow(out var window);
        var first = index.UpdateElements(window, [Root(), Button("button")], 0, 0);
        var renamed = index.UpdateElements(window, [Root(), Button("button") with { Name = "Changed" }], 0, 0);
        var disabled = index.UpdateElements(window, [Root(), Button("button") with { Name = "Changed", Enabled = false }], 0, 0);
        var removed = index.UpdateElements(window, [Root()], 0, 0);
        index.UpdateWindows([window.Sample with { Foreground = true }]);
        var activated = index.UpdateElements(window, [Root()], 0, 0);
        var clipped = index.UpdateElements(window, [Root()], 1, 2);

        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 },
            new[] { first.Generation, renamed.Generation, disabled.Generation, removed.Generation, activated.Generation, clipped.Generation });
    }

    [Fact]
    public void History_retains_recent_generations_and_evicts_old_baselines()
    {
        var index = IndexWithWindow(out var window);
        for (var value = 0; value < 10; value++)
        {
            index.UpdateElements(window, [Root(), Button("button") with { Value = value.ToString() }], 0, 0);
        }

        Assert.Null(window.Snapshot(1));
        Assert.Null(window.Snapshot(2));
        Assert.NotNull(window.Snapshot(3));
        Assert.NotNull(window.Snapshot(10));
        Assert.Equal(8, window.History.Count());
    }

    [Fact]
    public void Recently_retired_metadata_is_bounded_and_never_maps_to_a_reused_handle()
    {
        var index = IndexWithWindow(out var first);
        var firstSample = first.Sample;
        index.UpdateWindows([]);
        for (var i = 0; i < 129; i++)
        {
            index.UpdateWindows([Window(100) with { Pid = 1000 + i, ProcessStart = 1000 + i }]);
            index.UpdateWindows([]);
        }

        Assert.Null(index.KnownWindow(first.Id));
        Assert.Null(index.ById(first.Id));
        Assert.Null(index.KnownWindow("w999"));
        Assert.NotEqual(firstSample, index.KnownWindow("w130"));
    }

    [Fact]
    public void Pruning_an_old_ref_is_distinct_from_retiring_its_window()
    {
        var index = IndexWithWindow(out var window);
        var samples = new List<ElementSample> { Root() };
        for (var i = 0; i < 4100; i++)
        {
            samples.Add(Button("button" + i, index: i));
        }

        var first = index.UpdateElements(window, samples, 0, 0);
        var retiredRef = first.Elements[1].Ref;
        for (var generation = 0; generation < 8; generation++)
        {
            index.UpdateElements(window, [Root() with { Value = generation.ToString() }], 0, 0);
        }

        Assert.True(index.IssuedElement(retiredRef));
        Assert.Null(index.FindRef(retiredRef, out _));
        Assert.Null(index.KnownElementWindow(retiredRef));
        Assert.Same(window, index.ById(window.Id));

        var returned = index.UpdateElements(window, [Root(), Button("button0")], 0, 0);
        Assert.NotEqual(retiredRef, returned.Elements[1].Ref);
    }

    [Theory]
    [InlineData("w0")]
    [InlineData("w01")]
    [InlineData("w2")]
    [InlineData("W1")]
    [InlineData("e1")]
    [InlineData("w+1")]
    [InlineData("w-1")]
    public void Issued_ids_require_the_exact_issued_window_format(string value)
    {
        var index = IndexWithWindow(out _);

        Assert.False(index.IssuedWindow(value));
    }

    [Fact]
    public void Identity_checks_reject_reparenting_process_changes_and_reused_runtime_ids()
    {
        var sample = Button("button");

        Assert.True(sample.SameTarget(sample with { Value = "new", Focused = true }));
        Assert.False(sample.SameTarget(sample with { Name = "Other" }));
        Assert.False(sample.SameTarget(sample with { Parent = "different-parent" }));
        Assert.False(sample.SameTarget(sample with { Pid = 999 }));
        Assert.False(sample.SameTarget(sample with { Password = true }));
        Assert.False(sample.SameTarget(sample with { Enabled = false }));
        Assert.False(sample.SameTarget(sample with { ReadOnly = true }));
        Assert.False(sample.SameTarget(sample with { AutomationId = "new-id" }));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(-4, true)]
    [InlineData(-5, false)]
    public void Position_tolerance_is_four_physical_pixels(int distance, bool expected)
    {
        var before = Button("button");
        var after = before with { Bounds = before.Bounds with { X = before.Bounds.X + distance } };

        Assert.Equal(expected, before.PositionMatches(after));
    }

    [Fact]
    public void Scrolling_can_move_an_unchanged_control_but_does_not_excuse_resizing_or_new_containers()
    {
        var before = Button("button") with { Scroll = new ScrollPosition("list", 0, 0) };
        var after = before with { Bounds = before.Bounds with { Y = 500 }, Scroll = new ScrollPosition("list", 0, 50) };

        Assert.True(before.PositionMatches(after));
        Assert.False(before.PositionMatches(after with { Scroll = before.Scroll }));
        Assert.False(before.PositionMatches(after with { Scroll = new ScrollPosition("other", 0, 50) }));
        Assert.False(before.PositionMatches(after with { Bounds = after.Bounds with { Width = after.Bounds.Width + 5 } }));
    }

    internal static DesktopIndex IndexWithWindow(out DesktopWindow window)
    {
        var index = new DesktopIndex();
        index.UpdateWindows([Window(100)]);
        window = index.ByHandle(100)!;
        return index;
    }

    internal static WindowSample Window(long handle, string process = "fixture.exe") =>
        new(handle, 123, 10, process, "Fixture", new ScreenRect(0, 0, 800, 600));

    internal static ElementSample Root() =>
        new("root", null, 123, "window", "Fixture", "", "FixtureWindow", 0,
            new ScreenRect(0, 0, 800, 600), new ScreenRect(0, 0, 800, 600));

    internal static ElementSample Button(string identity, string? parent = "root", int index = 0) =>
        new(identity, parent, 123, "button", "Continue", "continueButton", "Button", index,
            new ScreenRect(10, 10, 100, 30), new ScreenRect(10, 10, 100, 30),
            Actions: ElementActions.Click | ElementActions.Invoke | ElementActions.Focus);
}
