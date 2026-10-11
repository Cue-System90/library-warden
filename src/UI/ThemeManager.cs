using System.Windows;
using LibraryWarden.Core;
using Microsoft.Win32;

namespace LibraryWarden.UI;

/// <summary>Hell/Dunkel/System und die Zoomstufe der Oberfläche.
/// Standard-Steuerelemente (Textfelder, Auswahllisten, Bildlaufleisten, Menüs) kommen aus dem Fluent-Design von WPF,
/// alles Eigene aus Palette.Light/Dark.xaml.</summary>
public static class ThemeManager
{
    static ThemeChoice _choice = ThemeChoice.System;
    static double _manualScale;
    static global::Windows.UI.ViewManagement.UISettings? _uiSettings;
    static double _textScale = 1.0;
    static bool _initialized;

    public static bool IsDark { get; private set; }
    public static ThemeChoice Choice => _choice;

    /// <summary>Wirksamer Zoomfaktor (fester Wert oder Windows-Textgröße).</summary>
    public static double Scale => _manualScale > 0 ? _manualScale : _textScale;
    public static double TextScaleFactor => _textScale;
    public static bool FollowsSystemScale => _manualScale <= 0;

    public static event Action? ThemeChanged;
    public static event Action? ScaleChanged;

    public static void Initialize(ThemeChoice theme, double scale)
    {
        _choice = theme;
        _manualScale = scale;
        if (!_initialized)
        {
            _initialized = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color && _choice == ThemeChoice.System)
                    Application.Current?.Dispatcher.InvokeAsync(Apply);
            };
            try
            {
                _uiSettings = new global::Windows.UI.ViewManagement.UISettings();
                _textScale = Clamp(_uiSettings.TextScaleFactor);
                _uiSettings.TextScaleFactorChanged += (s, _) =>
                {
                    var factor = Clamp(s.TextScaleFactor);
                    Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        _textScale = factor;
                        if (FollowsSystemScale) ScaleChanged?.Invoke();
                    });
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"Windows-Textgröße nicht lesbar: {ex.Message}");
            }
        }
        Apply();
    }

    public static void SetTheme(ThemeChoice choice)
    {
        _choice = choice;
        Apply();
    }

    public static void SetScale(double scale)
    {
        _manualScale = scale <= 0 ? 0 : Clamp(scale);
        ScaleChanged?.Invoke();
    }

    static double Clamp(double v) => Math.Round(Math.Clamp(v, 0.7, 2.5), 2);

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    static void Apply()
    {
        var app = Application.Current;
        if (app == null) return;
        bool dark = _choice == ThemeChoice.Dark || (_choice == ThemeChoice.System && SystemPrefersDark());
        bool changed = dark != IsDark || !_applied;
        IsDark = dark;
        _applied = true;

        try { app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light; }
        catch (Exception ex) { Log.Warn($"Fluent-Design nicht verfügbar: {ex.Message}"); }

        var dictionaries = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/Palette.{(dark ? "Dark" : "Light")}.xaml") };
        var old = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Palette.") == true);
        if (old != null) dictionaries[dictionaries.IndexOf(old)] = palette;
        else dictionaries.Add(palette);

        // Standard-Steuerelemente (Häkchen, Fokusrahmen) behalten die Windows-Akzentfarbe des Nutzers.
        foreach (Window window in app.Windows) WindowHelpers.ApplyDarkFrame(window, dark);
        if (changed) ThemeChanged?.Invoke();
    }

    static bool _applied;
}
