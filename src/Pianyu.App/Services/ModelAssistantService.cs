using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pianyu.Core;

namespace Pianyu.App.Services;

public sealed record ModelConfiguration(
    bool Enabled,
    string Endpoint,
    string ApiKey,
    string Model,
    string FallbackModel,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, bool> Features);

public interface ITextAssistant
{
    bool IsTemporarilyPaused { get; }
    Task<(bool Success, string Message)> TestConnectionAsync(ModelConfiguration configuration, CancellationToken cancellationToken);
    Task<IReadOnlyList<ModelSuggestion>> SuggestAsync(string feature, string content, ModelConfiguration configuration, CancellationToken cancellationToken);
}

public sealed class ModelAssistantService(HttpClient httpClient) : ITextAssistant
{
    private int _consecutiveFailures;
    private DateTimeOffset? _pausedUntil;
    public bool IsTemporarilyPaused => _pausedUntil > DateTimeOffset.Now;

    public async Task<(bool Success, string Message)> TestConnectionAsync(ModelConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!IsConfigured(configuration)) return (false, "请先填写服务地址、API Key 和模型名称");
        try
        {
            var response = await CompleteAsync("只回复“连接成功”。", configuration.Model, configuration, cancellationToken);
            RegisterSuccess();
            return (true, string.IsNullOrWhiteSpace(response) ? "服务已连接" : "服务已连接");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RegisterFailure();
            return (false, "连接超时，本地功能不受影响");
        }
        catch (Exception ex)
        {
            RegisterFailure();
            return (false, $"连接失败：{FriendlyError(ex)}；本地功能不受影响");
        }
    }

    public async Task<IReadOnlyList<ModelSuggestion>> SuggestAsync(string feature, string content, ModelConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!IsConfigured(configuration) || IsTemporarilyPaused || !configuration.Features.TryGetValue(feature, out var enabled) || !enabled) return [];
        var prompt = BuildPrompt(feature, content);
        try
        {
            var response = await CompleteWithFallbackAsync(prompt, configuration, cancellationToken);
            RegisterSuccess();
            return ParseSuggestions(feature, response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RegisterFailure();
            return [];
        }
        catch
        {
            RegisterFailure();
            return [];
        }
    }

    private async Task<string> CompleteWithFallbackAsync(string prompt, ModelConfiguration configuration, CancellationToken cancellationToken)
    {
        try { return await CompleteAsync(prompt, configuration.Model, configuration, cancellationToken); }
        catch when (!string.IsNullOrWhiteSpace(configuration.FallbackModel) && !string.Equals(configuration.Model, configuration.FallbackModel, StringComparison.OrdinalIgnoreCase))
        {
            return await CompleteAsync(prompt, configuration.FallbackModel, configuration, cancellationToken);
        }
    }

    private async Task<string> CompleteAsync(string prompt, string model, ModelConfiguration configuration, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(configuration.Timeout);
        var endpoint = configuration.Endpoint.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = "你是片语桌面工具的文本辅助器。只返回用户可确认的简洁建议，绝不声称已修改原文。" },
                new { role = "user", content = prompt }
            },
            temperature = 0.2,
            stream = false,
            thinking = new { type = "disabled" }
        });
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"服务返回 {(int)response.StatusCode}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        var root = document.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
        {
            return content.GetString() ?? string.Empty;
        }
        if (root.TryGetProperty("output_text", out var outputText)) return outputText.GetString() ?? string.Empty;
        throw new InvalidDataException("模型返回内容格式无法识别");
    }

    private void RegisterSuccess()
    {
        _consecutiveFailures = 0;
        _pausedUntil = null;
    }

    private void RegisterFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= 3)
        {
            _pausedUntil = DateTimeOffset.Now.AddMinutes(1);
            _consecutiveFailures = 0;
        }
    }

    private static bool IsConfigured(ModelConfiguration configuration) => configuration.Enabled && !string.IsNullOrWhiteSpace(configuration.Endpoint) && !string.IsNullOrWhiteSpace(configuration.ApiKey) && !string.IsNullOrWhiteSpace(configuration.Model);

    private static string BuildPrompt(string feature, string content) => feature switch
    {
        "title" => $"为以下文本生成一个不超过 20 个汉字的标题，只输出标题：\n\n{content}",
        "tags" => $"为以下文本推荐 1 到 5 个短标签，用逗号分隔，只输出标签：\n\n{content}",
        "summary" => $"为以下文本写一条不超过 80 个汉字的摘要，只输出摘要：\n\n{content}",
        "rewrite" => $"整理以下文本的表达和格式，保留原意与所有关键参数，只输出建议稿：\n\n{content}",
        "merge" => $"判断以下文本是否包含可合并的重复内容，输出简短合并建议；没有则输出“无合并建议”：\n\n{content}",
        "variables" => $"识别以下文本中适合参数化的值，使用 name=default 逐行输出；没有则输出“无变量建议”：\n\n{content}",
        "semantic" => $"提取以下搜索意图的同义表达和相关概念，用逗号分隔：\n\n{content}",
        _ => content
    };

    private static IReadOnlyList<ModelSuggestion> ParseSuggestions(string feature, string response)
    {
        response = response.Trim().Trim('`');
        if (string.IsNullOrWhiteSpace(response)) return [];
        if (feature is "tags" or "semantic")
        {
            return response.Split([',', '，', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(8).Select(value => new ModelSuggestion(feature, value.TrimStart('#', '-', ' '))).ToList();
        }
        if (feature == "variables")
        {
            return response.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.Contains("无变量建议", StringComparison.Ordinal)).Take(8)
                .Select(line => new ModelSuggestion(feature, line.TrimStart('-', '*', ' '))).ToList();
        }
        return [new ModelSuggestion(feature, response)];
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        HttpRequestException http => http.Message,
        JsonException => "响应格式错误",
        _ => ex.Message.Length > 80 ? ex.Message[..80] : ex.Message
    };
}
