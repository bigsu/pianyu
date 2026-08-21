using System.Collections.ObjectModel;
using Pianyu.App.Infrastructure;
using Pianyu.Core;

namespace Pianyu.App.ViewModels;

public sealed class ManagedSnippetViewModel(Snippet snippet) : ObservableObject
{
    private bool _isSelected;
    public Snippet Snippet { get; } = snippet;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class ManagementViewModel : ObservableObject
{
    private string _filter = "全部";
    private TagInfo? _selectedTag;
    private string _search = string.Empty;
    private string _message = string.Empty;
    public ObservableCollection<ManagedSnippetViewModel> Items { get; } = [];
    public ObservableCollection<TagInfo> Tags { get; } = [];
    public IReadOnlyList<string> Filters { get; } = ["全部", "收藏", "最近使用", "最近创建"];
    public string Filter { get => _filter; set => SetProperty(ref _filter, value); }
    public TagInfo? SelectedTag { get => _selectedTag; set => SetProperty(ref _selectedTag, value); }
    public string Search { get => _search; set => SetProperty(ref _search, value); }
    public string Message { get => _message; set => SetProperty(ref _message, value); }
    public IReadOnlyList<long> SelectedIds => Items.Where(item => item.IsSelected).Select(item => item.Snippet.Id).ToList();
}
