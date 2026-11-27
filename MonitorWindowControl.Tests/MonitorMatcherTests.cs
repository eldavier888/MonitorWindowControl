using System.Collections.Generic;
using Xunit;

namespace MonitorWindowControl.Tests;

/// <summary>Finding a saved monitor among the connected ones, whatever Windows' numbers say.</summary>
public class MonitorMatcherTests
{
    private static List<MonitorInfo> Connected(params FakeMonitor[] monitors) => new FakeMonitorSource { Monitors = new(monitors) }.Enumerate();

    private static string? Find(List<MonitorInfo> monitors, string? name, int? number, MonitorHardware? hw = null) =>
        MonitorMatcher.Find(monitors, name, number is int n ? @"\\.\DISPLAY" + n : null, hw)?.DeviceName;

    [Fact]
    public void SamePortWinsWhenWindowsRenumbers()
    {
        // Saved as DISPLAY2 on port A; Windows now calls it DISPLAY3 and put another monitor on DISPLAY2.
        var now = Connected(new(1, "Main", 0, true, "port-main"), new(2, "Other", 1920, Port: "port-b"), new(3, "Dell", 3840, Port: "port-a"));
        Assert.Equal(@"\\.\DISPLAY3", Find(now, "Dell", 2, new MonitorHardware { DevicePath = "port-a" }));
    }

    [Fact]
    public void SameModelAndSerialFollowsACableMovedToAnotherPort()
    {
        var now = Connected(new(1, "Main", 0, true, "p1"), new(4, "LG TV", 1920, Port: "new-port", Model: "1E6D-0001", Serial: "123"));
        var saved = new MonitorHardware { DevicePath = "old-port", Model = "1E6D-0001", Serial = "123" };
        Assert.Equal(@"\\.\DISPLAY4", Find(now, "LG TV", 2, saved));
    }

    [Fact]
    public void IdenticalModelsAreToldApartBySerialEvenWhenSwapped()
    {
        // Two identical monitors whose cables were swapped: the serial number shows which is which.
        var now = Connected(new(1, "Dell (DISPLAY1)", 0, true, "port-a", "10AC-A0B1", "AAA"),
                            new(2, "Dell (DISPLAY2)", 1920, false, "port-b", "10AC-A0B1", "BBB"));
        var savedB = new MonitorHardware { DevicePath = "port-a", Model = "10AC-A0B1", Serial = "BBB" };
        Assert.Equal(@"\\.\DISPLAY2", Find(now, "Dell (DISPLAY1)", 1, savedB));
    }

    [Fact]
    public void IdenticalModelsWithoutHardwareIdentityTrustTheNumber()
    {
        // What older versions saved: only the model name and Windows' number.
        var now = Connected(new(1, "Dell U2720Q (DISPLAY1)", 0, true), new(2, "Dell U2720Q (DISPLAY2)", 1920));
        Assert.Equal(@"\\.\DISPLAY2", Find(now, "Dell U2720Q", 2));
        Assert.Equal(@"\\.\DISPLAY1", Find(now, "Dell U2720Q", 1));
    }

    [Fact]
    public void ModelNameWinsOverANumberWindowsGaveToAnotherMonitor()
    {
        var now = Connected(new(1, "Main", 0, true), new(3, "Samsung", 1920), new(4, "LG TV", 3840));
        Assert.Equal(@"\\.\DISPLAY4", Find(now, "LG TV", 3));
    }

    [Fact]
    public void AMonitorThatIsNotConnectedStaysNotConnected()
    {
        // The TV is off: its old number now belongs to another model, which must not be used instead.
        var now = Connected(new(1, "Main", 0, true), new(3, "Samsung", 1920));
        Assert.Null(Find(now, "LG TV", 3));
    }

    [Fact]
    public void GenericNamesGoByNumber()
    {
        // Windows couldn't read a model name ("Display 3"): the number is all there is.
        var now = Connected(new(1, "Main", 0, true), new(3, "Display 3", 1920));
        Assert.Equal(@"\\.\DISPLAY3", Find(now, "Display 3", 3));
        // A monitor saved by model name that now shows up without one, on the same number, is still found.
        Assert.Equal(@"\\.\DISPLAY3", Find(now, "LG TV", 3));
    }

    [Fact]
    public void ADifferentSerialOnTheSamePortIsAnotherMonitor()
    {
        // Another monitor of the same model is plugged into the port the saved one used: not the same monitor.
        var now = Connected(new(1, "Main", 0, true, "p1"), new(2, "Dell", 1920, false, "port-a", "10AC-A0B1", "NEW"));
        var saved = new MonitorHardware { DevicePath = "port-a", Model = "10AC-A0B1", Serial = "OLD" };
        Assert.Null(Find(now, "Dell", 2, saved));
    }

    [Fact]
    public void OnlyANumberSavedFindsThatNumber()
    {
        var now = Connected(new(1, "Main", 0, true), new(2, "Side", 1920));
        Assert.Equal(@"\\.\DISPLAY2", Find(now, null, 2));
        Assert.Null(Find(now, null, 5));
    }
}
