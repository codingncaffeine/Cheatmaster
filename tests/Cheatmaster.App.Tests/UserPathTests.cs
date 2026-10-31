using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cheatmaster.App.ViewModels;
using Cheatmaster.App.Views;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Tests;
using Xunit;

namespace Cheatmaster.App.Tests;

/// <summary>
/// Settings and the cheat library live under the XDG data folder, so the tests get one of their
/// own before anything reads it. Absolute, so nothing lands next to the test binaries. Kept apart
/// from <see cref="HeadlessApp"/>: touching that class starts the UI session.
/// </summary>
internal static class TestProfile
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        string root = Path.Combine(Path.GetTempPath(), "cheatmaster-app-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"));
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(root, "cache"));
    }
}

/// <summary>The app's own App under Avalonia's headless platform: no display, no bus.</summary>
public static class HeadlessApp
{
    // The builder lives in a class of its own: the session calls it from its own thread, and a
    // builder on this class would wait for this class's static initialiser, which waits for the session.
    public static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(HeadlessAppBuilder));
}

public static class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>
/// The main window as a player works it, through its own controls: choose the game in the picker,
/// type the number on screen, scan, keep the address, write a new value, freeze it.
/// </summary>
public class UserPathTests
{
    private const int HeldValue = 123_456_789;

    private sealed class BindingSink : ILogSink
    {
        public readonly List<string> Lines = [];
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Lines.Add(messageTemplate);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Lines.Add(messageTemplate + " " + string.Join(" | ", propertyValues));
    }

