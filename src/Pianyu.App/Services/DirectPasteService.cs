using System.Runtime.InteropServices;

namespace Pianyu.App.Services;

public sealed class DirectPasteService(ClipboardService clipboard, ForegroundAppService foreground)
{
    public static int NativeInputStructureSize => Marshal.SizeOf<Input>();

    public async Task<(bool Success, string Message)> PasteAsync(string text, nint targetWindow, CancellationToken cancellationToken = default)
    {
        if (!clipboard.SetText(text)) return (false, "无法写入剪贴板，已取消直接粘贴。");
        await Task.Delay(80, cancellationToken);
        if (!foreground.RestoreForeground(targetWindow)) return (false, "无法切回原应用，内容已复制到剪贴板。");
        await Task.Delay(80, cancellationToken);

        var inputs = new[]
        {
            Input.KeyDown(VirtualKey.Control), Input.KeyDown(VirtualKey.V), Input.KeyUp(VirtualKey.V), Input.KeyUp(VirtualKey.Control)
        };
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        return sent == inputs.Length
            ? (true, "已粘贴到原应用")
            : (false, "直接粘贴失败，内容已保留在剪贴板。");
    }

    private enum InputType : uint { Keyboard = 1 }
    private enum VirtualKey : ushort { Control = 0x11, V = 0x56 }
    [Flags] private enum KeyFlags : uint { KeyUp = 0x0002 }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public InputType Type;
        public InputUnion Union;
        public static Input KeyDown(VirtualKey key) => new() { Type = InputType.Keyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)key } } };
        public static Input KeyUp(VirtualKey key) => new() { Type = InputType.Keyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)key, Flags = KeyFlags.KeyUp } } };
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey; public ushort ScanCode; public KeyFlags Flags; public uint Time; public nint ExtraInfo; }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint inputCount, [In] Input[] inputs, int size);
    }
}
