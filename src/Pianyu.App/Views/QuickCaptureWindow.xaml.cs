using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Pianyu.App.Data;
using Pianyu.App.ViewModels;

namespace Pianyu.App.Views;

public partial class QuickCaptureWindow : Window
{
    private readonly AppServices _services;
    private readonly SnippetEditorViewModel _viewModel;
    private int _listenMinutes = 10;

    public QuickCaptureWindow(AppServices services, string text, bool fromListener)
    {
        InitializeComponent();
        _services = services;
        var candidate = services.ClipboardCandidates.Create(text, fromListener);
        _viewModel = SnippetEditorViewModel.FromSnippet(candidate.Snippet);
        DataContext = _viewModel;
        SourceLabel.Text = fromListener ? "监听候选" : "手动读取";
        services.Clipboard.ListeningTick += Clipboard_OnListeningTick;
        Loaded += async (_, _) =>
        {
            var settings = await services.Repository.GetAppSettingsAsync();
            _listenMinutes = Math.Clamp(settings.ClipboardListenMinutes, 1, 120);
            ListenLabel.Text = $"监听 {_listenMinutes} 分钟（仅生成候选，仍需确认）";
            TitleBox.Focus();
        };
        Closed += (_, _) => services.Clipboard.ListeningTick -= Clipboard_OnListeningTick;
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        MessageText.Text = string.Empty;
        try
        {
            var result = await _services.ClipboardCandidates.ConfirmAsync(_viewModel.ToSnippet());
            if (result.IsDuplicate)
            {
                MessageText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
                MessageText.Text = $"相同内容已存在：“{result.Snippet?.Title}”";
                return;
            }
            DialogResult = true;
        }
        catch (Exception ex) { MessageText.Text = ex.Message; }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { Save_OnClick(sender, e); e.Handled = true; }
    }

    private void Listen_OnChecked(object sender, RoutedEventArgs e)
    {
        try { _services.Clipboard.StartListening(TimeSpan.FromMinutes(_listenMinutes)); }
        catch (Exception ex) { MessageText.Text = ex.Message; ListenCheckBox.IsChecked = false; }
    }

    private void Listen_OnUnchecked(object sender, RoutedEventArgs e)
    {
        if (_services.Clipboard.IsListening) _services.Clipboard.StopListening();
        CountdownText.Text = string.Empty;
    }

    private void Clipboard_OnListeningTick(object? sender, TimeSpan remaining) => Dispatcher.Invoke(() => CountdownText.Text = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}");
}
