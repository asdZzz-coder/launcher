using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AppLauncher.Models;

namespace AppLauncher.Services;

/// <summary>
/// 找出哪些版本正在執行，以及關閉它們。用 exe 的完整路徑比對，
/// 所以同一個 APP 的新舊版本分得出來，自己從桌面捷徑開的程式（不在啟動器資料夾裡）也不會被算進來。
/// </summary>
public static class RunningApps
{
    /// <summary>正在執行的程式的完整路徑。只查 exeNames 這幾個程式名稱，不用每個程式都查。</summary>
    public static HashSet<string> GetRunningExePaths(IEnumerable<string> exeNames)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in exeNames.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
                if (ImagePath(process.Id) is { } path) result.Add(path);
        }
        return result;
    }

    /// <summary>這個版本正在執行的程式。用完要 Dispose。</summary>
    public static List<Process> Find(InstalledVersion version)
    {
        var target = Path.GetFullPath(version.ExePath);
        var list = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(version.ExePath)))
        {
            if (string.Equals(ImagePath(process.Id), target, StringComparison.OrdinalIgnoreCase)) list.Add(process);
            else process.Dispose();
        }
        return list;
    }

    /// <summary>
    /// 請程式自己關閉（跟按視窗右上角的 X 一樣，程式可以先問要不要儲存）。
    /// 在 timeout 內全部關掉就回傳 true。
    /// </summary>
    public static async Task<bool> CloseAsync(IReadOnlyList<Process> processes, TimeSpan timeout)
    {
        foreach (var process in processes)
        {
            try { process.CloseMainWindow(); }
            catch (InvalidOperationException) { } // 已經結束了
        }
        return await WaitForExitAsync(processes, timeout);
    }

    /// <summary>強制結束，還沒儲存的資料會遺失。</summary>
    public static async Task<bool> KillAsync(IReadOnlyList<Process> processes, TimeSpan timeout)
    {
        foreach (var process in processes)
        {
            try { process.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }
        return await WaitForExitAsync(processes, timeout);
    }

    private static async Task<bool> WaitForExitAsync(IReadOnlyList<Process> processes, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await Task.WhenAll(processes.Select(p => p.WaitForExitAsync(cts.Token)));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// 程式的 exe 完整路徑。用最低的查詢權限，32 位元的程式也查得到；查不到（例如系統管理員身分的程式）回傳 null。
    /// </summary>
    private static string? ImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint process, int flags, char[] buffer, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
