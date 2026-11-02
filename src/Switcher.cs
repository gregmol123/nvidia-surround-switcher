using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ScreenSwitcher;

public enum ScreenMode { Surround, Extended }

// What the program keeps between runs: each mode's layout as it was last seen on the screens.
public sealed class SavedLayouts
{
    public List<Grid>? Surround { get; set; }
    public List<Grid>? Extended { get; set; }
}

public static class Switcher
{
    const string SwitchMutex = "ScreenSwitcher.Switching";
    static readonly string LayoutsPath = Path.Combine(AppContext.BaseDirectory, "screens.json");
    static readonly TimeSpan ModeChangeTimeout = TimeSpan.FromSeconds(30);

    public static ScreenMode ModeOf(List<Grid> grids) =>
        grids.Any(grid => grid.Displays.Count > 1) ? ScreenMode.Surround : ScreenMode.Extended;

    // For example "7680×2160, 240 Hz" or "2 × 3840×2160, 240 Hz".
    public static string Describe(List<Grid> grids) =>
        string.Join(" + ", grids
            .Select(grid => $"{grid.Width * grid.Columns}×{grid.Height * grid.Rows}, {grid.Frequency} Hz")
            .GroupBy(text => text)
            .Select(same => same.Count() > 1 ? $"{same.Count()} × {same.Key}" : same.Key));

    // Remembers the mode that is on now, so it can be restored exactly after being switched away from.
    public static List<Grid> ReadAndRemember()
    {
        var grids = NvApi.ReadGrids();
        Remember(grids);
        return grids;
    }

    // Returns false when another switch is still running; a second press of the shortcut is simply ignored.
    public static bool SwitchTo(ScreenMode target) => RunExclusively(() => Change(target));

    public static bool Toggle() => RunExclusively(() =>
        Change(ModeOf(NvApi.ReadGrids()) == ScreenMode.Surround ? ScreenMode.Extended : ScreenMode.Surround));

    static bool RunExclusively(Action action)
    {
        using var mutex = new Mutex(false, SwitchMutex);
        try
        {
            if (!mutex.WaitOne(0)) return false;
        }
        catch (AbandonedMutexException)
        {
            // The previous copy died mid-switch; the mutex is ours now.
        }

        try
        {
            action();
            return true;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    static void Change(ScreenMode target)
    {
        var before = NvApi.ReadGrids();
        var layouts = Remember(before);
        if (ModeOf(before) == target) return;

        var wanted = target == ScreenMode.Surround
            ? layouts.Surround ?? throw new InvalidOperationException(
                "Surround is not known yet. Turn Surround on once in the NVIDIA Control Panel, then open this program so it can remember it.")
            : layouts.Extended ?? OneGridPerMonitor(before);

        NvApi.Validate(wanted);
        NvApi.Apply(wanted);

        if (WaitForMode(target)) return;

        try
        {
            NvApi.Apply(before);
        }
        catch (InvalidOperationException)
        {
            // The message below already tells that the switch failed.
        }
        throw new InvalidOperationException("The screens did not switch in time, so the program tried to go back to the previous layout.");
    }

    static bool WaitForMode(ScreenMode target)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < ModeChangeTimeout)
        {
            if (ModeOf(NvApi.ReadGrids()) == target) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    // Used until an Extended layout has been seen: every monitor of Surround on its own, in the same mode.
    static List<Grid> OneGridPerMonitor(List<Grid> surround) =>
        surround.SelectMany(grid => grid.Displays.Select(display =>
            new Grid(1, 1, 0, grid.Width, grid.Height, grid.Bpp, grid.Frequency, [display with { OverlapX = 0, OverlapY = 0 }])))
        .ToList();

    static SavedLayouts Remember(List<Grid> current)
    {
        var layouts = Load();
        if (ModeOf(current) == ScreenMode.Surround)
            layouts.Surround = current;
        // A monitor that is switched off drops out of the list, and that must not become the Extended layout to come back to.
        else if (layouts.Surround is null || MonitorsOf(layouts.Surround).All(MonitorsOf(current).Contains))
            layouts.Extended = current;
        Save(layouts);
        return layouts;
    }

    static HashSet<uint> MonitorsOf(List<Grid> grids) =>
        grids.SelectMany(grid => grid.Displays).Select(display => display.Id).ToHashSet();

    static SavedLayouts Load()
    {
        try
        {
            return JsonSerializer.Deserialize<SavedLayouts>(File.ReadAllText(LayoutsPath)) ?? new SavedLayouts();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new SavedLayouts();
        }
    }

    static void Save(SavedLayouts layouts)
    {
        var temp = LayoutsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(layouts, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, LayoutsPath, overwrite: true);
    }
}
