using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Pianyu.App.Services;

public sealed class ClipboardService : IDisposable
{
    private HwndSource? _source;
    private DispatcherTimer? _countdownTimer;
    private DateTimeOffset? _listeningUntil;
    private string? _lastCandidate;

    public event EventHandler<string>? CandidateAvailable;
    public event EventHandler<TimeSpan>? ListeningTick;
    public event EventHandler? ListeningStopped;
    public bool IsListening => _listeningUntil > DateTimeOffset.Now;

    public void Attach(Window owner)
    {
        var helper = new WindowInteropHelper(owner);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
    }

    public string? ReadText()
    {
        try { return Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null; }
        catch (ExternalException) { return null; }
    }

    public bool SetText(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(30 * (attempt + 1));
            }
        }
        return false;
    }

    public void StartListening(TimeSpan duration)
    {
        if (_source is null) throw new InvalidOperationException("剪贴板服务尚未附加到窗口。");
        if (!NativeMethods.AddClipboardFormatListener(_source.Handle)) throw new InvalidOperationException("无法启动剪贴板监听。");
        _listeningUntil = DateTimeOffset.Now.Add(duration);
        _countdownTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, CountdownTimerOnTick, _source.Dispatcher);
        _countdownTimer.Start();
        CountdownTimerOnTick(this, EventArgs.Empty);
    }

    public void StopListening()
    {
        _listeningUntil = null;
        _countdownTimer?.Stop();
        if (_source is not null) NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        ListeningStopped?.Invoke(this, EventArgs.Empty);
    }

    private void CountdownTimerOnTick(object? sender, EventArgs e)
    {
        if (_listeningUntil is null) return;
        var remaining = _listeningUntil.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            StopListening();
            return;
        }
        ListeningTick?.Invoke(this, remaining);
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmClipboardUpdate && IsListening)
        {
            var text = ReadText()?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, _lastCandidate, StringComparison.Ordinal))
            {
                _lastCandidate = text;
                CandidateAvailable?.Invoke(this, text);
            }
        }
        return nint.Zero;
    }

    public void Dispose()
    {
        StopListening();
        if (_source is not null) _source.RemoveHook(WndProc);
    }

    private static class NativeMethods
    {
        internal const int WmClipboardUpdate = 0x031D;
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AddClipboardFormatListener(nint hwnd);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveClipboardFormatListener(nint hwnd);
    }
}
