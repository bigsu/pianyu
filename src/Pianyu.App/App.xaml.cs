using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Pianyu.App.Services;
using Pianyu.App.Views;
using Pianyu.Core;
using Forms = System.Windows.Forms;

namespace Pianyu.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;
    public AppSettings CurrentSettings { get; private set; } = new();
    public IReadOnlyList<ShortcutDefinition> CurrentShortcuts { get; private set; } = ShortcutService.Defaults;

    public AppServices Services { get; private set; } = null!;
    public MainWindow MainAppWindow { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, "Local.Pianyu.Desktop.Singleton", out var ownsMutex);
        if (!ownsMutex)
        {
            System.Windows.MessageBox.Show("片语已经在运行。可使用全局快捷键显示窗口。", "片语", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Services = new AppServices();
        // Apply the same native Windows caption bar to every WPF window,
        // including dialogs created after the main window.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnAnyWindowLoaded));
        MainAppWindow = new MainWindow(Services);
        MainWindow = MainAppWindow;
        MainAppWindow.SourceInitialized += OnMainWindowSourceInitialized;
        MainAppWindow.Closing += (_, args) =>
        {
            if (_isExiting) return;
            args.Cancel = true;
            RequestMainClose();
        };
        ConfigureTrayIcon();
        MainAppWindow.Show();
    }

    private void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
            Services.Theme.ApplyWindowChrome(window);
    }

    private async void OnMainWindowSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(MainAppWindow).Handle;
        Services.Shortcuts.Attach(handle);
        Services.Clipboard.Attach(MainAppWindow);
        Services.Shortcuts.Triggered += OnGlobalShortcut;
        await ReloadGlobalShortcutsAsync();
    }

    public async Task<IReadOnlyDictionary<string, string>> ReloadGlobalShortcutsAsync()
    {
        IReadOnlyDictionary<string, string> stored;
        try
        {
            stored = await Services.Repository.GetShortcutsAsync();
            if (ShortcutService.TryMigrateLegacyCopyGestures(stored, out var migrated))
            {
                await Services.Repository.SaveShortcutAsync("copy_close", migrated["copy_close"]);
                await Services.Repository.SaveShortcutAsync("copy_keep", migrated["copy_keep"]);
                stored = migrated;
            }
            CurrentSettings = await Services.Repository.GetAppSettingsAsync();
            ApplyVisualSettings(CurrentSettings);
        }
        catch (Exception ex)
        {
            stored = new Dictionary<string, string>();
            CurrentSettings = new AppSettings();
            MainAppWindow.ShowStatus($"数据库打开失败：{ex.Message}", true);
        }
        var definitions = ShortcutService.Defaults.Select(item => stored.TryGetValue(item.ActionId, out var gesture) ? item with { Gesture = gesture } : item).ToList();
        CurrentShortcuts = definitions;
        var errors = Services.Shortcuts.RegisterGlobals(definitions);
        if (errors.Count > 0) MainAppWindow.ShowStatus("部分全局快捷键被占用，可在设置中修改。", true);
        return errors;
    }

    public void ApplyFontScale(double scale) => Resources["AppFontSize"] = 14d * Math.Clamp(scale, 0.85, 1.35);

    private void ApplyVisualSettings(AppSettings settings)
    {
        ApplyFontScale(settings.FontScale);
        Services.Theme.Apply(settings.Theme);
    }

    public void ApplySettings(AppSettings settings)
    {
        CurrentSettings = settings;
        ApplyVisualSettings(settings);
        Services.Startup.SetEnabled(settings.StartWithWindows);
    }

    public void RequestMainClose()
    {
        if (CurrentSettings.MinimizeToTray) MainAppWindow.Hide();
        else ExitApplication();
    }

    private void OnGlobalShortcut(object? sender, string actionId)
    {
        Dispatcher.Invoke(() =>
        {
            if (actionId == "toggle") ToggleMainWindow();
            else if (actionId == "capture") MainAppWindow.OpenClipboardCapture();
        });
    }

    public void ToggleMainWindow()
    {
        if (MainAppWindow.IsVisible && MainAppWindow.IsActive) MainAppWindow.Hide();
        else MainAppWindow.ShowAndActivate();
    }

    private void ConfigureTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示片语", null, (_, _) => Dispatcher.Invoke(ToggleMainWindow));
        menu.Items.Add("读取剪贴板…", null, (_, _) => Dispatcher.Invoke(MainAppWindow.OpenClipboardCapture));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = Environment.ProcessPath is { } executable ? System.Drawing.Icon.ExtractAssociatedIcon(executable) ?? System.Drawing.SystemIcons.Application : System.Drawing.SystemIcons.Application,
            Text = "片语 - 本地文本片段",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleMainWindow);
    }

    public void ExitApplication()
    {
        _isExiting = true;
        _trayIcon?.Dispose();
        Services.Dispose();
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
