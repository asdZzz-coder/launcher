using System.Diagnostics.Eventing.Reader;
using System.IO;

namespace AppLauncher.Services;

/// <summary>
/// Windows「智慧型應用程式控制」：沒有簽章、Microsoft 雲端又判斷信任度不夠的程式會被擋下。
/// 這裡只用來看出「是不是被它擋的」，好顯示看得懂的訊息。
/// </summary>
public static class SmartAppControl
{
    /// <summary>ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION：exe 本身被擋，Process.Start 直接失敗。</summary>
    public const int BlockedError = 4551;

    public const string BlockedMessage = "被 Windows「智慧型應用程式控制」擋下來了，無法開啟";

    /// <summary>
    /// since 之後有沒有擋下 directory 裡的檔案。exe 可以啟動、但它載入的 dll 被擋時，
    /// 程式會一開就結束，只能從系統紀錄看出原因。
    /// </summary>
    public static bool BlockedSince(string directory, DateTime since)
    {
        // 紀錄裡的路徑是 \Device\HarddiskVolume4\Users\...，所以不比對磁碟機代號
        var path = Path.GetFullPath(directory);
        path = path[Path.GetPathRoot(path)!.Length..];
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-CodeIntegrity/Operational", PathType.LogName,
                $"*[System[(EventID=3077 or EventID=3033) and TimeCreated[@SystemTime>='{since.AddSeconds(-2).ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]");
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
            {
                using (e)
                    if (e.ToXml().Contains(path, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException) { }
        return false;
    }
}
