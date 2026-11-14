using System.Globalization;

namespace LinuxCleaner;

internal sealed record ScanFilters(ulong MinimumBytes, string[] ExcludedPaths)
{
    internal static readonly ScanFilters None = new(0, []);

    internal static ScanFilters Create(string home, IEnumerable<string> exclusions, ulong minimumBytes)
    {
        home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        if (home == "/")
            throw new ArgumentException("排除路径需要有效的用户 HOME。");
        var paths = new List<string>();
        foreach (var exclusion in exclusions)
        {
            if (string.IsNullOrWhiteSpace(exclusion))
                throw new ArgumentException("--exclude 不能是空路径。");
            var path = exclusion;
            if (path == "~")
                path = home;
            else if (path.StartsWith("~/", StringComparison.Ordinal))
                path = Path.Combine(home, path[2..]);
            else if (path.StartsWith('~'))
                throw new ArgumentException("--exclude 只支持 ~ 或 ~/，不支持 ~其他用户。");
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, home));
            if (path != home && !Cleaners.IsWithin(path, home))
                throw new ArgumentException($"排除路径必须在当前 HOME 内：{path}");
            paths.Add(path);
        }
        return new(minimumBytes, paths.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal bool Excludes(string path) => ExcludedPaths.Any(excluded =>
        path == excluded || Cleaners.IsWithin(path, excluded));

    internal static ulong ParseBytes(string value)
    {
        var digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[digits])) digits++;
        if (digits == 0 || !ulong.TryParse(value.AsSpan(0, digits), NumberStyles.None,
            CultureInfo.InvariantCulture, out var number))
            throw new ArgumentException("--min-size 需要非负整数字节数，可带 B/KB/MB/GB/TB 或 KiB/MiB/GiB/TiB。");
        var multiplier = value[digits..].ToUpperInvariant() switch
        {
            "" or "B" => 1UL,
            "KB" => 1000UL,
            "MB" => 1000UL * 1000,
            "GB" => 1000UL * 1000 * 1000,
            "TB" => 1000UL * 1000 * 1000 * 1000,
            "KIB" => 1UL << 10,
            "MIB" => 1UL << 20,
            "GIB" => 1UL << 30,
            "TIB" => 1UL << 40,
            _ => throw new ArgumentException("--min-size 单位仅支持 B/KB/MB/GB/TB 或 KiB/MiB/GiB/TiB，不支持小数。")
        };
        try { return checked(number * multiplier); }
        catch (OverflowException) { throw new ArgumentException("--min-size 超出 64 位无符号字节数范围。"); }
    }
}
