using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AppLauncher.Models;

namespace AppLauncher.Services;

/// <summary>
/// 管理下載到本機的各版本：root\&lt;id&gt;\&lt;版本&gt;\...。
/// 每個版本各自一個資料夾，更新時不動舊版，所以舊版隨時都還能開。
/// 各 APP 的使用者資料在 %AppData%，與這裡無關，刪除版本不會刪到資料。
/// </summary>
public sealed class AppStore(string root)
{
    /// <summary>安裝完成才寫入的標記檔，沒有它的資料夾視為沒裝好（例如下載到一半被關掉）。</summary>
    internal const string MarkerFile = ".launcher.json";
    private const string StagingPrefix = ".staging-";
    private const string TrashPrefix = ".trash-";

    private sealed record Marker(string Tag, string Exe, DateTime InstalledAt);

    public string Root { get; } = root;

    public string AppDirectory(AppDefinition app) => Path.Combine(Root, app.Id);

    public IReadOnlyList<InstalledVersion> GetInstalled(AppDefinition app)
    {
        var dir = AppDirectory(app);
        if (!Directory.Exists(dir)) return [];

        var list = new List<InstalledVersion>();
        foreach (var versionDir in Directory.EnumerateDirectories(dir))
        {
            if (Path.GetFileName(versionDir).StartsWith('.')) continue;
            var marker = ReadMarker(versionDir);
            if (marker == null) continue;
            var exe = Path.Combine(versionDir, marker.Exe);
            if (File.Exists(exe))
                list.Add(new InstalledVersion(marker.Tag, versionDir, exe, marker.InstalledAt));
        }
        list.Sort((a, b) => VersionTag.Compare(b.Tag, a.Tag)); // 新的在前
        return list;
    }

    /// <summary>把下載好的檔案（zip 或 exe）裝成一個版本。同版本已存在時覆蓋。</summary>
    public InstalledVersion Install(AppDefinition app, string tag, string assetName, string downloadedFile)
    {
        var appDir = AppDirectory(app);
        Directory.CreateDirectory(appDir);
        var staging = Path.Combine(appDir, StagingPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(downloadedFile, staging);
            else
                File.Copy(downloadedFile, Path.Combine(staging, assetName));

            var exe = FindExe(staging, app.Exe)
                ?? throw new InvalidOperationException($"下載的檔案裡找不到 {app.Exe}");
            var relativeExe = Path.GetRelativePath(staging, exe);
            var installedAt = DateTime.Now;
            File.WriteAllText(Path.Combine(staging, MarkerFile),
                JsonSerializer.Serialize(new Marker(tag, relativeExe, installedAt)));

            var target = Path.Combine(appDir, SafeFolderName(tag));
            if (Directory.Exists(target)) MoveToTrashAndDelete(target);
            Directory.Move(staging, target);
            return new InstalledVersion(tag, target, Path.Combine(target, relativeExe), installedAt);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <summary>
    /// 刪除一個版本。先把資料夾改名再刪：程式還開著時改名就會失敗，
    /// 這樣不會刪到一半留下壞掉的版本。
    /// </summary>
    public void Delete(InstalledVersion version)
    {
        try
        {
            MoveToTrashAndDelete(version.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{version.Tag} 無法刪除，可能還開著，請先關閉程式再試。", ex);
        }
    }

    public void DeleteAll(AppDefinition app)
    {
        foreach (var v in GetInstalled(app)) Delete(v);
        // 沒有其他版本了就把整個資料夾（含殘留檔）清掉
        var dir = AppDirectory(app);
        if (Directory.Exists(dir) && GetInstalled(app).Count == 0) TryDeleteDirectory(dir);
    }

    public static void Launch(InstalledVersion version)
    {
        Process.Start(new ProcessStartInfo(version.ExePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(version.ExePath)!,
        })?.Dispose();
    }

    /// <summary>清掉上次沒裝完或沒刪完的殘留資料夾。</summary>
    public void CleanupLeftovers()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var appDir in Directory.EnumerateDirectories(Root))
        foreach (var dir in Directory.EnumerateDirectories(appDir))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(StagingPrefix) || name.StartsWith(TrashPrefix)) TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// 在解壓後的資料夾裡找 exe，取最淺的一個。
    /// ClickOnce 安裝包裡的檔案可能是 xxx.exe.deploy，找到時把整個資料夾的 .deploy 去掉。
    /// </summary>
    internal static string? FindExe(string folder, string exeName)
    {
        var exe = Shallowest(Directory.EnumerateFiles(folder, exeName, SearchOption.AllDirectories));
        if (exe != null) return exe;

        var deploy = Shallowest(Directory.EnumerateFiles(folder, exeName + ".deploy", SearchOption.AllDirectories));
        if (deploy == null) return null;
        var deployDir = Path.GetDirectoryName(deploy)!;
        foreach (var file in Directory.GetFiles(deployDir, "*.deploy", SearchOption.AllDirectories))
            File.Move(file, file[..^".deploy".Length], overwrite: true);
        return deploy[..^".deploy".Length];
    }

    private static string? Shallowest(IEnumerable<string> paths) =>
        paths.OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    internal static string SafeFolderName(string tag)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(tag.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0 || name.StartsWith('.')) name = "_" + name;
        return name;
    }

    private static Marker? ReadMarker(string versionDir)
    {
        try
        {
            var path = Path.Combine(versionDir, MarkerFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void MoveToTrashAndDelete(string dir)
    {
        var trash = Path.Combine(Path.GetDirectoryName(dir)!, TrashPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.Move(dir, trash);
        TryDeleteDirectory(trash); // 刪不乾淨的下次啟動再清
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
