using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MyPIN.Services;

/// <summary>
/// Cores dos menus seguindo o tema do Windows (claro/escuro). As cores ficam em
/// Application.Resources e são usadas via DynamicResource, então mudam na hora
/// quando você troca o tema do Windows.
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    public static void Init()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;

    static void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            Application.Current?.Dispatcher.BeginInvoke(Apply);
    }

    static bool ReadIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply()
    {
        IsDark = ReadIsDark();
        var r = Application.Current.Resources;
        if (IsDark)
        {
            Set(r, "Menu.Background", "#2C2C2C");
            Set(r, "Menu.Border", "#454545");
            Set(r, "Menu.Foreground", "#F3F3F3");
            Set(r, "Menu.Hover", "#3D3D3D");
            Set(r, "Menu.Pressed", "#4A4A4A");
            Set(r, "Menu.Shortcut", "#A8A8A8");
            Set(r, "Menu.Separator", "#424242");
        }
        else
        {
            Set(r, "Menu.Background", "#F9F9F9");
            Set(r, "Menu.Border", "#DADADA");
            Set(r, "Menu.Foreground", "#1B1B1B");
            Set(r, "Menu.Hover", "#EAEAEA");
            Set(r, "Menu.Pressed", "#DEDEDE");
            Set(r, "Menu.Shortcut", "#6E6E6E");
            Set(r, "Menu.Separator", "#E3E3E3");
        }
    }

    static void Set(ResourceDictionary r, string key, string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        r[key] = b;
    }
}
