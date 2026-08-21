using System.Collections.ObjectModel;
using Pianyu.App.Infrastructure;
using Pianyu.Core;

namespace Pianyu.App.ViewModels;

public sealed class ShortcutRowViewModel : ObservableObject
{
    private string _gesture = string.Empty;
    private string _error = string.Empty;
    private bool _isRecording;
    public required string ActionId { get; init; }
    public required string DisplayName { get; init; }
    public required string DefaultGesture { get; init; }
    public required ShortcutScope Scope { get; init; }
    public string ScopeText => Scope == ShortcutScope.Global ? "全局" : "窗口内";
    public string Gesture { get => _gesture; set => SetProperty(ref _gesture, value); }
    public string Error { get => _error; set { SetProperty(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsRecording { get => _isRecording; set => SetProperty(ref _isRecording, value); }
}

public sealed class SettingsViewModel : ObservableObject
{
    private AppSettings _settings = new();
    private string _apiKey = string.Empty;
    private string _message = string.Empty;
    private string _connectionStatus = "未配置";
    private DatabaseStats _stats = new(0, 0, 0);
    private string _databasePath = string.Empty;

    public AppSettings Settings { get => _settings; set { SetProperty(ref _settings, value); OnPropertyChanged(string.Empty); } }
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
    public string Message { get => _message; set => SetProperty(ref _message, value); }
    public string ConnectionStatus { get => _connectionStatus; set => SetProperty(ref _connectionStatus, value); }
    public DatabaseStats Stats { get => _stats; set { SetProperty(ref _stats, value); OnPropertyChanged(nameof(DatabaseSizeText)); } }
    public string DatabasePath { get => _databasePath; set => SetProperty(ref _databasePath, value); }
    public string DatabaseSizeText => Stats.SizeBytes switch { < 1024 => $"{Stats.SizeBytes} B", < 1024 * 1024 => $"{Stats.SizeBytes / 1024d:F1} KB", _ => $"{Stats.SizeBytes / 1024d / 1024d:F1} MB" };
    public ObservableCollection<ShortcutRowViewModel> Shortcuts { get; } = [];
    public ObservableCollection<SearchAlias> Aliases { get; } = [];
}
