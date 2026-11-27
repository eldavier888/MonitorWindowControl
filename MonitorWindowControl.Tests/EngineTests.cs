using System;
using System.Linq;
using Xunit;

namespace MonitorWindowControl.Tests;

/// <summary>Where the engine sends windows, and when it learns, on made-up monitors.</summary>
public class EngineTests
{
    private const string App = "mwc-test-app.exe"; // no real app has this name, so no real window is ever moved

    private static readonly FakeMonitor Main = new(1, "Main", 0, Primary: true, Port: "port-main", Model: "AAAA-0001", Serial: "1");
    private static readonly FakeMonitor Side = new(2, "Side", 1920, Port: "port-side", Model: "BBBB-0002", Serial: "2");
    private static readonly FakeMonitor Tv = new(3, "LG TV", 3840, Port: "port-tv", Model: "1E6D-0003", Serial: "3");

    private static MonitorInfo? Target(TestEngine t, AppRule? rule) => t.Engine.ChooseTarget(rule, null, null).Target;

    // ------------------------------------------------------------------ fallback order

    [Fact]
    public void AnAppOpensOnItsOwnMonitorWhenItIsConnected()
    {
        using var t = new TestEngine(Main, Side, Tv);
        var rule = t.Pin(App, 3);
        Assert.Equal(@"\\.\DISPLAY3", Target(t, rule)?.DeviceName);
    }

    [Fact]
    public void WithItsMonitorOffItUsesTheAppsOwnChoiceFirst()
    {
        using var t = new TestEngine(Main, Side, Tv);
        var rule = t.Pin(App, 3);
        t.Engine.SetAppFallback(App, FallbackModes.Monitor, t.Display(2));
        t.Engine.SetDefaultFallback(FallbackModes.Leave, null);
        t.Plug(Main, Side);
        Assert.Equal(@"\\.\DISPLAY2", Target(t, t.Engine.Settings.Find(App))?.DeviceName);
    }

    [Fact]
    public void ThenTheChoiceForAllApps()
    {
        using var t = new TestEngine(Main, Side, Tv);
        t.Pin(App, 3);
        t.Engine.SetDefaultFallback(FallbackModes.Monitor, t.Display(2));
        t.Plug(Main, Side);
        Assert.Equal(@"\\.\DISPLAY2", Target(t, t.Engine.Settings.Find(App))?.DeviceName);
    }

    [Fact]
    public void ThenTheMainMonitor()
    {
        using var t = new TestEngine(Main, Side, Tv);
        t.Pin(App, 3);
        t.Plug(Main, Side);
        var target = Target(t, t.Engine.Settings.Find(App));
        Assert.True(target?.IsPrimary);
    }

    [Fact]
    public void LeaveItAndMainForThisAppOverrideTheChoiceForAllApps()
    {
        using var t = new TestEngine(Main, Side, Tv);
        t.Pin(App, 3);
        t.Engine.SetDefaultFallback(FallbackModes.Monitor, t.Display(2));
        t.Plug(Main, Side);

        t.Engine.SetAppFallback(App, FallbackModes.Leave, null);
        Assert.Null(Target(t, t.Engine.Settings.Find(App)));

        t.Engine.SetAppFallback(App, FallbackModes.Main, null);
        Assert.True(Target(t, t.Engine.Settings.Find(App))?.IsPrimary);
    }

    [Fact]
    public void TreatAsSwitchedOffSendsNewWindowsElsewhereUntilItIsBackOn()
    {
        using var t = new TestEngine(Main, Side, Tv);
        t.Pin(App, 3);
        t.Engine.SetMonitorSwitchedOff(t.Display(3), true);
        Assert.True(Target(t, t.Engine.Settings.Find(App))?.IsPrimary);

        t.Engine.SetMonitorSwitchedOff(t.Display(3), false);
        Assert.Equal(@"\\.\DISPLAY3", Target(t, t.Engine.Settings.Find(App))?.DeviceName);
    }

    [Fact]
    public void TheTvComingBackUnderAnotherNumberIsStillItsMonitor()
    {
        using var t = new TestEngine(Main, Side, Tv);
        t.Pin(App, 3);
        t.Plug(Main, Side);                       // TV off
        t.Plug(Main, Side, Tv with { Number = 5 }); // back on, and Windows numbered it 5
        Assert.Equal(@"\\.\DISPLAY5", Target(t, t.Engine.Settings.Find(App))?.DeviceName);
    }

