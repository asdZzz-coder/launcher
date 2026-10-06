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
/// 未登入的 API 每小時只能查 60 次，所以用 ETag 快取：內容沒變時 GitHub 回 304，不算次數。
/// 連不上網路時改用上次快取的結果。
/// </summary>
public sealed class GitHubService : IDisposable
{
    private sealed record CacheEntry(string? ETag, string Json);

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache;

    public GitHubService(string cachePath, HttpMessageHandler? handler = null)
    {
        _cachePath = cachePath;
        _cache = LoadCache(cachePath);
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // 大檔下載很久，逾時由各呼叫自己控制
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AppLauncher", "1.0"));
    }

    public async Task<ReleaseInfo?> GetLatestAsync(AppDefinition app, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{app.Repo}/releases/latest";
        string json;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
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

    /// <summary>下載到 destination，progress 回報 0~1（不知道大小時回報 -1）。</summary>
    public async Task DownloadAsync(string url, string destination, long expectedSize, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;

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
                progress?.Report(total > 0 ? (double)done / total : -1);
            }
        }
        if (total > 0 && done != total)
            throw new IOException($"下載不完整（{done:N0} / {total:N0} bytes）");
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

    public void Dispose() => _http.Dispose();
}
