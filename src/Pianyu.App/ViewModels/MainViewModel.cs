using System.Collections.ObjectModel;
using Pianyu.App.Data;
using Pianyu.App.Infrastructure;
using Pianyu.App.Services;
using Pianyu.Core;

namespace Pianyu.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SnippetRepository _repository;
    private readonly RankingService _ranking;
    private readonly ForegroundAppService _foreground;
    private readonly ITextAssistant _modelAssistant;
    private readonly ModelConfigurationStore _modelStore;
    private CancellationTokenSource? _searchCancellation;
    private string _query = string.Empty;
    private Snippet? _selectedSnippet;
    private bool _isBusy;
    private string _statusMessage = string.Empty;
    private bool _isStatusError;
    private long? _lastDeletedId;
    private AppSettings _settings = new();

    public MainViewModel(SnippetRepository repository, RankingService ranking, ForegroundAppService foreground, ITextAssistant modelAssistant, ModelConfigurationStore modelStore)
    {
        _repository = repository;
        _ranking = ranking;
        _foreground = foreground;
        _modelAssistant = modelAssistant;
        _modelStore = modelStore;
        DeleteCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => SelectedSnippet is not null);
        UndoDeleteCommand = new AsyncRelayCommand(UndoDeleteAsync, () => _lastDeletedId.HasValue);
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync, () => SelectedSnippet is not null);
        TogglePinCommand = new AsyncRelayCommand(TogglePinAsync, () => SelectedSnippet is not null);
    }

    public ObservableCollection<Snippet> Results { get; } = [];
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand UndoDeleteCommand { get; }
    public AsyncRelayCommand ToggleFavoriteCommand { get; }
    public AsyncRelayCommand TogglePinCommand { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (!SetProperty(ref _query, value)) return;
            _ = DebouncedSearchAsync();
        }
    }

    public Snippet? SelectedSnippet
    {
        get => _selectedSnippet;
        set
        {
            if (!SetProperty(ref _selectedSnippet, value)) return;
            DeleteCommand.RaiseCanExecuteChanged();
            ToggleFavoriteCommand.RaiseCanExecuteChanged();
            TogglePinCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool HasResults => Results.Count > 0;
    public bool HasSelection => SelectedSnippet is not null;
    public string EmptyTitle => string.IsNullOrWhiteSpace(Query) ? "保存第一条片语" : "没有找到匹配片段";
    public string EmptyMessage => string.IsNullOrWhiteSpace(Query) ? "按 Ctrl+N 新建，或按 Ctrl+Alt+S 从剪贴板录入。" : "尝试更短的词、拼音首字母或标签。";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsStatusError { get => _isStatusError; private set => SetProperty(ref _isStatusError, value); }
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public AppSettings Settings => _settings;
    public string? ContextApp { get; set; }

    public async Task InitializeAsync()
    {
        try
        {
            _settings = await _repository.GetAppSettingsAsync();
            OnPropertyChanged(nameof(Settings));
        }
        catch (Exception ex)
        {
            _settings = new AppSettings();
            ShowStatus($"数据库打开失败：{ex.Message}", true);
        }
        await SearchAsync();
    }

    public async Task SearchAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        IsBusy = true;
        try
        {
            var candidates = await _repository.SearchAsync(Query, 300, token);
            var context = new RankingContext(Query, _settings.AppAwareness ? ContextApp : null, DateTimeOffset.Now, _settings.SmartRanking, _settings.AppAwareness, _settings.DefaultSort);
            var ranked = await Task.Run(() => _ranking.Rank(candidates, context), token);
            Results.Clear();
            foreach (var item in ranked) Results.Add(item);
            SelectedSnippet = Results.FirstOrDefault();
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyMessage));
            if (!string.IsNullOrWhiteSpace(Query)) _ = SupplementSemanticResultsAsync(Query, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowStatus($"数据库打开失败：{ex.Message}", true);
        }
        finally { IsBusy = false; }
    }

    private async Task SupplementSemanticResultsAsync(string originalQuery, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _modelStore.LoadAsync(cancellationToken);
            if (!stored.Settings.ModelEnabled || string.IsNullOrWhiteSpace(stored.ApiKey) ||
                !stored.Settings.ModelFeatures.TryGetValue("semantic", out var enabled) || !enabled) return;
            var suggestions = await _modelAssistant.SuggestAsync("semantic", originalQuery, ModelConfigurationStore.ToConfiguration(stored.Settings, stored.ApiKey), cancellationToken);
            if (cancellationToken.IsCancellationRequested || !string.Equals(Query, originalQuery, StringComparison.Ordinal)) return;

            var merged = Results.ToDictionary(item => item.Id);
            foreach (var suggestion in suggestions.Take(5))
            {
                foreach (var item in await _repository.SearchAsync(suggestion.Value, 60, cancellationToken))
                {
                    if (merged.ContainsKey(item.Id)) continue;
                    item.SearchRank = Math.Max(item.SearchRank, 4);
                    merged[item.Id] = item;
                }
            }
            var ranked = _ranking.Rank(merged.Values, new RankingContext(originalQuery, ContextApp, DateTimeOffset.Now, _settings.SmartRanking, _settings.AppAwareness, _settings.DefaultSort));
            Results.Clear();
            foreach (var item in ranked) Results.Add(item);
            SelectedSnippet ??= Results.FirstOrDefault();
            OnPropertyChanged(nameof(HasResults));
        }
        catch (OperationCanceledException) { }
        catch { /* 模型补充失败不会中断本地结果。 */ }
    }

    public async Task RecordUseAsync(Snippet snippet, string action)
    {
        await _repository.RecordUseAsync(snippet.Id, ContextApp, action);
        await _repository.LearnAliasAsync(Query, snippet);
        snippet.CopyCount++;
        snippet.LastUsedAt = DateTimeOffset.Now;
    }

    public void ShowStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
        OnPropertyChanged(nameof(HasStatus));
    }

    public void ClearStatus()
    {
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(HasStatus));
    }

    private async Task DebouncedSearchAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        try
        {
            await Task.Delay(120, token);
            await SearchAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedSnippet is null) return;
        _lastDeletedId = SelectedSnippet.Id;
        await _repository.DeleteAsync(SelectedSnippet.Id);
        UndoDeleteCommand.RaiseCanExecuteChanged();
        ShowStatus($"已删除“{SelectedSnippet.Title}” · Ctrl+Z 撤销");
        await SearchAsync();
    }

    private async Task UndoDeleteAsync()
    {
        if (!_lastDeletedId.HasValue) return;
        await _repository.UndoDeleteAsync(_lastDeletedId.Value);
        _lastDeletedId = null;
        UndoDeleteCommand.RaiseCanExecuteChanged();
        ShowStatus("已撤销删除");
        await SearchAsync();
    }

    private async Task ToggleFavoriteAsync()
    {
        if (SelectedSnippet is null) return;
        SelectedSnippet.IsFavorite = !SelectedSnippet.IsFavorite;
        await _repository.SaveAsync(SelectedSnippet);
        ShowStatus(SelectedSnippet.IsFavorite ? "已收藏" : "已取消收藏");
        await SearchAsync();
    }

    private async Task TogglePinAsync()
    {
        if (SelectedSnippet is null) return;
        SelectedSnippet.IsPinned = !SelectedSnippet.IsPinned;
        await _repository.SaveAsync(SelectedSnippet);
        ShowStatus(SelectedSnippet.IsPinned ? "已置顶" : "已取消置顶");
        await SearchAsync();
    }
}
