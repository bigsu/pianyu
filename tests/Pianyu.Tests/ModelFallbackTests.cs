using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pianyu.App.Services;

namespace Pianyu.Tests;

[TestClass]
public sealed class ModelFallbackTests
{
    [TestMethod]
    public async Task RepeatedModelFailure_PausesRequests_WithoutThrowing()
    {
        var client = new HttpClient(new AlwaysFailHandler());
        var service = new ModelAssistantService(client);
        var configuration = new ModelConfiguration(true, "https://invalid.example/v3", "test-key", "primary", string.Empty, TimeSpan.FromSeconds(1), new Dictionary<string, bool> { ["title"] = true });
        for (var i = 0; i < 3; i++)
        {
            var result = await service.SuggestAsync("title", "本地内容", configuration, CancellationToken.None);
            Assert.AreEqual(0, result.Count);
        }
        Assert.IsTrue(service.IsTemporarilyPaused);
        Assert.AreEqual(0, (await service.SuggestAsync("title", "仍可继续本地编辑", configuration, CancellationToken.None)).Count);
    }

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    [TestMethod]
    public async Task ModelTimeout_ReturnsNoSuggestion_InsteadOfBlockingCaller()
    {
        var service = new ModelAssistantService(new HttpClient(new SlowHandler()));
        var configuration = new ModelConfiguration(true, "https://slow.example/v3", "test-key", "model", string.Empty, TimeSpan.FromMilliseconds(80), new Dictionary<string, bool> { ["title"] = true });
        var started = DateTime.UtcNow;
        var result = await service.SuggestAsync("title", "本地内容", configuration, CancellationToken.None);
        Assert.AreEqual(0, result.Count);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
