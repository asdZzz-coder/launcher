using System.Net;
using System.Net.Http.Headers;
using AppLauncher.Models;
using AppLauncher.Services;

namespace AppLauncher.Tests;

public sealed class GitHubServiceTests : IDisposable
{
    private const string ReleaseJson = """
        {
          "tag_name": "v1.0.14",
          "html_url": "https://github.com/asdZzz-coder/tools/releases/tag/v1.0.14",
          "published_at": "2026-09-01T08:00:00Z",
          "assets": [
            { "name": "Ledger-ClickOnce-v1.0.14.zip", "size": 100, "browser_download_url": "https://example/zip" },
            { "name": "Ledger.exe", "size": 200, "browser_download_url": "https://example/exe" }
          ]
        }
        """;

    private static readonly AppDefinition Ledger = new("ledger", "簡易記帳", "asdZzz-coder/tools", "^Ledger\\.exe$", "Ledger.exe");

    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"AppLauncherTests-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_cache);

    [Fact]
    public void ParseRelease_PicksMatchingAsset()
    {
        var release = GitHubService.ParseRelease(ReleaseJson, Ledger);

        Assert.Equal("v1.0.14", release.Tag);
        Assert.Equal("Ledger.exe", release.AssetName);
        Assert.Equal(200, release.Size);
    }

    [Fact]
    public void ParseRelease_NoMatchingAsset_Throws() =>
        Assert.Throws<InvalidOperationException>(() => GitHubService.ParseRelease(ReleaseJson, Ledger with { Asset = "\\.msi$" }));

    [Fact]
    public async Task GetLatest_SendsETag_AndUses304()
    {
        var handler = new FakeHandler(
            _ => Json(HttpStatusCode.OK, ReleaseJson, "\"abc\""),
            req =>
            {
                Assert.Equal("\"abc\"", req.Headers.IfNoneMatch.Single().ToString());
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });
        using var github = new GitHubService(_cache, handler);

        Assert.Equal("v1.0.14", (await github.GetLatestAsync(Ledger))!.Tag);
        Assert.Equal("v1.0.14", (await github.GetLatestAsync(Ledger))!.Tag);
    }

    [Fact]
    public async Task GetLatest_Offline_UsesSavedCache()
    {
        using (var online = new GitHubService(_cache, new FakeHandler(_ => Json(HttpStatusCode.OK, ReleaseJson, "\"abc\""))))
        {
            await online.GetLatestAsync(Ledger);
            online.SaveCache();
        }

        using var offline = new GitHubService(_cache, new FakeHandler(_ => throw new HttpRequestException("offline")));
        Assert.Equal("v1.0.14", (await offline.GetLatestAsync(Ledger))!.Tag);
    }

    [Fact]
    public async Task GetLatest_OfflineWithoutCache_ThrowsFriendlyMessage()
    {
        using var github = new GitHubService(_cache, new FakeHandler(_ => throw new HttpRequestException("offline")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => github.GetLatestAsync(Ledger));
        Assert.Contains("網路", ex.Message);
    }

    [Fact]
    public async Task GetLatest_NoReleases_ReturnsNull()
    {
        using var github = new GitHubService(_cache, new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await github.GetLatestAsync(Ledger));
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json, string etag)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(json) };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    /// <summary>依序回傳預先準備的回應。</summary>
    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var next = responses[Math.Min(_calls++, responses.Length - 1)];
            return Task.FromResult(next(request));
        }
    }
}
