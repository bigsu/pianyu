using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Pianyu.App.Services;

public sealed class ForegroundAppService
{
    public nint GetForegroundWindowHandle() => NativeMethods.GetForegroundWindow();

    public string? GetForegroundProcessName()
    {
        return GetProcessName(NativeMethods.GetForegroundWindow());
    }

    public string? GetProcessName(nint handle)
    {
        if (handle == nint.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        try { return Process.GetProcessById((int)processId).ProcessName; }
        catch { return null; }
    }

    public string? GetForegroundWindowTitle()
    {
        var handle = NativeMethods.GetForegroundWindow();
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0) return null;
        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public bool RestoreForeground(nint handle) => handle != nint.Zero && NativeMethods.SetForegroundWindow(handle);

    private static class NativeMethods
    {
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hWnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] internal static extern int GetWindowTextLength(nint hWnd);
    }
}
