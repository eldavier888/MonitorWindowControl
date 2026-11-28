using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MonitorWindowControl.Tests;

/// <summary>"Split into areas": the areas' geometry, which area a window is in, learning, and saving the split.</summary>
public class SplitTests
{
    private const string App = "mwc-test-app.exe"; // no real app has this name, so no real window is ever moved

    private static readonly FakeMonitor Wide = new(1, "Ultrawide", 0, Primary: true, Port: "port-wide", Model: "AAAA-0001", Serial: "1");
    private static readonly FakeMonitor Side = new(2, "Side", 1920, Port: "port-side", Model: "BBBB-0002", Serial: "2");

    private static Native.RECT Work => new() { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };

    private static MonitorSplit SplitOf(string preset, params string[] names) =>
        new() { Preset = preset, Names = names.ToList() };

    private static MonitorInfo? Target(TestEngine t, AppRule? rule) => t.Engine.ChooseTarget(rule, null, null).Target;

    // ------------------------------------------------------------------ geometry

    [Fact]
    public void TwoEqualAreasShareTheWorkAreaWithoutGapOrOverlap()
    {
        var r = MonitorAreas.Rects(Work, false, SplitPresets.DefaultRatios(SplitPresets.TwoEqual)!);
        Assert.Equal(2, r.Count);
        Assert.Equal((0, 960), (r[0].Left, r[0].Right));
        Assert.Equal((960, 1920), (r[1].Left, r[1].Right));
        Assert.All(r, x => Assert.Equal((0, 1040), (x.Top, x.Bottom)));
    }

    [Theory]
    [InlineData(SplitPresets.Two6040, 1152)]
    [InlineData(SplitPresets.Two7030, 1344)]
    public void UnequalSplitsUseTheirShares(string preset, int firstRight)
    {
        var r = MonitorAreas.Rects(Work, false, SplitPresets.DefaultRatios(preset)!);
        Assert.Equal(firstRight, r[0].Right);
        Assert.Equal(firstRight, r[1].Left);
        Assert.Equal(1920, r[1].Right);
    }

    [Theory]
    [InlineData(SplitPresets.ThreeEqual, 3)]
    [InlineData(SplitPresets.FourEqual, 4)]
    public void EveryPixelBelongsToExactlyOneArea(string preset, int count)
    {
        var work = new Native.RECT { Left = -1000, Top = 30, Right = 2441, Bottom = 1440 }; // odd width, taskbar at the top
        var r = MonitorAreas.Rects(work, false, SplitPresets.DefaultRatios(preset)!);
        Assert.Equal(count, r.Count);
        Assert.Equal(work.Left, r[0].Left);
        Assert.Equal(work.Right, r[^1].Right);
        for (int i = 1; i < r.Count; i++) Assert.Equal(r[i - 1].Right, r[i].Left);
    }

    [Fact]
    public void StackedAreasSplitTheHeight()
    {
        var r = MonitorAreas.Rects(Work, true, SplitPresets.DefaultRatios(SplitPresets.TwoStacked)!);
        Assert.Equal((0, 520), (r[0].Top, r[0].Bottom));
        Assert.Equal((520, 1040), (r[1].Top, r[1].Bottom));
        Assert.All(r, x => Assert.Equal((0, 1920), (x.Left, x.Right)));
        Assert.Equal(new[] { "Top", "Bottom" }, SplitPresets.DefaultNames(SplitPresets.TwoStacked));
    }

    [Fact]
    public void AreasAreMonitorsOfTheirOwnInsideTheWorkArea()
    {
        var list = MonitorAreas.Split(new[] { Wide.ToInfo(), Side.ToInfo() },
                                      m => m.DeviceName == Wide.Device ? SplitOf(SplitPresets.TwoEqual) : null);
        Assert.Equal(new[] { @"\\.\DISPLAY1#1", @"\\.\DISPLAY1#2", @"\\.\DISPLAY2" }, list.Select(m => m.DeviceName));
        var left = list[0];
        Assert.True(left.IsArea);
        Assert.Equal(@"\\.\DISPLAY1", left.PhysicalDevice);
        Assert.Equal("Left", left.FriendlyName);
        Assert.True(left.IsPrimary);
        Assert.False(list[1].IsPrimary);                  // one main monitor…
        Assert.True(list[1].IsPhysicalPrimary);           // …but both areas are on it
        Assert.Equal(1040, left.Bounds.Bottom);           // the work area, not the whole screen
        Assert.Equal(2, list[1].Hardware.Area);
        Assert.Equal("port-wide", list[1].Hardware.DevicePath);
        Assert.False(list[2].IsArea);
    }

    [Fact]
    public void DeviceNamesOfAreasRoundTrip()
    {
        Assert.Equal(@"\\.\DISPLAY3#2", MonitorAreas.AreaDevice(@"\\.\DISPLAY3", 2));
        Assert.Equal(@"\\.\DISPLAY3", MonitorAreas.PhysicalDeviceOf(@"\\.\DISPLAY3#2"));
        Assert.Equal(2, MonitorAreas.AreaOfDevice(@"\\.\DISPLAY3#2"));
        Assert.Null(MonitorAreas.AreaOfDevice(@"\\.\DISPLAY3"));
    }

