using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppLauncher.Models;

namespace AppLauncher.Services;

/// <summary>
/// 向 GitHub 查詢最新 Release 並下載附件。只支援公開 repo，不需要 token。
/// 先從 github.com 網頁查（沒有次數限制）；網頁格式看不懂時才改用 API。
/// 未登入的 API 每小時只能查 60 次，而且同一個網路的電腦共用，連 304 也算一次，所以不能常用。
/// 連不上網路時改用上次快取的結果。
/// </summary>
public sealed partial class GitHubService : IDisposable
{
    private sealed record CacheEntry(string? ETag, string Json);

    /// <summary>網頁查詢的結果：查到版本、確定沒有版本，或網頁格式看不懂（改用 API）。</summary>
    private enum WebResult { Found, NoRelease, Unknown }

    private readonly HttpClient _http;
    private readonly HttpClient _noRedirect; // 查最新版時要讀轉址的目的地，不能自動跟過去
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache;

    public GitHubService(string cachePath, HttpMessageHandler? handler = null)
    {
        _cachePath = cachePath;
        _cache = LoadCache(cachePath);
        _http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _noRedirect = handler == null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        foreach (var client in new[] { _http, _noRedirect })
        {
            client.Timeout = Timeout.InfiniteTimeSpan; // 大檔下載很久，逾時由各呼叫自己控制
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AppLauncher", "1.0"));
        }
    }

    public async Task<ReleaseInfo?> GetLatestAsync(AppDefinition app, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{app.Repo}/releases/latest";
        string json;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var (result, webJson) = await GetLatestFromWebAsync(app, timeout.Token);
            if (result == WebResult.NoRelease) return null;
            if (result == WebResult.Found)
            {
                // 存成跟 API 一樣的格式，離線時沿用
                _cache[url] = new CacheEntry(null, webJson!);
                return ParseRelease(webJson!, app);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            _cache.TryGetValue(url, out var cached);
            if (cached?.ETag != null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
                json = cached.Json;
            else if (response.StatusCode == HttpStatusCode.NotFound)
                return null; // repo 還沒有任何 Release，或 repo 是私有的（沒登入看不到）
            else if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return FromCacheOr(url, app, "GitHub 查詢次數已達上限，請過一小時再重新整理");
            else
            {
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(timeout.Token);
                _cache[url] = new CacheEntry(response.Headers.ETag?.ToString(), json);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return FromCacheOr(url, app, "連不上 GitHub，請確認網路");
        }
        return ParseRelease(json, app);
    }

    /// <summary>
    /// 從網頁查最新版：github.com/&lt;repo&gt;/releases/latest 會轉址到 /releases/tag/&lt;版本&gt;，
    /// 再從 expanded_assets 頁面讀出附件的檔名、大小與時間。回傳的 JSON 與 API 同格式。
    /// </summary>
    private async Task<(WebResult, string?)> GetLatestFromWebAsync(AppDefinition app, CancellationToken ct)
    {
        var baseUrl = $"https://github.com/{app.Repo}/releases";
        using (var latest = await _noRedirect.GetAsync(baseUrl + "/latest", HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (latest.StatusCode == HttpStatusCode.NotFound) return (WebResult.NoRelease, null); // repo 不存在或是私有的
            if ((int)latest.StatusCode is < 300 or >= 400 || latest.Headers.Location is not { } location)
                return (WebResult.Unknown, null);

            var match = TagInLocation().Match(location.OriginalString);
            if (!match.Success)
                // 沒有任何 Release 時會轉到 /releases
                return location.OriginalString.TrimEnd('/').EndsWith("/releases", StringComparison.OrdinalIgnoreCase)
                    ? (WebResult.NoRelease, null) : (WebResult.Unknown, null);

            var tag = Uri.UnescapeDataString(match.Groups["tag"].Value);
            using var assets = await _http.GetAsync($"{baseUrl}/expanded_assets/{Uri.EscapeDataString(tag)}", ct);
            if (!assets.IsSuccessStatusCode) return (WebResult.Unknown, null);
            var html = await assets.Content.ReadAsStringAsync(ct);
            var json = ParseExpandedAssets(html, tag, $"{baseUrl}/tag/{Uri.EscapeDataString(tag)}");
            return json == null ? (WebResult.Unknown, null) : (WebResult.Found, json);
        }
    }

    /// <summary>把 expanded_assets 頁面轉成 API 格式的 JSON。一個附件都找不到時回傳 null。</summary>
    internal static string? ParseExpandedAssets(string html, string tag, string htmlUrl)
    {
        var links = AssetLink().Matches(html);
        if (links.Count == 0) return null;

        var assets = new List<object>();
        DateTimeOffset? published = null;
        for (var i = 0; i < links.Count; i++)
        {
            // 每個附件的大小與時間，寫在它的連結到下一個連結之間
            var start = links[i].Index + links[i].Length;
            var end = i + 1 < links.Count ? links[i + 1].Index : html.Length;
            var block = html[start..end];

            var href = WebUtility.HtmlDecode(links[i].Groups["href"].Value);
            var size = AssetSize().Match(block) is { Success: true } s ? ToBytes(s) : 0;
            if (AssetTime().Match(block) is { Success: true } t &&
                DateTimeOffset.TryParse(t.Groups[1].Value, out var time) && (published == null || time > published))
                published = time;

            assets.Add(new Dictionary<string, object>
            {
                ["name"] = Uri.UnescapeDataString(href[(href.LastIndexOf('/') + 1)..]),
                ["size"] = size,
                ["browser_download_url"] = "https://github.com" + href,
            });
        }

        var release = new Dictionary<string, object> { ["tag_name"] = tag, ["html_url"] = htmlUrl, ["assets"] = assets };
        if (published != null) release["published_at"] = published.Value;
        return JsonSerializer.Serialize(release);
    }

    /// <summary>網頁上的大小是約略值（例如 61.9 MB），只用來顯示；下載時以實際檔案大小為準。</summary>
    private static long ToBytes(Match size)
    {
        var value = double.Parse(size.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = size.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            _ => 1d,
        };
        return (long)(value * unit);
    }

    [GeneratedRegex(@"/releases/tag/(?<tag>[^/?#]+)/?(?:[?#]|$)")]
    private static partial Regex TagInLocation();

    [GeneratedRegex("href=\"(?<href>/[^\"]+/releases/download/[^\"]+)\"")]
    private static partial Regex AssetLink();

    [GeneratedRegex(@">\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>Bytes|Byte|B|KB|MB|GB)\s*<", RegexOptions.IgnoreCase)]
    private static partial Regex AssetSize();

    [GeneratedRegex("datetime=\"([^\"]+)\"")]
    private static partial Regex AssetTime();

    private ReleaseInfo? FromCacheOr(string url, AppDefinition app, string message) =>
        _cache.TryGetValue(url, out var cached) ? ParseRelease(cached.Json, app) : throw new InvalidOperationException(message);

    internal static ReleaseInfo ParseRelease(string json, AppDefinition app)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var pattern = new Regex(app.Asset, RegexOptions.IgnoreCase);
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!pattern.IsMatch(name)) continue;
            return new ReleaseInfo(
                tag,
                name,
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.GetProperty("size").GetInt64(),
                root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "",
                root.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String
                    ? published.GetDateTimeOffset() : DateTimeOffset.MinValue);
        }
        throw new InvalidOperationException($"{tag} 沒有附上符合「{app.Asset}」的檔案");
    }