    [Fact]
    public Task Find_keep_set_and_freeze_a_value_through_the_window() => HeadlessApp.Session.Dispatch(() =>
    {
        ResetLibrary();
        using var target = WatchTargetProcess.Start("hold", HeldValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var outside = TargetProcess.Open(target.Pid, out string why);
        Assert.True(outside is not null, why);
        Assert.Equal(HeldValue, outside!.ReadValue<int>(target.Field));

        var sink = new BindingSink();
        Logger.Sink = sink;

        var window = new MainWindow();
        var vm = window.ViewModel;
        window.Show();
        Settle(300);

        // A first run opens on the guide; the full search panel is one click away.
        if (vm.Guide.IsActive) Click(window, Button(window, "Skip the guide"));
        Settle();
        Assert.False(vm.Guide.IsActive, "the guide is still covering the search panel");

        // Choose the game in the picker, by its pid.
        Click(window, window.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, vm.AttachCommand)));
        var picker = WaitFor(() => window.OwnedWindows.OfType<ProcessPickerWindow>().FirstOrDefault(), "the process picker never opened");
        var list = picker.GetVisualDescendants().OfType<ListBox>().Single();
        WaitFor(() => list.ItemCount > 0 ? list : null, "the picker listed no processes");
        var search = picker.GetVisualDescendants().OfType<TextBox>().First();
        search.Focus();
        picker.KeyTextInput(target.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Settle();
        Assert.Equal(target.Pid, (list.SelectedItem as ProcessCandidate)?.Pid);
        Click(picker, Button(picker, "Attach"));
        WaitFor(() => vm.IsAttached ? vm : null, "attaching never finished: " + vm.Notice);
        Assert.Equal(target.Pid, vm.Process!.Pid);

        // Type the number on screen and press Enter.
        var valueBox = window.FindControl<TextBox>("ValueBox")!;
        valueBox.Focus();
        window.KeyTextInput(HeldValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        WaitFor(() => !vm.IsScanning && vm.HasResults ? vm : null, "the scan found nothing: " + vm.Notice, seconds: 60);

        var results = vm.Results!;
        ResultRow? hit = null;
        for (int i = 0; i < Math.Min(results.Count, 5000) && hit is null; i++)
        {
            if (results[i].Address == target.Field) hit = results[i];
        }
        Assert.True(hit is not null, $"the held value's address was not among {results.Count} results");

        // One row per address, and once few are left the app names the likely one outright.
        int rowsForField = 0;
        for (int i = 0; i < results.Count; i++) if (results[i].Address == target.Field) rowsForField++;
        Assert.Equal(1, rowsForField);
        Assert.True(results.IsByAddress, "the list still shows one row per encoding");
        if (results.TotalCount <= 8)
        {
            Assert.True(vm.HasLikely, "no most-likely address was offered: " + vm.Verdict);
            Assert.Contains("left", vm.Verdict);
            Assert.True(Button(window, "Add and test it").IsEffectivelyVisible);
        }

        // Keep it: select the row, then Enter.
        var grid = window.FindControl<ListBox>("ResultsGrid")!;
        grid.ScrollIntoView(hit);
        Settle();
        var rowControl = grid.ContainerFromItem(hit!) as Control
                         ?? throw new Xunit.Sdk.XunitException("the result's row is not on screen");
        Click(window, rowControl);
        Assert.Same(hit, grid.SelectedItem);
        // The tests share one library, so a table saved by another test may already be loaded.
        int before = vm.Cheats.Count;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle();
        Assert.True(vm.Cheats.Count == before + 1,
            $"Enter on the selected result added {vm.Cheats.Count - before}; focus was on {TopLevel.GetTopLevel(grid)?.FocusManager?.GetFocusedElement()}");
        var kept = vm.Cheats[^1];
        Assert.Equal(target.Field, kept.Entry.Address.Resolve(vm.Process));

        // Write a new value through the Set value prompt.
        var cheats = window.FindControl<DataGrid>("CheatsGrid")!;
        cheats.SelectedItem = kept;
        Settle();
        Click(window, Button(window, "Set value…"));
        var prompt = WaitFor(() => window.OwnedWindows.OfType<ValuePromptWindow>().FirstOrDefault(), "the value prompt never opened");
        prompt.GetVisualDescendants().OfType<TextBox>().First().Text = "4242";
        Click(prompt, Button(prompt, "Apply"));
        WaitFor(() => outside.ReadValue<int>(target.Field) == 4242 ? vm : null,
            $"the game's value is {outside.ReadValue<int>(target.Field)}, not 4242: {vm.Notice}");

        // Freeze it from the row's own check box. Holding the value is covered against a real
        // process by FreezerTests in the core suite; here the check is that the row's box reaches it.
        var freeze = cheats.GetVisualDescendants().OfType<CheckBox>().First(c => ReferenceEquals(c.DataContext, kept));
        Click(window, freeze);
        Settle();
        Assert.True(kept.Frozen, "the row's check box did not freeze it");
        Assert.Contains("frozen", vm.FreezeStatus);

        Assert.True(sink.Lines.Count == 0, "binding errors:\n" + string.Join("\n", sink.Lines.Distinct()));
        window.Close();
        Settle();
    }, CancellationToken.None);

    /// <summary>
    /// A first scan for zero matches most of memory. The grid must only build the rows it shows;
    /// building a view model for every hit is what the lazy list exists to prevent.
    /// </summary>
    [Fact]
    public Task A_huge_result_set_only_builds_the_rows_on_screen() => HeadlessApp.Session.Dispatch(() =>
    {
        ResetLibrary();
        using var target = WatchTargetProcess.Start("hold", "0");

        var window = new MainWindow();
        var vm = window.ViewModel;
        vm.Guide.DismissCommand.Execute(null);
        window.Show();
        Settle(200);

        var candidate = ProcessList.Enumerate(includeSystem: true).First(p => p.Pid == target.Pid);
        vm.Attach(candidate);
        WaitFor(() => vm.IsAttached ? vm : null, "attaching never finished: " + vm.Notice);

        vm.SelectedTypeOption = vm.TypeOptions.First(t => t.Label.Contains("4 Bytes", StringComparison.OrdinalIgnoreCase) || t.Label.Contains("Int32", StringComparison.OrdinalIgnoreCase));
        vm.ValueText = "0";
        vm.ScanCommand.Execute(null);
        WaitFor(() => !vm.IsScanning && vm.HasResults ? vm : null, "the scan found nothing: " + vm.Notice, seconds: 120);
        Settle(500);

        var results = vm.Results!;
        Assert.True(results.Count > 10_000, $"expected a huge result set, got {results.Count}");
        Assert.True(results.RealizedCount < 500,
            $"the grid built {results.RealizedCount:N0} rows for {results.Count:N0} results");

        window.Close();
        Settle();
    }, CancellationToken.None);

    /// <summary>
    /// A game keeps copies of a number for its screens, and every copy follows the real one
    /// through any search. The app has to tell them apart by test, and pick the real one.
    /// </summary>
    [Fact]
    public Task Find_the_real_one_picks_the_value_the_copies_follow() => HeadlessApp.Session.Dispatch(() =>
    {
        ResetLibrary();
        using var target = WatchTargetProcess.Start("mirror", "987654321");
        using var outside = TargetProcess.Open(target.Pid, out string why);
        Assert.True(outside is not null, why);
        // Wait for the stand-in to make its copies.
        var until = DateTime.UtcNow.AddSeconds(5);
        while (outside!.ReadValue<int>(target.Field + 24) != 987654321 && DateTime.UtcNow < until) Thread.Sleep(20);

        var window = new MainWindow();
        var vm = window.ViewModel;
        vm.Guide.DismissCommand.Execute(null);
        window.Show();
        Settle(200);

        vm.Attach(ProcessList.Enumerate(includeSystem: true).First(p => p.Pid == target.Pid));
        WaitFor(() => vm.IsAttached ? vm : null, "attaching never finished: " + vm.Notice);

        vm.ValueText = "987654321";
        vm.ScanCommand.Execute(null);
        WaitFor(() => !vm.IsScanning && vm.HasResults ? vm : null, "the scan found nothing: " + vm.Notice, seconds: 60);
        Settle(200);

        Assert.True(vm.Results!.TotalCount is >= 4 and <= 8, $"expected the value and its copies, got {vm.Results.TotalCount} addresses");
        Assert.True(vm.CanFindReal, "Find the real one was not offered: " + vm.Verdict);

        // What a player does first: keep every candidate, and one of them twice.
        var candidates = new List<ResultRow>();
        for (int i = 0; i < vm.Results.TotalCount; i++) candidates.Add(vm.Results[i]);
        vm.AddResults(candidates, "gold");
        int kept = vm.Cheats.Count;
        vm.AddResult(candidates[0]);
        Assert.Equal(kept, vm.Cheats.Count);

        Click(window, Button(window, "Find the real one"));
        WaitFor(() => vm.Verdict.StartsWith("Found it", StringComparison.Ordinal) ? vm : null,
            "the real one was not found: " + vm.Verdict + " / " + vm.Notice, seconds: 20);

        // Only the real one is left of the candidates; the copies were taken out of the table.
        var left = vm.Cheats.Select(c => c.Entry.Address.Resolve(vm.Process!))
            .Where(a => candidates.Any(r => r.Address == a)).ToList();
        Assert.Equal([target.Field], left);
        // The test's nudged numbers never stick to what is kept.
        var keptRow = vm.Cheats.Single(c => c.Entry.Address.Resolve(vm.Process!) == target.Field);
        Assert.Equal("987654321", keptRow.Entry.FreezeValue);
        // Put back as it was, and the copies follow it again.
        WaitFor(() => outside.ReadValue<int>(target.Field + 8) == 987654321 ? vm : null, "the copies did not settle back");
        Assert.Equal(987654321, outside.ReadValue<int>(target.Field));

        window.Close();
        Settle();
    }, CancellationToken.None);

    /// <summary>
    /// A cheat saved as a plain address, the game restarted: the entry must not touch the old
    /// address, and Find it again must bring it back, freeze and all.
    /// </summary>
    [Fact]
    public Task Find_it_again_brings_a_saved_value_back_after_a_restart() => HeadlessApp.Session.Dispatch(() =>
    {
        ResetLibrary();
        const string Value = "123123123";
        ulong oldField;

        // First session: find it, keep it, freeze it.
        {
            using var target = WatchTargetProcess.Start("mirror", Value);
            WaitForCopies(target, Value);
            var window = new MainWindow();
            var vm = window.ViewModel;
            vm.SeekRoutesInBackground = false;
            vm.Guide.DismissCommand.Execute(null);
            window.Show();
            Settle(200);
            AttachTo(vm, target);
            vm.ClearCheatsCommand.Execute(null);
            Settle(300);
            foreach (var prompt in window.OwnedWindows.OfType<MessageWindow>().ToList()) Click(prompt, Button(prompt, "Yes"));
            Settle(200);

            vm.ValueText = Value;
            vm.ScanCommand.Execute(null);
            WaitFor(() => !vm.IsScanning && vm.HasResults ? vm : null, "the scan found nothing: " + vm.Notice, seconds: 60);
            Settle(200);
            Click(window, Button(window, "Find the real one"));
            WaitFor(() => vm.Verdict.StartsWith("Found it", StringComparison.Ordinal) ? vm : null, "not found: " + vm.Notice, seconds: 20);
            var row = vm.Cheats.Single(c => c.Entry.Address.Resolve(vm.Process!) == target.Field);
            row.Frozen = true;
            row.Group = "gold";
            // A second stale line under the same cheat, as an earlier search would have left.
            vm.AddResults([vm.Results!.Cast<ResultRow>().First(r => r.Address != target.Field)], "gold");
            oldField = target.Field;
            vm.SaveTableCommand.Execute(null);
            window.Close();
            Settle();
        }

        // The bare line saved ahead of the real one, so simply taking the first line would keep the
        // wrong one and throw away the freeze.
        string saved = Directory.EnumerateFiles(Cheatmaster.Core.Cheats.CheatLibrary.DefaultRoot, "*.cmt").Single();
        var table = Cheatmaster.Core.Cheats.CheatTable.Load(saved)!;
        table.Entries.Reverse();
        Assert.False(table.Entries[0].Frozen);
        table.Save(saved);

        // The game starts again: the value is somewhere new.
        using var restarted = WatchTargetProcess.Start("mirror", Value);
        WaitForCopies(restarted, Value);
        Assert.NotEqual(oldField, restarted.Field);
        {
            var window = new MainWindow();
            var vm = window.ViewModel;
            vm.SeekRoutesInBackground = false;
            vm.Guide.DismissCommand.Execute(null);
            window.Show();
            Settle(200);
            AttachTo(vm, restarted);
            Settle(300);

            var row = vm.Cheats.Single(c => c.Frozen);
            Assert.All(vm.Cheats, c => Assert.True(c.NeedsFinding, "a plain address from the last session was trusted"));
            Assert.Equal(string.Empty, vm.FreezeStatus);

            // The player presses Find it again on the bare line and types the number on screen.
            var bare = vm.Cheats.Single(c => !c.Frozen);
            var bareRowButton = window.GetVisualDescendants().OfType<Button>()
                .First(b => Equals(b.Content, "Find it again") && ReferenceEquals(b.DataContext, bare));
            Click(window, bareRowButton);
            var prompt = WaitFor(() => window.OwnedWindows.OfType<ValuePromptWindow>().FirstOrDefault(), "Find it again asked nothing");
            prompt.GetVisualDescendants().OfType<TextBox>().First().Text = Value;
            Click(prompt, Button(prompt, "Apply"));

            WaitFor(() => row.NeedsFinding ? null : vm, "it was not found again: " + vm.Verdict + " / " + vm.Notice, seconds: 40);
            Assert.Equal(restarted.Field, row.Entry.Address.Resolve(vm.Process!));
            Assert.Contains("frozen", vm.FreezeStatus);
            Assert.Single(vm.Cheats); // the stale line saved with it was cleared
            window.Close();
            Settle();
        }
    }, CancellationToken.None);

    private static void WaitForCopies(WatchTargetProcess target, string value)
    {
        using var outside = TargetProcess.Open(target.Pid, out _);
        int want = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (outside!.ReadValue<int>(target.Field + 24) != want && DateTime.UtcNow < until) Thread.Sleep(20);
    }

    private static void AttachTo(MainViewModel vm, WatchTargetProcess target)
    {
        vm.Attach(ProcessList.Enumerate(includeSystem: true).First(p => p.Pid == target.Pid));
        WaitFor(() => vm.IsAttached && vm.Process?.Pid == target.Pid ? vm : null, "attaching never finished: " + vm.Notice);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Every test starts with no saved cheats: a table another test saved for the stand-in game
    /// would be loaded on attach, and found again automatically, under this test's feet. The
    /// library lives in the tests' own temporary profile, never the user's.
    /// </summary>
    private static void ResetLibrary()
    {
        string root = Cheatmaster.Core.Cheats.CheatLibrary.DefaultRoot;
        Assert.StartsWith(Path.GetTempPath(), root);
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*.cmt")) File.Delete(file);
    }

    private static Button Button(Control root, string content) =>
        root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && Equals(b.Content, content))
        ?? throw new Xunit.Sdk.XunitException($"no visible button \"{content}\"");

    /// <summary>A real press and release over the middle of the control.</summary>
    private static void Click(Window window, Control control)
    {
        Settle(50);
        Assert.True(control.IsEffectivelyVisible && control.IsEffectivelyEnabled, $"{control} cannot be clicked");
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                     ?? throw new Xunit.Sdk.XunitException($"{control} is not in the window");
        window.MouseMove(centre);
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Settle(50);
    }

    private static void Settle(int milliseconds = 120)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }

    private static T WaitFor<T>(Func<T?> probe, string failure, int seconds = 15) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            if (probe() is { } found) return found;
            Thread.Sleep(20);
        }
        throw new Xunit.Sdk.XunitException(failure);
    }
}
