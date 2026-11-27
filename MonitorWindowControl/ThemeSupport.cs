using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MonitorWindowControl;

/// <summary>
/// Makes the settings window follow Windows' own look, live: light or dark mode, contrast themes (system colours and
/// no Mica), "Transparency effects" off (a solid background), and Accessibility → Text size. Nothing polls: it only
/// reacts when Windows says a setting changed.
/// </summary>
internal static class ThemeSupport
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AccessibilityKey = @"Software\Microsoft\Accessibility";

    /// <summary>A contrast theme is on (Aquatic, Desert, Dusk, Night sky…).</summary>
    public static bool HighContrast => SystemParameters.HighContrast;

    /// <summary>Settings → Personalization → Colors → Transparency effects.</summary>
    public static bool TransparencyOn => ReadDword(PersonalizeKey, "EnableTransparency", 1) != 0;

    /// <summary>Apps use light mode.</summary>
    public static bool AppsLight => ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) != 0;

    /// <summary>The taskbar (and so the tray) uses light mode.</summary>
    public static bool TaskbarLight => ReadDword(PersonalizeKey, "SystemUsesLightTheme", 0) != 0;

    /// <summary>Settings → Accessibility → Text size, from 1.0 (100 %) to 2.25 (225 %).</summary>
    public static double TextScale => Math.Clamp(ReadDword(AccessibilityKey, "TextScaleFactor", 100), 100, 225) / 100.0;

    /// <summary>Applies Windows' look to the window now, and again whenever it changes, until the window closes.</summary>
    public static void Follow(FluentWindow window)
    {
        string applied = "";
        void Update()
        {
            string state = $"{HighContrast}|{TransparencyOn}|{AppsLight}|{TextScale}";
            if (state == applied) return; // (Windows reports many unrelated changes too)
            applied = state;
            Apply(window);
        }
        UserPreferenceChangedEventHandler onPreference = (_, _) => window.Dispatcher.BeginInvoke(Update);
        PropertyChangedEventHandler onSystem = (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) window.Dispatcher.BeginInvoke(Update);
        };
        SystemEvents.UserPreferenceChanged += onPreference;
        SystemParameters.StaticPropertyChanged += onSystem;
        window.Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= onPreference;
            SystemParameters.StaticPropertyChanged -= onSystem;
        };
        Update();
    }

    /// <summary>Theme, background and text size for the window as Windows is set now.</summary>
    private static void Apply(FluentWindow window)
    {
        bool contrast = HighContrast;
        var backdrop = contrast || !TransparencyOn ? WindowBackdropType.None : WindowBackdropType.Mica;
        var theme = contrast ? ApplicationTheme.HighContrast : AppsLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
        try
        {
            if (backdrop == WindowBackdropType.Mica) window.ClearValue(System.Windows.Controls.Control.BackgroundProperty); // (Mica draws it)
            ApplicationThemeManager.Apply(theme, backdrop, true);
            window.WindowBackdropType = backdrop;
            if (contrast) window.Background = SystemColors.WindowBrush;          // the contrast theme's own colours
            else if (backdrop == WindowBackdropType.None) window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
        }
        catch (Exception ex) { Log.Write("Could not apply the Windows theme: " + ex.Message); }
        ApplyTextScale(Application.Current?.Resources ?? window.Resources, TextScale);
    }

    /// <summary>
    /// Text size: the app's text styles and WPF-UI's controls get bigger together, in every window and dialog (icons
    /// and spacing stay the same, as in Windows Settings).
    /// </summary>
    private static void ApplyTextScale(ResourceDictionary resources, double scale)
    {
        resources["TextCaption"] = 12 * scale;
        resources["TextBody"] = 13 * scale;
        resources["TextBodyLarge"] = 14 * scale;
        resources["TextSubtitle"] = 16 * scale;
        resources["TextTitle"] = 28 * scale;
        resources["ControlContentThemeFontSize"] = 14 * scale;
    }

    private static int ReadDword(string key, string name, int fallback)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name) is int v ? v : fallback;
        }
        catch { return fallback; }
    }
}

/// <summary>Tells Narrator and other screen readers what just happened ("Saved", "Paused"…), without moving the focus.</summary>
internal static class Announcer
{
    /// <summary>Says <paramref name="text"/> through UI Automation, from <paramref name="source"/> (usually the window).</summary>
    public static void Announce(UIElement source, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var peer = UIElementAutomationPeer.FromElement(source) ?? UIElementAutomationPeer.CreatePeerForElement(source);
            peer?.RaiseNotificationEvent(System.Windows.Automation.AutomationNotificationKind.ActionCompleted,
                System.Windows.Automation.AutomationNotificationProcessing.ImportantMostRecent, text, "MonitorWindowControl.Status");
        }
        catch (Exception ex) { Log.Write("Could not announce a change: " + ex.Message); }
    }
}
