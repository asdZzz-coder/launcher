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

    // 實際 expanded_assets 頁面的節錄（每個附件一段：連結、sha256、大小、時間）
    private const string AssetsHtml = """
        <li class="Box-row d-flex flex-column flex-md-row">
          <a href="/asdZzz-coder/tools/releases/download/v1.0.17/Ledger-ClickOnce-v1.0.17.zip" rel="nofollow" data-turbo="false" class="wb-break-all">
          <span class="Truncate-text">sha256:3c001293f7a353c555c979c0eb9489c46b45e45eaf902b2c73766c1fe24dcfca</span>
          <span style="white-space: nowrap;" class="color-fg-muted text-right">58.1 MB</span>
          <span class="color-fg-muted"><relative-time datetime="2026-10-06T01:09:42Z" class="no-wrap" prefix="">2026-10-06T01:09:42Z</relative-time></span>
        </li>
        <li class="Box-row d-flex flex-column flex-md-row">
          <a href="/asdZzz-coder/tools/releases/download/v1.0.17/Ledger%20Setup.exe" rel="nofollow" data-turbo="false" class="wb-break-all">
          <span style="white-space: nowrap;" class="color-fg-muted text-right">512 Bytes</span>
          <span class="color-fg-muted"><relative-time datetime="2026-10-06T01:11:20Z" class="no-wrap" prefix="">2026-10-06T01:11:20Z</relative-time></span>
        </li>
        <li class="Box-row d-flex flex-column flex-md-row">
          <a href="/asdZzz-coder/tools/archive/refs/tags/v1.0.17.zip" rel="nofollow" data-turbo="false">Source code (zip)</a>
        </li>
        """;

    [Fact]
    public void ParseExpandedAssets_ReadsNamesSizesAndTime()
    {
        var json = GitHubService.ParseExpandedAssets(AssetsHtml, "v1.0.17", "https://github.com/asdZzz-coder/tools/releases/tag/v1.0.17")!;

        var zip = GitHubService.ParseRelease(json, Ledger with { Asset = "^Ledger-ClickOnce.*\\.zip$" });
        Assert.Equal("v1.0.17", zip.Tag);
        Assert.Equal("https://github.com/asdZzz-coder/tools/releases/download/v1.0.17/Ledger-ClickOnce-v1.0.17.zip", zip.DownloadUrl);
        Assert.Equal((long)(58.1 * 1024 * 1024), zip.Size);
        Assert.Equal(DateTimeOffset.Parse("2026-10-06T01:11:20Z"), zip.PublishedAt); // 取最新的附件時間

        var setup = GitHubService.ParseRelease(json, Ledger with { Asset = "Setup\\.exe$" });
        Assert.Equal("Ledger Setup.exe", setup.AssetName);
        Assert.Equal(512, setup.Size);

        // 原始碼壓縮檔不是附件
        Assert.Throws<InvalidOperationException>(() => GitHubService.ParseRelease(json, Ledger with { Asset = "^v1\\.0\\.17\\.zip$" }));
    }

    [Fact]
    public void ParseExpandedAssets_NoAssets_ReturnsNull() =>
        Assert.Null(GitHubService.ParseExpandedAssets("<html>改版了</html>", "v1", "https://example"));

    [Fact]
    public async Task GetLatest_UsesWebPages_NotTheRateLimitedApi()
    {
        var handler = new FakeHandler(_ => throw new Xunit.Sdk.XunitException("不該呼叫 API"))
        {
            Web = req => req.RequestUri!.AbsolutePath switch
            {
                "/asdZzz-coder/tools/releases/latest" => Redirect("https://github.com/asdZzz-coder/tools/releases/tag/v1.0.17"),
                "/asdZzz-coder/tools/releases/expanded_assets/v1.0.17" => Html(AssetsHtml),
                var path => throw new Xunit.Sdk.XunitException("沒預期的網址 " + path),
            },
        };
        using var github = new GitHubService(_cache, handler);

        var release = await github.GetLatestAsync(Ledger with { Asset = "^Ledger-ClickOnce.*\\.zip$" });

        Assert.Equal("v1.0.17", release!.Tag);
        Assert.Equal("https://github.com/asdZzz-coder/tools/releases/tag/v1.0.17", release.HtmlUrl);
        Assert.Equal(0, handler.ApiCalls);
    }

    [Fact]
    public async Task GetLatest_WebSaysNoRelease_ReturnsNull()
    {
        var handler = new FakeHandler(_ => throw new Xunit.Sdk.XunitException("不該呼叫 API"))
        {
            Web = _ => Redirect("https://github.com/asdZzz-coder/tools/releases"),
        };
        using var github = new GitHubService(_cache, handler);

        Assert.Null(await github.GetLatestAsync(Ledger));
    }

    [Fact]
    public async Task GetLatest_FromWeb_IsCachedForOffline()
    {
        var asset = Ledger with { Asset = "^Ledger-ClickOnce.*\\.zip$" };
        using (var online = new GitHubService(_cache, new FakeHandler
               {
                   Web = req => req.RequestUri!.AbsolutePath.EndsWith("/latest")
                       ? Redirect("https://github.com/asdZzz-coder/tools/releases/tag/v1.0.17")
                       : Html(AssetsHtml),
               }))
        {
            await online.GetLatestAsync(asset);
            online.SaveCache();
        }

        var offlineHandler = new FakeHandler(_ => throw new HttpRequestException("offline"))
        {
            Web = _ => throw new HttpRequestException("offline"),
        };
        using var offline = new GitHubService(_cache, offlineHandler);
        Assert.Equal("v1.0.17", (await offline.GetLatestAsync(asset))!.Tag);
    }

    [Fact]
    public async Task GetLatest_WebLayoutChanged_FallsBackToApi()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, ReleaseJson, "\"abc\""))
        {
            Web = req => req.RequestUri!.AbsolutePath.EndsWith("/latest")
                ? Redirect("https://github.com/asdZzz-coder/tools/releases/tag/v1.0.14")
                : Html("<html>改版了</html>"),
        };
        using var github = new GitHubService(_cache, handler);

        Assert.Equal("v1.0.14", (await github.GetLatestAsync(Ledger))!.Tag);
        Assert.Equal(1, handler.ApiCalls);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json, string etag)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent(json) };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html) };

    /// <summary>
    /// API 的請求依序回傳預先準備的回應；github.com 網頁的請求交給 Web，
    /// 沒設定時回 503，讓程式改用 API。
    /// </summary>
    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Web { get; init; }

        public int ApiCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "github.com")
                return Task.FromResult(Web?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var next = responses[Math.Min(ApiCalls++, responses.Length - 1)];
            return Task.FromResult(next(request));
        }
    }
}