    // ------------------------------------------------------------------ which area a window is in

    [Fact]
    public void AWindowBelongsToTheAreaWithItsCentre()
    {
        var list = MonitorAreas.Split(new[] { Wide.ToInfo() }, _ => SplitOf(SplitPresets.ThreeEqual));
        var centre = MonitorAreas.Centre(new Native.RECT { Left = 500, Top = 100, Right = 1500, Bottom = 900 }); // mostly in two areas
        Assert.Equal(@"\\.\DISPLAY1#2", MonitorAreas.AreaAt(list, Wide.Device, centre)?.DeviceName);
        Assert.Equal(@"\\.\DISPLAY1#3", MonitorAreas.AreaAt(list, Wide.Device, new Native.POINT { X = 1900, Y = 10 })?.DeviceName);
    }

    [Fact]
    public void AWindowOutsideEveryAreaBelongsToTheNearestOne()
    {
        var list = MonitorAreas.Split(new[] { Wide.ToInfo() }, _ => SplitOf(SplitPresets.TwoEqual));
        // On the taskbar (below the work area) on the right, and with no position at all.
        Assert.Equal(@"\\.\DISPLAY1#2", MonitorAreas.AreaAt(list, Wide.Device, new Native.POINT { X = 1500, Y = 1070 })?.DeviceName);
        Assert.Equal(@"\\.\DISPLAY1#1", MonitorAreas.AreaAt(list, Wide.Device, null)?.DeviceName);
        Assert.Null(MonitorAreas.AreaAt(list, @"\\.\DISPLAY9", null));
    }

    // ------------------------------------------------------------------ the engine

    [Fact]
    public void ASplitMonitorIsListedAsItsAreasWithNumbers()
    {
        using var t = new TestEngine(Wide);
        t.Engine.SetMonitorSplit(t.Display(1), SplitPresets.TwoEqual, null);
        Assert.Equal(new[] { "1 · Left", "2 · Right" }, t.Engine.Monitors.Select(m => m.Name));
        Assert.Single(t.Engine.PhysicalMonitors);
        Assert.Equal(@"\\.\DISPLAY1", t.Engine.PhysicalOf(t.Engine.Monitors[1]).DeviceName); // Make main, Disconnect, DPI
        Assert.Equal(@"\\.\DISPLAY1#2", t.Engine.MonitorForShortcut(2)?.DeviceName);          // "send to monitor 2"
        Assert.False(t.Engine.Monitors[1].CanDisconnect);
    }

