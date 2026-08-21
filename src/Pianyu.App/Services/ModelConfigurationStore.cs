using Pianyu.App.Data;
using Pianyu.Core;

namespace Pianyu.App.Services;

public sealed class ModelConfigurationStore(SnippetRepository repository, SecretProtector protector)
{
    public async Task<(AppSettings Settings, string ApiKey)> LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await repository.GetAppSettingsAsync(cancellationToken);
        var protectedKey = await repository.GetSettingAsync("model_api_key", cancellationToken) ?? string.Empty;
        return (settings, protector.Unprotect(protectedKey));
    }

    public async Task SaveAsync(AppSettings settings, string apiKey, CancellationToken cancellationToken = default)
    {
        await repository.SaveAppSettingsAsync(settings, cancellationToken);
        await repository.SetSettingAsync("model_api_key", protector.Protect(apiKey), cancellationToken);
    }

    public static ModelConfiguration ToConfiguration(AppSettings settings, string apiKey) => new(
        settings.ModelEnabled,
        settings.ModelEndpoint,
        apiKey,
        settings.ModelName,
        settings.FallbackModelName,
        TimeSpan.FromSeconds(Math.Clamp(settings.ModelTimeoutSeconds, 3, 120)),
        settings.ModelFeatures);
}