    /// <summary>
    /// 下載到 destination，progress 回報 0~1（不知道大小時回報 -1）。
    /// expectedSize 可能是網頁上的約略值，只在伺服器沒給大小時拿來估進度。
    /// </summary>
    public async Task DownloadAsync(string url, string destination, long expectedSize, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var exactSize = response.Content.Headers.ContentLength;
        var total = exactSize ?? expectedSize;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destination);
        var buffer = new byte[81920];
        long done = 0;
        var lastReport = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (done - lastReport >= 256 * 1024 || done == total)
            {
                lastReport = done;
                progress?.Report(total > 0 ? Math.Min(1, (double)done / total) : -1);
            }
        }
        if (exactSize is > 0 && done != exactSize)
            throw new IOException($"下載不完整（{done:N0} / {exactSize:N0} bytes）");
    }

    /// <summary>
    /// 下載文字檔（線上 APP 清單）。一樣用 ETag 快取，內容沒變時不重抓。
    /// 失敗時回傳 null，由呼叫的人沿用手上的版本。
    /// </summary>
    public async Task<string?> GetTextAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            _cache.TryGetValue(url, out var cached);
            if (cached?.ETag != null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotModified && cached != null) return cached.Json;
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            _cache[url] = new CacheEntry(response.Headers.ETag?.ToString(), text);
            return text;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// 下載 repo 裡的圖示（apps.json 的 icon），還沒下載 APP 前顯示用。存在 iconDir，7 天內不重抓。
    /// 抓不到就回傳舊的快取或 null，不影響其他功能。
    /// </summary>
    public async Task<string?> GetIconAsync(AppDefinition app, string iconDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(app.Icon)) return null;
        var path = Path.Combine(iconDir, app.Id + Path.GetExtension(app.Icon).ToLowerInvariant());
        if (File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < TimeSpan.FromDays(7)) return path;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var url = $"https://raw.githubusercontent.com/{app.Repo}/HEAD/{app.Icon.TrimStart('/')}";
            var bytes = await _http.GetByteArrayAsync(url, timeout.Token);
            Directory.CreateDirectory(iconDir);
            await File.WriteAllBytesAsync(path, bytes, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested)) { }
        return File.Exists(path) ? path : null;
    }

    public void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 快取存不了只是下次多查一次
    }

    private static ConcurrentDictionary<string, CacheEntry> LoadCache(string path)
    {
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(path)) is { } data)
                return new ConcurrentDictionary<string, CacheEntry>(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new ConcurrentDictionary<string, CacheEntry>();
    }

    public void Dispose()
    {
        _http.Dispose();
        _noRedirect.Dispose();
    }
}