    [Fact]
    public void OneSplitMonitorLearnsButOneWholeMonitorDoesNot()
    {
        using var t = new TestEngine(Wide);
        Assert.Equal("only one monitor", t.Engine.LearningBlockedReason(DateTime.UtcNow.AddMinutes(5)));
        t.Engine.SetMonitorSplit(t.Display(1), SplitPresets.TwoEqual, null);
        Assert.Null(t.Engine.LearningBlockedReason(DateTime.UtcNow.AddMinutes(5)));
        t.Engine.SetMonitorSplit(t.Engine.Monitors[0], SplitPresets.None, null);
        Assert.NotNull(t.Engine.LearningBlockedReason(DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public void AnAppSetToAnAreaOpensInThatArea()
    {
        using var t = new TestEngine(Wide, Side);
        t.Engine.SetMonitorSplit(t.Display(1), SplitPresets.ThreeEqual, new[] { "Code", "", "Chat" });
        var chat = t.Engine.Monitors.Single(m => m.DeviceName == @"\\.\DISPLAY1#3");
        Assert.Equal("Chat", chat.FriendlyName);
        Assert.Equal("Middle", t.Engine.Monitors.Single(m => m.DeviceName == @"\\.\DISPLAY1#2").FriendlyName); // empty = default
        t.Engine.Pin(App, chat, App);
        var rule = t.Engine.Settings.Find(App)!;
        Assert.Equal(3, rule.MonitorHardware?.Area);
        Assert.Equal(@"\\.\DISPLAY1#3", Target(t, rule)?.DeviceName);

        // Windows renumbers the displays: still the same area of the same monitor.
        t.Plug(Wide with { Number = 4 }, Side with { Number = 1, Primary = true });
        Assert.Equal(@"\\.\DISPLAY4#3", Target(t, rule)?.DeviceName);
    }

    [Fact]
    public void AnAppSetBeforeTheSplitOpensInTheFirstAreaAndAnAreaWithoutTheSplitIsTheWholeMonitor()
    {
        using var t = new TestEngine(Wide, Side);
        var whole = t.Pin(App, 1);
        t.Engine.SetMonitorSplit(t.Display(1), SplitPresets.TwoEqual, null);
        Assert.Equal(@"\\.\DISPLAY1#1", Target(t, whole)?.DeviceName);

        t.Engine.Pin(App, t.Engine.Monitors.Single(m => m.DeviceName == @"\\.\DISPLAY1#2"), App);
        var rule = t.Engine.Settings.Find(App)!;
        t.Engine.SetMonitorSplit(t.Engine.Monitors[0], SplitPresets.None, null);
        Assert.Equal(@"\\.\DISPLAY1", Target(t, rule)?.DeviceName);
    }

    [Fact]
    public void SwitchedOffAppliesToEveryAreaOfTheMonitor()
    {
        using var t = new TestEngine(Wide, Side);
        t.Engine.SetMonitorSplit(t.Display(1), SplitPresets.TwoEqual, null);
        t.Engine.SetMonitorSwitchedOff(t.Engine.Monitors.Single(m => m.DeviceName == @"\\.\DISPLAY1#2"), true);
        Assert.All(t.Engine.Monitors.Where(m => m.IsArea), m => Assert.True(m.IsSwitchedOff));
        Assert.False(t.Display(2).IsSwitchedOff);
        Assert.Null(t.Engine.Settings.MonitorOptions.Single().Monitor.Hardware?.Area); // saved for the whole monitor
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void TheSplitIsSavedAndReadBack()
    {
        using (var t = new TestEngine(Wide))
        {
            t.Engine.SetMonitorSplit(t.Display(1), SplitPresets.Two7030, new[] { "Work", "Music" });
            t.Engine.SetMaximizeFillsArea(false);
        }
        var s = SettingsStore.Load(out bool existed);
        Assert.True(existed);
        var split = Assert.Single(s.MonitorSplits);
        Assert.Equal(SplitPresets.Two7030, split.Preset);
        Assert.Equal(new[] { "Work", "Music" }, split.Names);
        Assert.Equal(new[] { 0.7, 0.3 }, split.Ratios);
        Assert.Equal("port-wide", split.Monitor.Hardware?.DevicePath);
        Assert.Null(split.Monitor.Hardware?.Area);
        Assert.False(s.MaximizeFillsArea);
    }

    [Fact]
    public void AnOlderFileHasNoSplitAndMaximizeFillsTheArea()
    {
        TestFolder.Reset();
        File.WriteAllText(Paths.SettingsFile, """{ "Version": 1, "Apps": [] }""");
        var s = SettingsStore.Load(out _);
        Assert.Empty(s.MonitorSplits);
        Assert.True(s.MaximizeFillsArea);
    }

    [Fact]
    public void ADamagedSplitIsRepaired()
    {
        TestFolder.Reset();
        File.WriteAllText(Paths.SettingsFile, """
            { "Version": 1, "MonitorSplits": [
                null,
                { "Monitor": { "Device": "\\\\.\\DISPLAY1#2", "Hardware": { "DevicePath": "p1", "Area": 2 } }, "Preset": "TwoEqual", "Ratios": [ 5, -1, 3 ], "Names": [ " Code ", null ] },
                { "Monitor": { "Device": "\\\\.\\DISPLAY2" }, "Preset": "Seven" },
                { "Monitor": { "Device": "\\\\.\\DISPLAY3" }, "Preset": "None" },
                { "Monitor": null, "Preset": "TwoEqual" }
            ],
              "Apps": [ { "Key": "a.exe", "MonitorDevice": "\\\\.\\DISPLAY1#9", "MonitorHardware": { "Model": "X", "Area": 9 } } ] }
            """);
        var s = SettingsStore.Load(out _);
        var split = Assert.Single(s.MonitorSplits);
        Assert.Equal(@"\\.\DISPLAY1", split.Monitor.Device);
        Assert.Null(split.Monitor.Hardware!.Area);
        Assert.Equal(new[] { 0.5, 0.5 }, split.Ratios);                 // shares that don't fit: the preset's own
        Assert.Equal(new[] { "Code", "Right" }, split.Names);           // trimmed, missing ones get the default
        Assert.Null(s.Apps[0].MonitorHardware!.Area);                   // no such area
    }

    [Fact]
    public void AreaIdentityRoundTripsThroughJson()
    {
        var s = new AppSettings();
        s.Apps.Add(new AppRule { Key = "a.exe", MonitorHardware = new MonitorHardware { DevicePath = "p", Area = 2 } });
        s.Apps.Add(new AppRule { Key = "b.exe", MonitorHardware = new MonitorHardware { DevicePath = "p" } });
        TestFolder.Reset();
        SettingsStore.Save(s);
        string json = File.ReadAllText(Paths.SettingsFile);
        Assert.Equal(1, json.Split("\"Area\"").Length - 1);                // a whole monitor writes nothing new
        var back = SettingsStore.Load(out _);
        Assert.Equal(2, back.Find("a.exe")!.MonitorHardware!.Area);
        Assert.Null(back.Find("b.exe")!.MonitorHardware!.Area);
    }
}
