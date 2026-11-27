using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MonitorWindowControl.Tests;

/// <summary>settings.json: old files load unchanged, damaged ones are repaired or kept aside, and saves never collide.</summary>
public class SettingsTests
{
    private static AppSettings LoadFrom(string json, out bool existed)
    {
        TestFolder.Reset();
        File.WriteAllText(Paths.SettingsFile, json);
        return SettingsStore.Load(out existed);
    }

    [Fact]
    public void NoFileIsAFirstRunWithSafeDefaults()
    {
        TestFolder.Reset();
        var s = SettingsStore.Load(out bool existed);
        Assert.False(existed);
        Assert.True(s.Enabled);
        Assert.Equal(RunModes.Tray, s.RunMode);
        Assert.False(s.HotKeys.Enabled);          // keyboard shortcuts are off until you turn them on
        Assert.False(s.Notices.Learned);          // and so are notifications
        Assert.Null(s.KeepMainMonitor);
    }

    [Fact]
    public void AFileFromAnOlderVersionLoadsUnchanged()
    {
        const string old = """
            { "Version": 1, "Enabled": true, "LearnAutomatically": false,
              "Apps": [ { "Key": "chrome.exe", "DisplayName": "Chrome", "MonitorDevice": "\\\\.\\DISPLAY2", "MonitorName": "Dell", "Pinned": true } ] }
            """;
        var s = LoadFrom(old, out bool existed);
        Assert.True(existed);
        Assert.False(s.LearnAutomatically);
        var chrome = Assert.Single(s.Apps);
        Assert.Equal(@"\\.\DISPLAY2", chrome.MonitorDevice);
        Assert.Equal("Dell", chrome.MonitorName);
        Assert.True(chrome.Pinned);
        Assert.Null(chrome.MonitorHardware);
        // Everything newer gets its default.
        Assert.NotNull(s.HotKeys);
        Assert.NotNull(s.Notices);
        Assert.Empty(s.Groups);
        Assert.Empty(s.Setups);
        Assert.Empty(chrome.WindowRules);
        Assert.True(s.RestoreAfterSleep);
    }

    [Fact]
    public void AHandEditedFileIsRepaired()
    {
        const string damaged = """
            { "Apps": [ null, { "Key": "" }, { "Key": "a.exe", "AllowedMonitors": [ null ], "Place": "Sideways", "WaitMs": 42,
                        "WindowRules": [ null, { "Match": "Unknown", "Text": "x" }, { "Match": "TitleContains", "Text": "  " } ] },
                      { "Key": "A.EXE" } ],
              "MonitorNicknames": null, "MonitorOptions": [ null ], "HotKeys": null, "Notices": null,
              "Groups": [ { "Id": "", "Name": "No id" }, { "Id": "g", "Name": "Movie night", "Apps": [ null, { "Key": "a.exe" }, { "Key": "A.exe" } ] } ],
              "Setups": [ { "Id": "" }, { "Id": "s1", "Homes": [ { "Key": "a.exe" } ] } ], "UsualSetup": "gone",
              "RunMode": "Sometimes", "Fallback": "Monitor" }
            """;
        var s = LoadFrom(damaged, out _);
        var a = Assert.Single(s.Apps);                     // nulls, empty keys and the duplicate are gone
        Assert.Equal("a.exe", a.Key);
        Assert.Equal("a.exe", a.DisplayName);
        Assert.Empty(a.AllowedMonitors);
        Assert.Null(a.Place);                              // unknown "Place it" becomes centered
        Assert.Null(a.WaitMs);                             // impossible timing becomes automatic
        Assert.Empty(a.WindowRules);
        Assert.NotNull(s.MonitorNicknames);
        Assert.Empty(s.MonitorOptions);
        Assert.NotNull(s.HotKeys);
        Assert.NotNull(s.Notices);
        var g = Assert.Single(s.Groups);
        Assert.Single(g.Apps);
        var setup = Assert.Single(s.Setups);
        Assert.Empty(setup.Homes);                         // a home without a monitor is dropped
        Assert.Equal("s1", s.UsualSetup);
        Assert.Equal(RunModes.Tray, s.RunMode);
        Assert.Null(s.Fallback);                           // "a monitor" without a monitor means the main monitor
    }

    [Fact]
    public void ABrokenFileIsKeptAsideAndDefaultsAreUsed()
    {
        var s = LoadFrom("{ this is not json", out bool existed);
        Assert.True(existed);
        Assert.Empty(s.Apps);
        Assert.True(File.Exists(Paths.SettingsFile + ".broken"));
    }

    [Fact]
    public void SavedSettingsReadBackTheSame()
    {
        TestFolder.Reset();
        var s = new AppSettings { LearnAutomatically = false, Fallback = FallbackModes.Leave };
        s.Apps.Add(new AppRule { Key = "x.exe", DisplayName = "X", MonitorDevice = @"\\.\DISPLAY1", Pinned = true });
        SettingsStore.Save(s);
        var back = SettingsStore.Load(out bool existed);
        Assert.True(existed);
        Assert.False(back.LearnAutomatically);
        Assert.Equal(FallbackModes.Leave, back.Fallback);
        Assert.Equal("X", back.Find("X.EXE")?.DisplayName);   // app keys ignore case
    }

    [Fact]
    public void SavesUnderTheLockNeverLoseAChange()
    {
        TestFolder.Reset();
        SettingsStore.Save(new AppSettings());
        // Two "programs" add apps at the same time, each re-reading, changing and saving under the lock.
        Parallel.For(0, 2, worker =>
        {
            for (int i = 0; i < 15; i++)
                using (SettingsStore.Lock())
                {
                    var s = SettingsStore.TryReload() ?? new AppSettings();
                    s.Apps.Add(new AppRule { Key = $"w{worker}-{i}.exe" });
                    SettingsStore.Save(s);
                }
        });
        var final = SettingsStore.TryReload();
        Assert.NotNull(final);
        Assert.Equal(30, final!.Apps.Count);
        Assert.Equal(30, final.Apps.Select(a => a.Key).Distinct().Count());
    }

    [Fact]
    public void AnImportWithAChoiceThisVersionDoesNotKnowIsRefused()
    {
        TestFolder.Reset();
        string file = Path.Combine(Paths.Dir, "import.json");
        File.WriteAllText(file, """{ "RunMode": "Teleport" }""");
        Assert.Null(SettingsStore.ReadUntrusted(file, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ADailyBackupIsKeptWhenSaving()
    {
        TestFolder.Reset();
        SettingsStore.Save(new AppSettings());
        SettingsStore.WriteBackup(force: true);
        Assert.NotEmpty(SettingsStore.ListBackups());
    }
}
