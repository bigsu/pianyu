using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Pianyu.App.ViewModels;
using Pianyu.App.Services;
using Pianyu.Core;

namespace Pianyu.App.Views;

public partial class SnippetEditorWindow : Window
{
    private readonly AppServices _services;
    private readonly SnippetEditorViewModel _viewModel;

    public SnippetEditorWindow(AppServices services, Snippet? snippet)
    {
        InitializeComponent();
        _services = services;
        _viewModel = SnippetEditorViewModel.FromSnippet(snippet);
        DataContext = _viewModel;
        Loaded += (_, _) => (string.IsNullOrWhiteSpace(_viewModel.Title) ? ContentBox : TitleBox).Focus();
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Message = string.Empty;
        try
        {
            var result = await _services.Repository.SaveAsync(_viewModel.ToSnippet());
            if (result.IsDuplicate)
            {
                _viewModel.Message = $"相同内容已存在：“{result.Snippet?.Title}”";
                return;
            }
            DialogResult = true;
        }
        catch (Exception ex) { _viewModel.Message = ex.Message; }
    }

    private async void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsEditing) return;
        await _services.Repository.DeleteAsync(_viewModel.Id);
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void InsertVariable_OnClick(object sender, RoutedEventArgs e)
    {
        var insertion = "{name=default}";
        ContentBox.SelectedText = insertion;
        ContentBox.CaretIndex -= insertion.Length - 1;
        ContentBox.Focus();
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save_OnClick(sender, e); e.Handled = true; }
    }

    private void SuggestTitle_OnClick(object sender, RoutedEventArgs e) => _ = SuggestAsync("title");
    private void SuggestTags_OnClick(object sender, RoutedEventArgs e) => _ = SuggestAsync("tags");
    private void SuggestSummary_OnClick(object sender, RoutedEventArgs e) => _ = SuggestAsync("summary");
    private void SuggestRewrite_OnClick(object sender, RoutedEventArgs e) => _ = SuggestAsync("rewrite");
    private void SuggestMerge_OnClick(object sender, RoutedEventArgs e) => _ = SuggestAsync("merge");
    private void SuggestVariables_OnClick(object sender, RoutedEventArgs e) => _ = SuggestAsync("variables");

    private async Task SuggestAsync(string feature)
    {
        _viewModel.Message = string.Empty;
        _viewModel.IsBusy = true;
        try
        {
            var stored = await _services.ModelConfigurationStore.LoadAsync();
            if (!stored.Settings.ModelEnabled || string.IsNullOrWhiteSpace(stored.ApiKey))
            {
                _viewModel.Message = "模型服务未配置；本地编辑与保存仍可正常使用。";
                return;
            }
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(stored.Settings.ModelTimeoutSeconds + 2));
            var suggestions = await _services.ModelAssistant.SuggestAsync(feature, _viewModel.Content, ModelConfigurationStore.ToConfiguration(stored.Settings, stored.ApiKey), cancellation.Token);
            _viewModel.Suggestions.Clear();
            foreach (var suggestion in suggestions) _viewModel.Suggestions.Add(suggestion);
            _viewModel.SelectedSuggestion = _viewModel.Suggestions.FirstOrDefault();
            if (suggestions.Count == 0) _viewModel.Message = _services.ModelAssistant.IsTemporarilyPaused ? "模型连续失败，已暂停自动请求 1 分钟。" : "模型暂时不可用；本地功能不受影响。";
        }
        catch (Exception ex) { _viewModel.Message = $"模型建议失败：{ex.Message}"; }
        finally { _viewModel.IsBusy = false; }
    }

    private void ApplySuggestion_OnClick(object sender, RoutedEventArgs e)
    {
        var suggestion = _viewModel.SelectedSuggestion;
        if (suggestion is null) return;
        switch (suggestion.Kind)
        {
            case "title": _viewModel.Title = suggestion.Value; break;
            case "tags": _viewModel.TagsText = string.Join(' ', (_viewModel.TagsText + " " + suggestion.Value).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)); break;
            case "rewrite": _viewModel.Content = suggestion.Value; break;
            case "summary":
            case "merge":
                _services.Clipboard.SetText(suggestion.Value);
                _viewModel.Message = "建议已复制，原文没有被修改。";
                return;
            case "variables":
                var pair = suggestion.Value.Split('=', 2);
                if (pair.Length > 0) ContentBox.SelectedText = $"{{{pair[0]}={(pair.Length > 1 ? pair[1] : string.Empty)}}}";
                break;
        }
        _viewModel.Message = "建议已应用到编辑区，保存前仍可修改。";
    }
}
