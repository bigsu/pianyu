using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Pianyu.Core;

namespace Pianyu.App.Services;

public sealed class ShortcutService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private readonly Dictionary<int, ShortcutDefinition> _registered = [];
    private HwndSource? _source;
    private int _nextId = 100;

    public event EventHandler<string>? Triggered;

    public static IReadOnlyList<ShortcutDefinition> Defaults { get; } =
    [
        new("toggle", "显示/隐藏片语", "Ctrl+Alt+Space", "Ctrl+Alt+Space", ShortcutScope.Global),
        new("close", "关闭窗口", "Esc", "Esc", ShortcutScope.Local),
        new("new", "新建片段", "Ctrl+N", "Ctrl+N", ShortcutScope.Local),
        new("capture", "保存当前剪贴板", "Ctrl+Alt+S", "Ctrl+Alt+S", ShortcutScope.Global),
        new("search", "聚焦搜索", "Ctrl+F", "Ctrl+F", ShortcutScope.Local),
        new("copy_close", "复制并关闭", "Space", "Space", ShortcutScope.Local),
        new("paste", "直接粘贴", "Shift+Enter", "Shift+Enter", ShortcutScope.Local),
        new("copy_keep", "复制并保持打开", "Enter", "Enter", ShortcutScope.Local),
        new("edit", "编辑片段", "E", "E", ShortcutScope.Local),
        new("delete", "删除片段", "Del", "Del", ShortcutScope.Local),
    ];

    public void Attach(nint windowHandle)
    {
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);
    }

    public IReadOnlyDictionary<string, string> RegisterGlobals(IEnumerable<ShortcutDefinition> definitions)
    {
        UnregisterAll();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions.Where(item => item.Scope == ShortcutScope.Global && !string.IsNullOrWhiteSpace(item.Gesture)))
        {
            if (!TryParseGesture(definition.Gesture, out var modifiers, out var key, out var error))
            {
                errors[definition.ActionId] = error;
                continue;
            }
            var id = _nextId++;
            if (_source is null || !NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | HotKeyModifiers.NoRepeat, KeyInterop.VirtualKeyFromKey(key)))
            {
                errors[definition.ActionId] = "该快捷键已被系统或其他程序占用";
                continue;
            }
            _registered[id] = definition;
        }
        return errors;
    }

    public void SuspendGlobals() => UnregisterAll();

    public static string? FindInternalConflict(string actionId, string gesture, IEnumerable<ShortcutDefinition> definitions)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return null;
        var conflict = definitions.FirstOrDefault(item => !string.Equals(item.ActionId, actionId, StringComparison.OrdinalIgnoreCase) && string.Equals(Normalize(item.Gesture), Normalize(gesture), StringComparison.OrdinalIgnoreCase));
        return conflict is null ? null : $"与“{conflict.DisplayName}”冲突";
    }

    public static bool GestureMatches(string configured, string actual) => string.Equals(Normalize(configured), Normalize(actual), StringComparison.OrdinalIgnoreCase);

    public static bool TryMigrateLegacyCopyGestures(IReadOnlyDictionary<string, string> stored, out Dictionary<string, string> migrated)
    {
        migrated = new Dictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase);
        if (!stored.TryGetValue("copy_close", out var close) || !stored.TryGetValue("copy_keep", out var keep) ||
            !GestureMatches(close, "Enter") || !GestureMatches(keep, "Space")) return false;
        migrated["copy_close"] = "Space";
        migrated["copy_keep"] = "Enter";
        return true;
    }

    public static bool TryParseGesture(string gesture, out HotKeyModifiers modifiers, out Key key, out string error)
    {
        modifiers = HotKeyModifiers.None;
        key = Key.None;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(gesture)) return true;
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotKeyModifiers.Control; break;
                case "alt": modifiers |= HotKeyModifiers.Alt; break;
                case "shift": modifiers |= HotKeyModifiers.Shift; break;
                case "win" or "windows": modifiers |= HotKeyModifiers.Windows; break;
                case "del": key = Key.Delete; break;
                default:
                    if (Enum.TryParse<Key>(part, true, out var parsed)) key = parsed;
                    else
                    {
                        var converter = new KeyConverter();
                        try { key = (Key)(converter.ConvertFromString(part) ?? Key.None); }
                        catch { error = "无法识别这个组合键"; return false; }
                    }
                    break;
            }
        }
        if (key == Key.None || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            error = "组合键需要包含一个非修饰键";
            return false;
        }
        return true;
    }

    public static string FromKeyEvent(Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (key == Key.System) key = Keyboard.FocusedElement is null ? Key.None : KeyInterop.KeyFromVirtualKey(KeyInterop.VirtualKeyFromKey(key));
        var values = new List<string>();
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) values.Add("Ctrl");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) values.Add("Alt");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) values.Add("Shift");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows)) values.Add("Win");
        if (key != Key.None && key is not Key.LeftCtrl and not Key.RightCtrl and not Key.LeftAlt and not Key.RightAlt and not Key.LeftShift and not Key.RightShift and not Key.LWin and not Key.RWin) values.Add(key.ToString());
        return string.Join('+', values);
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotKey && _registered.TryGetValue(wParam.ToInt32(), out var definition))
        {
            Triggered?.Invoke(this, definition.ActionId);
            handled = true;
        }
        return nint.Zero;
    }

    private void UnregisterAll()
    {
        if (_source is not null) foreach (var id in _registered.Keys) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private static string Normalize(string gesture) => gesture
        .Replace("Control", "Ctrl", StringComparison.OrdinalIgnoreCase)
        .Replace("Escape", "Esc", StringComparison.OrdinalIgnoreCase)
        .Replace("Return", "Enter", StringComparison.OrdinalIgnoreCase)
        .Replace("Delete", "Del", StringComparison.OrdinalIgnoreCase)
        .Replace(" ", string.Empty)
        .ToLowerInvariant();

    public void Dispose()
    {
        UnregisterAll();
        if (_source is not null) _source.RemoveHook(WndProc);
    }

    [Flags]
    public enum HotKeyModifiers : uint
    {
        None = 0, Alt = 0x0001, Control = 0x0002, Shift = 0x0004, Windows = 0x0008, NoRepeat = 0x4000
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(nint hwnd, int id, HotKeyModifiers modifiers, int virtualKey);
        [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    }
}
