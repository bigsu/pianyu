using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Pianyu.App.ViewModels;
using Pianyu.App.Services;
using Pianyu.Core;

namespace Pianyu.App.Views;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainViewModel _viewModel;
    private nint _pasteTarget;
    private bool _captureOpen;

    public MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _viewModel = new MainViewModel(services.Repository, services.Ranking, services.Foreground, services.ModelAssistant, services.ModelConfigurationStore);
        DataContext = _viewModel;
        CaptureTargetApplication();
        Loaded += async (_, _) =>
        {
            await _viewModel.InitializeAsync();
            RootPanel.Focus();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        services.Clipboard.CandidateAvailable += (_, text) => Dispatcher.Invoke(() => OpenClipboardCapture(text, true));
        services.Clipboard.ListeningTick += (_, remaining) => Dispatcher.Invoke(() => ShowStatus($"剪贴板监听中  {(int)remaining.TotalMinutes:00}:{remaining.Seconds:00} · 候选仍需确认"));
        services.Clipboard.ListeningStopped += (_, _) => Dispatcher.Invoke(() => ShowStatus("剪贴板临时监听已结束"));
    }

    public void ShowAndActivate()
    {
        CaptureTargetApplication();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        SearchBox.Focus();
        SearchBox.SelectAll();
        _ = _viewModel.SearchAsync();
    }

    private void CaptureTargetApplication()
    {
        _pasteTarget = _services.Foreground.GetForegroundWindowHandle();
        _viewModel.ContextApp = _services.Foreground.GetProcessName(_pasteTarget);
    }

    public void ShowStatus(string message, bool isError = false) => _viewModel.ShowStatus(message, isError);

    public void OpenClipboardCapture() => OpenClipboardCapture(_services.Clipboard.ReadText(), false);

    private void OpenClipboardCapture(string? text, bool fromListener)
    {
        if (_captureOpen) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowStatus("剪贴板中没有纯文本", true);
            return;
        }
        var window = new QuickCaptureWindow(_services, text, fromListener) { Owner = this };
        _captureOpen = true;
        try { if (window.ShowDialog() == true) _ = _viewModel.SearchAsync(); }
        finally { _captureOpen = false; }
    }

    private Task CopySelectedAsync(bool closeAfter, bool directPaste) =>
        _viewModel.SelectedSnippet is { } snippet ? CopySnippetAsync(snippet, closeAfter, directPaste) : Task.CompletedTask;

    private async Task CopySnippetAsync(Snippet snippet, bool closeAfter, bool directPaste)
    {
        var text = snippet.Content;
        var variables = TemplateEngine.Parse(text);
        if (variables.Count > 0)
        {
            var variableWindow = new TemplateVariableWindow(_services, text, variables) { Owner = this };
            if (variableWindow.ShowDialog() != true) return;
            text = variableWindow.RenderedText;
        }

        if (directPaste)
        {
            Hide();
            var result = await _services.DirectPaste.PasteAsync(text, _pasteTarget);
            _ = RecordUseBestEffortAsync(snippet, result.Success ? "paste" : "copy-fallback");
            if (!result.Success)
            {
                ShowAndActivate();
                ShowStatus(result.Message, true);
            }
            return;
        }

        if (!_services.Clipboard.SetText(text))
        {
            ShowStatus("剪贴板暂时被占用，请重试", true);
            return;
        }
        ShowStatus("已复制");
        if (closeAfter) Hide();
        _ = RecordUseBestEffortAsync(snippet, "copy");
    }

    private async Task RecordUseBestEffortAsync(Snippet snippet, string action)
    {
        try { await _viewModel.RecordUseAsync(snippet, action); }
        catch { /* 使用统计失败不能阻塞复制或直接粘贴。 */ }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var actualGesture = ShortcutService.FromKeyEvent(key, Keyboard.Modifiers);
        var configuredAction = ((App)System.Windows.Application.Current).CurrentShortcuts
            .FirstOrDefault(item => item.Scope == ShortcutScope.Local && !string.IsNullOrWhiteSpace(item.Gesture) && ShortcutService.GestureMatches(item.Gesture, actualGesture))?.ActionId;
        switch (configuredAction)
        {
            case "close": Hide(); e.Handled = true; return;
            case "new": OpenEditor(null); e.Handled = true; return;
            case "search": SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return;
            case "edit" when !SearchBox.IsKeyboardFocused: OpenEditor(_viewModel.SelectedSnippet); e.Handled = true; return;
            case "delete" when _viewModel.SelectedSnippet is not null && !SearchBox.IsKeyboardFocused: _viewModel.DeleteCommand.Execute(null); e.Handled = true; return;
            case "copy_close" when _viewModel.SelectedSnippet is not null: _ = CopySelectedAsync(true, false); e.Handled = true; return;
            case "paste" when _viewModel.SelectedSnippet is not null: _ = CopySelectedAsync(false, true); e.Handled = true; return;
            case "copy_keep" when _viewModel.SelectedSnippet is not null && !SearchBox.IsKeyboardFocused: _ = CopySelectedAsync(false, false); e.Handled = true; return;
        }
        if (e.Key == Key.Down && SearchBox.IsKeyboardFocused && ResultList.Items.Count > 0)
        {
            ResultList.SelectedIndex = Math.Min(ResultList.Items.Count - 1, Math.Max(0, ResultList.SelectedIndex + 1));
            ResultList.ScrollIntoView(ResultList.SelectedItem);
            e.Handled = true;
        }
        if (e.Key == Key.Up && SearchBox.IsKeyboardFocused && ResultList.Items.Count > 0)
        {
            ResultList.SelectedIndex = Math.Max(0, ResultList.SelectedIndex - 1);
            ResultList.ScrollIntoView(ResultList.SelectedItem);
            e.Handled = true;
        }
    }

    private void OpenEditor(Snippet? snippet)
    {
        var window = new SnippetEditorWindow(_services, snippet) { Owner = this };
        if (window.ShowDialog() == true) _ = _viewModel.SearchAsync();
    }

    private void NewSnippet_OnClick(object sender, RoutedEventArgs e) => OpenEditor(null);
    private void CopyItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Snippet snippet }) return;
        _viewModel.SelectedSnippet = snippet;
        _ = CopySnippetAsync(snippet, false, false);
        e.Handled = true;
    }
    private void EditItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Snippet snippet }) return;
        _viewModel.SelectedSnippet = snippet;
        OpenEditor(snippet);
        e.Handled = true;
    }
    private void DeleteItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Snippet snippet }) return;
        _viewModel.SelectedSnippet = snippet;
        _viewModel.DeleteCommand.Execute(null);
        e.Handled = true;
    }
    private void ActionButton_OnPreviewMouseDoubleClick(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void Clipboard_OnClick(object sender, RoutedEventArgs e) => OpenClipboardCapture();
    private void ResultList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => _ = CopySelectedAsync(_viewModel.Settings.CloseAfterCopy, false);
    private void Settings_OnClick(object sender, RoutedEventArgs e) { var window = new SettingsWindow(_services) { Owner = this }; window.ShowDialog(); _ = _viewModel.InitializeAsync(); }
    private void Manage_OnClick(object sender, RoutedEventArgs e) { var window = new ManagementWindow(_services) { Owner = this }; window.ShowDialog(); _ = _viewModel.SearchAsync(); }
}
