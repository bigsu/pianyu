using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Microsoft.Win32;
using Pianyu.App.Services;
using Pianyu.App.ViewModels;
using Pianyu.Core;

namespace Pianyu.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppServices _services;
    private readonly SettingsViewModel _viewModel = new();
    private ShortcutRowViewModel? _recordingRow;
    private ThemeMode _originalTheme = ThemeMode.Dark;

    public SettingsWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            try { await LoadAsync(); }
            catch (Exception ex) { _viewModel.Message = $"数据库打开失败：{ex.Message}。可在“数据与备份”中恢复备份。"; }
        };
        Closing += (_, _) =>
        {
            if (DialogResult != true) _services.Theme.Apply(_originalTheme);
        };
    }

    private async Task LoadAsync()
    {
        var stored = await _services.ModelConfigurationStore.LoadAsync();
        _viewModel.Settings = stored.Settings;
        _originalTheme = stored.Settings.Theme;
        _viewModel.ApiKey = stored.ApiKey;
        ApiKeyBox.Password = stored.ApiKey;
        _viewModel.ConnectionStatus = string.IsNullOrWhiteSpace(stored.ApiKey) ? "未配置" : "已配置，未测试";
        var gestures = await _services.Repository.GetShortcutsAsync();
        _viewModel.Shortcuts.Clear();
        foreach (var item in ShortcutService.Defaults)
        {
            _viewModel.Shortcuts.Add(new ShortcutRowViewModel { ActionId = item.ActionId, DisplayName = item.DisplayName, DefaultGesture = item.DefaultGesture, Scope = item.Scope, Gesture = gestures.TryGetValue(item.ActionId, out var gesture) ? gesture : item.DefaultGesture });
        }
        await ReloadAliasesAsync();
        _viewModel.Stats = await _services.Backup.GetStatsAsync();
        _viewModel.DatabasePath = _services.Paths.DatabasePath;
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _services.ModelConfigurationStore.SaveAsync(_viewModel.Settings, _viewModel.ApiKey);
            ((App)System.Windows.Application.Current).ApplySettings(_viewModel.Settings);
            _viewModel.Message = "设置已保存";
            DialogResult = true;
        }
        catch (Exception ex) { _viewModel.Message = ex.Message; }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        _services.Theme.Apply(_originalTheme);
        DialogResult = false;
    }
    private void ThemePreview_OnChecked(object sender, RoutedEventArgs e) => _services.Theme.Apply(_viewModel.Settings.Theme);
    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.ApiKey = ApiKeyBox.Password;

    private void ShortcutRecord_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShortcutRowViewModel row }) return;
        if (_recordingRow is not null) _recordingRow.IsRecording = false;
        _recordingRow = row;
        _services.Shortcuts.SuspendGlobals();
        row.IsRecording = true;
        row.Error = "请按新的组合键，Esc 取消";
        Focus();
    }

    private async void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingRow is null) return;
        if (e.Key == Key.Escape)
        {
            _recordingRow.IsRecording = false;
            _recordingRow.Error = string.Empty;
            _recordingRow = null;
            await ((App)System.Windows.Application.Current).ReloadGlobalShortcutsAsync();
            e.Handled = true;
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var gesture = ShortcutService.FromKeyEvent(key, Keyboard.Modifiers);
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }
        var conflict = ShortcutService.FindInternalConflict(_recordingRow.ActionId, gesture, _viewModel.Shortcuts.Select(ToDefinition));
        if (conflict is not null)
        {
            _recordingRow.Error = conflict;
            e.Handled = true;
            return;
        }
        var row = _recordingRow;
        var previousGesture = row.Gesture;
        row.Error = string.Empty;
        await _services.Repository.SaveShortcutAsync(row.ActionId, gesture);
        var errors = await ((App)System.Windows.Application.Current).ReloadGlobalShortcutsAsync();
        if (errors.TryGetValue(row.ActionId, out var error))
        {
            await _services.Repository.SaveShortcutAsync(row.ActionId, previousGesture);
            await ((App)System.Windows.Application.Current).ReloadGlobalShortcutsAsync();
            row.Error = $"{error}；已保留原快捷键";
        }
        else
        {
            row.Gesture = gesture;
        }
        row.IsRecording = false;
        _recordingRow = null;
        e.Handled = true;
    }

    private async void ShortcutClear_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShortcutRowViewModel row }) return;
        row.Gesture = string.Empty;
        row.Error = string.Empty;
        await _services.Repository.SaveShortcutAsync(row.ActionId, string.Empty);
        await ((App)System.Windows.Application.Current).ReloadGlobalShortcutsAsync();
    }

    private async void RestoreShortcuts_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _viewModel.Shortcuts)
        {
            row.Gesture = row.DefaultGesture;
            row.Error = string.Empty;
            await _services.Repository.SaveShortcutAsync(row.ActionId, row.DefaultGesture);
        }
        var errors = await ((App)System.Windows.Application.Current).ReloadGlobalShortcutsAsync();
        foreach (var row in _viewModel.Shortcuts) if (errors.TryGetValue(row.ActionId, out var error)) row.Error = error;
        _viewModel.Message = "已恢复默认快捷键";
    }

    private static ShortcutDefinition ToDefinition(ShortcutRowViewModel row) => new(row.ActionId, row.DisplayName, row.Gesture, row.DefaultGesture, row.Scope);

    private async void DeleteAlias_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SearchAlias alias }) return;
        await _services.Repository.DeleteAliasAsync(alias.Id);
        await ReloadAliasesAsync();
    }

    private async Task ReloadAliasesAsync()
    {
        _viewModel.Aliases.Clear();
        foreach (var alias in await _services.Repository.GetAliasesAsync()) _viewModel.Aliases.Add(alias);
    }

    private async void TestModel_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ConnectionStatus = "正在连接…";
        await _services.ModelConfigurationStore.SaveAsync(_viewModel.Settings, _viewModel.ApiKey);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_viewModel.Settings.ModelTimeoutSeconds, 3, 120) + 2));
        var configuration = ModelConfigurationStore.ToConfiguration(_viewModel.Settings, _viewModel.ApiKey) with { Enabled = true };
        var result = await _services.ModelAssistant.TestConnectionAsync(configuration, cancellation.Token);
        _viewModel.ConnectionStatus = result.Success ? "已连接" : "连接失败";
        _viewModel.Message = result.Message;
    }

    private void OpenDataDirectory_OnClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_services.Paths.DatabasePath}\"") { UseShellExecute = true });
    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "片语 JSON|*.json", FileName = $"pianyu-{DateTime.Now:yyyyMMdd}.json" };
        if (dialog.ShowDialog(this) != true) return;
        await _services.Backup.ExportJsonAsync(dialog.FileName);
        _viewModel.Message = "JSON 导出完成";
    }
    private async void Import_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "片语 JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var summary = await _services.Backup.ImportJsonAsync(dialog.FileName);
        _viewModel.Message = $"导入完成：新增 {summary.Created}，跳过重复 {summary.Skipped}，失败 {summary.Failed}";
        _viewModel.Stats = await _services.Backup.GetStatsAsync();
    }
    private async void Backup_OnClick(object sender, RoutedEventArgs e)
    {
        try { _viewModel.Message = $"备份完成：{await _services.Backup.BackupAsync()}"; }
        catch (Exception ex) { _viewModel.Message = ex.Message; }
    }
    private async void Restore_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "片语数据库备份|*.db" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _services.Backup.RestoreAsync(dialog.FileName); _viewModel.Message = "备份恢复完成"; _viewModel.Stats = await _services.Backup.GetStatsAsync(); }
        catch (Exception ex) { _viewModel.Message = ex.Message; }
    }
}
