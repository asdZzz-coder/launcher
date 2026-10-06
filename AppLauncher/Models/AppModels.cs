namespace AppLauncher.Models;

/// <summary>apps.json 裡的一個 APP：從哪個 repo 的 Release 下載哪個檔案、下載後要執行哪個 exe。</summary>
/// <param name="Id">資料夾名稱用的代號，只能用英數字與 -_。</param>
/// <param name="Asset">Release 附件檔名的規則運算式（不分大小寫）。可以是 .zip 或單一 .exe。</param>
/// <param name="Exe">要啟動的 exe 檔名，會在解壓後的資料夾裡找。</param>
public sealed record AppDefinition(string Id, string Name, string Repo, string Asset, string Exe, string? Description = null);

/// <summary>GitHub 上最新的 Release 與要下載的附件。</summary>
public sealed record ReleaseInfo(string Tag, string AssetName, string DownloadUrl, long Size, string HtmlUrl, DateTimeOffset PublishedAt);

/// <summary>已下載到本機的一個版本。</summary>
public sealed record InstalledVersion(string Tag, string Directory, string ExePath, DateTime InstalledAt);
