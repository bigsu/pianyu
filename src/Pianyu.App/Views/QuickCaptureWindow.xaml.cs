using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Pianyu.App.Data;
using Pianyu.App.Services;
using Pianyu.App.ViewModels;

namespace Pianyu.App.Views;

public partial class QuickCaptureWindow : Window
{
    private readonly AppServices _services;
    private readonly SnippetEditorViewModel _viewModel;
    private readonly CancellationTokenSource _modelCancellation = new();
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
            var stored = await services.ModelConfigurationStore.LoadAsync(_modelCancellation.Token);
            _listenMinutes = Math.Clamp(stored.Settings.ClipboardListenMinutes, 1, 120);
            ListenLabel.Text = $"监听 {_listenMinutes} 分钟（仅生成候选，仍需确认）";
            TitleBox.Focus();
            _ = EnrichWithModelAsync(stored.Settings, stored.ApiKey, _modelCancellation.Token);
        };
        Closed += (_, _) =>
        {
            services.Clipboard.ListeningTick -= Clipboard_OnListeningTick;
            _modelCancellation.Cancel();
            _modelCancellation.Dispose();
        };
    }

    private async Task EnrichWithModelAsync(Pianyu.Core.AppSettings settings, string apiKey, CancellationToken cancellationToken)
    {
        if (!settings.ModelEnabled || string.IsNullOrWhiteSpace(apiKey)) return;
        var titleEnabled = settings.ModelFeatures.TryGetValue("title", out var titleFeature) && titleFeature;
        var tagsEnabled = settings.ModelFeatures.TryGetValue("tags", out var tagsFeature) && tagsFeature;
        if (!titleEnabled && !tagsEnabled) return;

        var originalTitle = _viewModel.Title;
        var originalTags = _viewModel.TagsText;
        var configuration = ModelConfigurationStore.ToConfiguration(settings, apiKey);
        MessageText.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        MessageText.Text = "正在使用模型生成标题和标签…";
        var generated = false;
        try
        {
            if (titleEnabled)
            {
                var titleSuggestions = await _services.ModelAssistant.SuggestAsync("title", _viewModel.Content, configuration, cancellationToken);
                var title = titleSuggestions.FirstOrDefault()?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(title) && string.Equals(_viewModel.Title, originalTitle, StringComparison.Ordinal))
                {
                    _viewModel.Title = title;
                    generated = true;
                }
            }

            if (tagsEnabled)
            {
                var tagSuggestions = await _services.ModelAssistant.SuggestAsync("tags", _viewModel.Content, configuration, cancellationToken);
                var tags = tagSuggestions.Select(item => item.Value.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
                if (tags.Length > 0 && string.Equals(_viewModel.TagsText, originalTags, StringComparison.Ordinal))
                {
                    _viewModel.TagsText = string.Join(' ', tags);
                    generated = true;
                }
            }

            MessageText.Text = generated ? "模型已生成标题和标签，请确认后保存。" : "模型暂时不可用，已保留本地标题。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MessageText.Text = string.Empty;
        }
        catch
        {
            MessageText.Text = "模型暂时不可用，已保留本地标题。";
        }
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        MessageText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
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
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }

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
