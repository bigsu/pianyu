namespace Pianyu.App.Data;

public sealed class AppPaths
{
    public AppPaths(string? baseDirectory = null)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        DatabasePath = Path.Combine(BaseDirectory, "pianyu.db");
        BackupDirectory = Path.Combine(BaseDirectory, "backups");
    }

    public string BaseDirectory { get; }
    public string DatabasePath { get; }
    public string BackupDirectory { get; }
}
