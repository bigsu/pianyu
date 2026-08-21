using System.Windows;
using System.Windows.Controls;
using Pianyu.App.ViewModels;
using Pianyu.Core;

namespace Pianyu.App.Views;

public partial class ManagementWindow : Window
{
    private readonly AppServices _services;
    private readonly ManagementViewModel _viewModel = new();
    private List<Snippet> _all = [];

    public ManagementWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        DataContext = _viewModel;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _all = (await _services.Repository.GetAllAsync()).ToList();
        _viewModel.Tags.Clear();
        foreach (var tag in await _services.Repository.GetTagsAsync()) _viewModel.Tags.Add(tag);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<Snippet> items = _all;
        items = _viewModel.Filter switch
        {
            "收藏" => items.Where(item => item.IsFavorite),
            "最近使用" => items.OrderByDescending(item => item.LastUsedAt ?? DateTimeOffset.MinValue).Take(100),
            "最近创建" => items.OrderByDescending(item => item.CreatedAt).Take(100),
            _ => items.OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.LastUsedAt ?? item.CreatedAt)
        };
        if (_viewModel.SelectedTag is TagInfo tag) items = items.Where(item => item.Tags.Contains(tag.Name, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(_viewModel.Search)) items = items.Where(item => SearchText.IsFuzzyMatch(_viewModel.Search, $"{item.Title} {item.Content} {item.TagsText}"));
        _viewModel.Items.Clear();
        foreach (var item in items) _viewModel.Items.Add(new ManagedSnippetViewModel(item));
    }

    private void Filter_OnChanged(object sender, EventArgs e) => ApplyFilter();
    private void Tag_OnChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void ClearTag_OnClick(object sender, RoutedEventArgs e) { _viewModel.SelectedTag = null; ApplyFilter(); }

    private async void RenameTag_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTag is not TagInfo tag) { _viewModel.Message = "请先选择一个标签。"; return; }
        var prompt = new TextPromptWindow("重命名标签", "新标签名", tag.Name) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        await _services.Repository.RenameTagAsync(tag.Name, prompt.Value);
        _viewModel.SelectedTag = null;
        await ReloadAsync();
    }

    private async void DeleteTag_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTag is not TagInfo tag) { _viewModel.Message = "请先选择一个标签。"; return; }
        await _services.Repository.DeleteTagAsync(tag.Name);
        _viewModel.Message = $"已移除标签 #{tag.Name}，片段正文未删除。";
        _viewModel.SelectedTag = null;
        await ReloadAsync();
    }

    private async void BatchTag_OnClick(object sender, RoutedEventArgs e)
    {
        var ids = _viewModel.SelectedIds;
        if (ids.Count == 0) { _viewModel.Message = "请先勾选片段。"; return; }
        var prompt = new TextPromptWindow("批量添加标签", "标签（空格或逗号分隔）", string.Empty) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var tags = prompt.Value.Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await _services.Repository.AddTagsAsync(ids, tags);
        _viewModel.Message = $"已为 {ids.Count} 条片段添加标签。";
        await ReloadAsync();
    }

    private async void BatchDelete_OnClick(object sender, RoutedEventArgs e)
    {
        var ids = _viewModel.SelectedIds;
        if (ids.Count == 0) { _viewModel.Message = "请先勾选片段。"; return; }
        foreach (var id in ids) await _services.Repository.DeleteAsync(id);
        _viewModel.Message = $"已永久删除 {ids.Count} 条片段。";
        await ReloadAsync();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
