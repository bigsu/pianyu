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
        ["Canvas"] = "#101416",
        ["Surface"] = "#171D1F",
        ["Raised"] = "#1E2528",
        ["Hover"] = "#263034",
        ["Border"] = "#344044",
        ["BorderStrong"] = "#526267",
        ["TextPrimary"] = "#F1F6F4",
        ["TextSecondary"] = "#AEBAB7",
        ["TextMuted"] = "#7F8D89",
        ["Accent"] = "#35D0B0",
        ["AccentHover"] = "#55E1C4",
        ["AccentDark"] = "#123E36",
        ["AccentForeground"] = "#071512",
        ["Warning"] = "#E9AC56",
        ["Danger"] = "#F07070",
        ["Success"] = "#63D693"
    };

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["Canvas"] = "#F4F7F6",
        ["Surface"] = "#FFFFFF",
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
