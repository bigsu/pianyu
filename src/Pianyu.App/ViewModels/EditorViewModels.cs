using System.Collections.ObjectModel;
using Pianyu.App.Infrastructure;
using Pianyu.Core;

namespace Pianyu.App.ViewModels;

public sealed class SnippetEditorViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _content = string.Empty;
    private string _tagsText = string.Empty;
    private bool _isFavorite;
    private bool _isPinned;
    private bool _isBusy;
    private string _message = string.Empty;
    private ModelSuggestion? _selectedSuggestion;

    public long Id { get; init; }
    public bool IsEditing => Id > 0;
    public string WindowTitle => IsEditing ? "编辑片段" : "新建片段";
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Content { get => _content; set => SetProperty(ref _content, value); }
    public string TagsText { get => _tagsText; set => SetProperty(ref _tagsText, value); }
    public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }
    public bool IsPinned { get => _isPinned; set => SetProperty(ref _isPinned, value); }
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public string Message { get => _message; set { SetProperty(ref _message, value); OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public ObservableCollection<ModelSuggestion> Suggestions { get; } = [];
    public ModelSuggestion? SelectedSuggestion { get => _selectedSuggestion; set => SetProperty(ref _selectedSuggestion, value); }

    public Snippet ToSnippet() => new()
    {
        Id = Id,
        Title = string.IsNullOrWhiteSpace(Title) ? Pianyu.App.Data.SnippetRepository.FirstLine(Content) : Title.Trim(),
        Content = Content.Trim(),
        Tags = TagsText.Split([',', '，', ' ', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(tag => tag.TrimStart('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        IsFavorite = IsFavorite,
        IsPinned = IsPinned
    };

    public static SnippetEditorViewModel FromSnippet(Snippet? snippet) => snippet is null ? new SnippetEditorViewModel() : new SnippetEditorViewModel
    {
        Id = snippet.Id,
        Title = snippet.Title,
        Content = snippet.Content,
        TagsText = string.Join(' ', snippet.Tags.Select(tag => $"#{tag}")),
        IsFavorite = snippet.IsFavorite,
        IsPinned = snippet.IsPinned
    };
}

public sealed class TemplateVariableItemViewModel : ObservableObject
{
    private string _value = string.Empty;
    public required string Name { get; init; }
    public required string DefaultValue { get; init; }
    public ObservableCollection<string> RecentValues { get; } = [];
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}
