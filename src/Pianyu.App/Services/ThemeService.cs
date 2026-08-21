using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Pianyu.Core;

namespace Pianyu.App.Services;

public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["Canvas"] = "#1A1A19",
        ["Header"] = "#1A1A19",
        ["Surface"] = "#2E2E2D",
        ["Search"] = "#262625",
        ["Raised"] = "#242424",
        ["Hover"] = "#2B2B2A",
        ["Border"] = "#363535",
        ["BorderStrong"] = "#4A4A49",
        ["TextPrimary"] = "#F0F0EF",
        ["TextSecondary"] = "#B4B4B2",
        ["TextMuted"] = "#8E8E8C",
        ["Accent"] = "#9DE1ED",
        ["AccentHover"] = "#B2EAF2",
        ["AccentDark"] = "#24393C",
        ["AccentForeground"] = "#101A1B",
        ["Warning"] = "#D6A051",
        ["Danger"] = "#E77B78",
        ["Success"] = "#65C68B"
    };

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["Canvas"] = "#F4F7F6",
        ["Header"] = "#F4F7F6",
        ["Surface"] = "#FFFFFF",
        ["Search"] = "#EAF0EE",
        ["Raised"] = "#EAF0EE",
        ["Hover"] = "#DCE8E4",
        ["Border"] = "#C5D1CE",
        ["BorderStrong"] = "#94A8A3",
        ["TextPrimary"] = "#16201E",
        ["TextSecondary"] = "#42514E",
        ["TextMuted"] = "#687773",
        ["Accent"] = "#087D69",
        ["AccentHover"] = "#066653",
        ["AccentDark"] = "#CFEDE6",
        ["AccentForeground"] = "#FFFFFF",
        ["Warning"] = "#975900",
        ["Danger"] = "#B4232C",
        ["Success"] = "#147A46"
    };

    public ThemeService() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    public ThemeMode RequestedMode { get; private set; } = ThemeMode.Dark;
    public bool IsLight { get; private set; }

    public void Apply(ThemeMode mode)
    {
        RequestedMode = mode;
        IsLight = mode == ThemeMode.Light || mode == ThemeMode.System && SystemUsesLightTheme();
        var resources = System.Windows.Application.Current.Resources;
        foreach (var (name, value) in IsLight ? LightPalette : DarkPalette)
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            resources[$"{name}Color"] = color;
            if (resources[$"{name}Brush"] is SolidColorBrush brush && !brush.IsFrozen)
                brush.Color = color;
            else
                resources[$"{name}Brush"] = new SolidColorBrush(color);
        }
        ApplyWindowChrome();
    }

    public void ApplyWindowChrome(Window? window = null)
    {
        IEnumerable<Window> windows = window is null
            ? System.Windows.Application.Current.Windows.Cast<Window>()
            : new[] { window };
        foreach (var candidate in windows)
        {
            var handle = new WindowInteropHelper(candidate).Handle;
            if (handle == nint.Zero) continue;
            var value = IsLight ? 0 : 1;
            if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (RequestedMode == ThemeMode.System)
            System.Windows.Application.Current.Dispatcher.Invoke(() => Apply(ThemeMode.System));
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
