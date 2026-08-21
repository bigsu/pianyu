using Pianyu.App.Data;
using Pianyu.App.Services;
using Pianyu.Core;
using System.Net.Http;

namespace Pianyu.App;

public sealed class AppServices : IDisposable
{
    public AppServices()
    {
        Paths = new AppPaths();
        Database = new DatabaseService(Paths);
        Repository = new SnippetRepository(Database);
        Backup = new BackupService(Paths, Database, Repository);
        Clipboard = new ClipboardService();
        ClipboardCandidates = new ClipboardCandidateService(Repository);
        Foreground = new ForegroundAppService();
        DirectPaste = new DirectPasteService(Clipboard, Foreground);
        Shortcuts = new ShortcutService();
        Ranking = new RankingService();
        SecretProtector = new SecretProtector();
        ModelConfigurationStore = new ModelConfigurationStore(Repository, SecretProtector);
        Startup = new StartupService();
        Theme = new ThemeService();
        HttpClient = new HttpClient();
        ModelAssistant = new ModelAssistantService(HttpClient);
    }

    public AppPaths Paths { get; }
    public DatabaseService Database { get; }
    public SnippetRepository Repository { get; }
    public BackupService Backup { get; }
    public ClipboardService Clipboard { get; }
    public ClipboardCandidateService ClipboardCandidates { get; }
    public ForegroundAppService Foreground { get; }
    public DirectPasteService DirectPaste { get; }
    public ShortcutService Shortcuts { get; }
    public RankingService Ranking { get; }
    public SecretProtector SecretProtector { get; }
    public ModelConfigurationStore ModelConfigurationStore { get; }
    public StartupService Startup { get; }
    public ThemeService Theme { get; }
    public HttpClient HttpClient { get; }
    public ITextAssistant ModelAssistant { get; }

    public void Dispose()
    {
        Theme.Dispose();
        Clipboard.Dispose();
        Shortcuts.Dispose();
        HttpClient.Dispose();
    }
}
