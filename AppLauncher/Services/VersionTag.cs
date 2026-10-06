namespace AppLauncher.Services;

/// <summary>比較 Release 標籤（v1.0.15、1.2、v2.0.0-beta）。看不懂的標籤退回字串比較。</summary>
public static class VersionTag
{
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!s.Contains('.')) s += ".0";
        if (!Version.TryParse(s, out var v)) return null;
        // 補齊成四段，讓 1.0.15 和 1.0.15.0 視為同一版
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    public static int Compare(string? a, string? b)
    {
        var va = Parse(a);
        var vb = Parse(b);
        if (va != null && vb != null) return va.CompareTo(vb);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;
}
