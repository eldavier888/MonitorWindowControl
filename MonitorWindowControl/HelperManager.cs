using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace MonitorWindowControl;

/// <summary>
/// The tiny helper (MonitorWindowControlHelper.exe) is packed inside this app, so there's still one file to download.
/// It's unpacked to %LocalAppData%\MonitorWindowControl when needed (a folder Windows' "Controlled folder access"
/// doesn't protect), and started / stopped from here.
/// </summary>
public static class HelperManager
{
    private const string HelperMutexName = @"Local\MonitorWindowControl.Helper";
    private const string HelperExitEventName = @"Local\MonitorWindowControl.HelperExit";
    private const string ResourceName = "helper.exe";

    public static bool IsAvailable => Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) != null;

    public static bool IsRunning() => Native.NamedMutexExists(HelperMutexName);

    /// <summary>
    /// Unpacks the helper if it's missing or a different version (e.g. after updating the app). If an older
    /// background helper was running, it's stopped, replaced and started again. Returns its path, or null on failure.
    /// </summary>
    public static string? EnsureExtracted()
    {
        try
        {
            using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (res == null) { Log.Write("This build doesn't contain the helper."); return null; }
            var target = new FileInfo(Paths.HelperExe);
            if (target.Exists && target.Length == res.Length && SameBytes(res, target.FullName)) return target.FullName;

            bool wasRunning = IsRunning();
            if (wasRunning) Stop(); // replace it, then start the new version below
            Directory.CreateDirectory(Paths.HelperDir);
            CleanUpOldCopies();
            string tmp = target.FullName + ".new";
            res.Position = 0;
            using (var f = File.Create(tmp)) res.CopyTo(f);
            Replace(tmp, target.FullName);
            Log.Write($"Helper unpacked to {target.FullName}.");
            if (wasRunning) StartProcess(target.FullName);
            return target.FullName;
        }
        catch (Exception ex)
        {
            Log.Write("Could not unpack the helper: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Puts the new exe in place. If the old one is still in use (a launch shortcut running it, or a helper that
    /// hasn't quite exited), it can't be overwritten but can be renamed aside; the old copy is deleted later.
    /// </summary>
    private static void Replace(string tmp, string target)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { File.Move(tmp, target, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
        File.Move(target, target + ".old-" + Guid.NewGuid().ToString("N")[..8], false);
        File.Move(tmp, target, false);
    }

    private static void CleanUpOldCopies()
    {
        foreach (var old in Directory.EnumerateFiles(Paths.HelperDir, "MonitorWindowControlHelper.exe.old-*"))
            try { File.Delete(old); } catch { /* still in use; next time */ }
    }

    private static bool SameBytes(Stream res, string path)
    {
        res.Position = 0;
        using var f = File.OpenRead(path);
        var a = new byte[81920]; var b = new byte[81920];
        while (true)
        {
            int na = res.ReadAtLeast(a, a.Length, throwOnEndOfStream: false);
            int nb = f.ReadAtLeast(b, b.Length, throwOnEndOfStream: false);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!a.AsSpan(0, na).SequenceEqual(b.AsSpan(0, nb))) return false;
        }
    }

    /// <summary>Makes sure the current version of the background helper is running. False if it couldn't be started.</summary>
    public static bool EnsureRunning()
    {
        string? exe = EnsureExtracted(); // also replaces (and restarts) an outdated running helper
        if (exe == null) return false;
        return IsRunning() || StartProcess(exe);
    }

    /// <summary>
    /// Opens an app group: the helper starts each of its apps on its monitor, then exits (like the group's shortcut).
    /// Returns an error message, or null.
    /// </summary>
    public static string? OpenGroup(AppGroup group)
    {
        string? exe = EnsureExtracted();
        if (exe == null) return "The helper that opens groups couldn't be unpacked (see the log).";
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--group " + ShortcutManager.QuoteArg(group.Id)) { UseShellExecute = false })?.Dispose();
            Log.Write($"Opening the app group \"{group.Name}\".");
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not open the app group \"{group.Name}\": {ex.Message}");
            return "The group couldn't be opened: " + ex.Message;
        }
    }

    private static bool StartProcess(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
            for (int i = 0; i < 40 && !IsRunning(); i++) Thread.Sleep(50); // wait until it's up (≤ 2 s)
            if (!IsRunning()) Log.Write("The helper was started but didn't stay running (see the lines above).");
            return IsRunning();
        }
        catch (Exception ex)
        {
            Log.Write("Could not start the helper: " + ex.Message);
            return false;
        }
    }

    /// <summary>Asks the background helper to exit and waits (briefly) until it has.</summary>
    public static void Stop()
    {
        if (!IsRunning()) return;
        try
        {
            using var ev = EventWaitHandle.OpenExisting(HelperExitEventName);
            ev.Set();
        }
        catch { /* not running after all */ }
        for (int i = 0; i < 60 && IsRunning(); i++) Thread.Sleep(50); // ≤ 3 s
        if (IsRunning()) Log.Write("The helper didn't stop when asked (if it was started with \"Run as administrator\", only a copy run as administrator can stop it).");
    }
}