    [Fact]
    public void AppsItHasNotLearnedOpenOnTheMainMonitorOrStayPut()
    {
        using var t = new TestEngine(Main, Side);
        Assert.True(Target(t, null)?.IsPrimary);
        t.Engine.SetUnknownApps(toMain: false, followMouse: false);
        Assert.Null(Target(t, null));
    }

    // ------------------------------------------------------------------ learning guards

    [Fact]
    public void LearningWaitsAfterMonitorsChange()
    {
        if (Engine.IsRemoteSession) return; // (in a Remote Desktop session learning is always paused)
        using var t = new TestEngine(Main, Side, Tv);
        Assert.Null(t.Engine.LearningBlockedReason(DateTime.UtcNow));

        t.Plug(Main, Side, Tv with { Left = 5000 }); // the TV moved: a new layout
        Assert.NotNull(t.Engine.LearningBlockedReason(DateTime.UtcNow));
        Assert.NotNull(t.Engine.LearningBlockedReason(DateTime.UtcNow.AddSeconds(25)));
        Assert.Null(t.Engine.LearningBlockedReason(DateTime.UtcNow.AddSeconds(31)));
    }

    [Fact]
    public void NothingIsLearnedWithOneMonitor()
    {
        using var t = new TestEngine(Main);
        Assert.NotNull(t.Engine.LearningBlockedReason(DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public void ATemporaryMonitorDoesNotCountAsASecondMonitor()
    {
        using var t = new TestEngine(Main, new FakeMonitor(2, "Wireless", 1920, Output: MonitorTarget.OutputMiracast));
        Assert.True(t.Display(2).IsTemporary);
        Assert.NotNull(t.Engine.LearningBlockedReason(DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public void ANameChangeAloneIsNotAMonitorChange()
    {
        if (Engine.IsRemoteSession) return;
        using var t = new TestEngine(Main, Side);
        t.Plug(Main, Side with { Name = "Side (renamed)" }); // same place, same port: the layout didn't change
        Assert.Null(t.Engine.LearningBlockedReason(DateTime.UtcNow));
    }

    // ------------------------------------------------------------------ monitors changing during a move

    [Fact]
    public void AMoveStartedBeforeADisplayChangeIsStale()
    {
        using var t = new TestEngine(Main, Side, Tv);
        var stillCurrent = t.Engine.StillCurrent(t.Engine.TopologyGeneration);
        Assert.True(stillCurrent());

        t.Engine.OnDisplaySettingsChanged(); // Windows says the displays changed
        Assert.False(stillCurrent());
    }

    [Fact]
    public void ANewLayoutSeenWhileMovingMakesTheMoveStale()
    {
        using var t = new TestEngine(Main, Side, Tv);
        var stillCurrent = t.Engine.StillCurrent(t.Engine.TopologyGeneration);
        t.Plug(Main, Side, Tv);  // read again, nothing changed
        Assert.True(stillCurrent());
        t.Plug(Main, Side);      // the TV went away before Windows said so
        Assert.False(stillCurrent());
    }

    // ------------------------------------------------------------------ saved identity

    [Fact]
    public void PinningSavesTheHardwareIdentity()
    {
        using var t = new TestEngine(Main, Side, Tv);
        var rule = t.Pin(App, 3);
        Assert.Equal("port-tv", rule.MonitorHardware?.DevicePath);
        Assert.Equal("3", rule.MonitorHardware?.Serial);
        Assert.True(rule.Pinned);
        // ... and it's on disk.
        var reread = SettingsStore.TryReload();
        Assert.Equal("port-tv", reread?.Find(App)?.MonitorHardware?.DevicePath);
    }

    [Fact]
    public void YourNumbersDecideTheOrder()
    {
        using var t = new TestEngine(Main, Side, Tv);
        t.Engine.RenameMonitor(t.Display(3), "TV", 1);
        Assert.Equal(@"\\.\DISPLAY3", t.Engine.Monitors.First().DeviceName);
        Assert.Equal("1 · TV", t.Display(3).Name);
    }
}
