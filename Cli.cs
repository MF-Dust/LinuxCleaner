using System.Globalization;
using System.Text.Json;

namespace LinuxCleaner;

internal sealed record Options(string Command, string[] Cleaners, int OlderThanDays,
    bool Yes, bool DryRun, bool Json, bool Verbose, bool All = false,
    string[]? Exclusions = null, ulong MinimumBytes = 0, int Top = 0)
{
    internal static Options Parse(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
            return new("help", [], 7, false, false, false, false);
        if (args is ["--version"])
            return new("version", [], 7, false, false, false, false);
        var command = args[0];
        if (command is not ("list" or "scan" or "clean"))
            throw new ArgumentException($"未知命令：{command}");
        List<string> cleaners = [];
        List<string> exclusions = [];
        var days = 7;
        ulong minimumBytes = 0;
        var top = 0;
        bool yes = false, dryRun = false, json = false, verbose = false, all = false;
        HashSet<string> seen = [];
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                if (cleaners.Contains(arg))
                    throw new ArgumentException($"重复的清理规则：{arg}");
                cleaners.Add(arg);
                continue;
            }
            if (!seen.Add(arg) && arg != "--exclude")
                throw new ArgumentException($"重复参数：{arg}");
            switch (arg)
            {
                case "--older-than":
                    if (++i >= args.Length || !int.TryParse(args[i], NumberStyles.None,
                        CultureInfo.InvariantCulture, out days) || days is < 0 or > 36500)
                        throw new ArgumentException("--older-than 需要 0 到 36500 之间的整数天数。");
                    break;
                case "--exclude":
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith('-'))
                        throw new ArgumentException("--exclude 需要 HOME 内的路径，可重复指定；以 - 开头的文件名请使用 ./ 前缀。");
                    exclusions.Add(args[i]);
                    break;
                case "--min-size":
                    if (++i >= args.Length)
                        throw new ArgumentException("--min-size 需要字节数，例如 10MiB。");
                    minimumBytes = ScanFilters.ParseBytes(args[i]);
                    break;
                case "--top":
                    if (++i >= args.Length || !int.TryParse(args[i], NumberStyles.None,
                        CultureInfo.InvariantCulture, out top) || top is < 1 or > 1000)
                        throw new ArgumentException("--top 需要 1 到 1000 之间的整数。");
                    break;
                case "--yes": yes = true; break;
                case "--dry-run": dryRun = true; break;
                case "--json": json = true; break;
                case "--verbose": verbose = true; break;
                case "--all": all = true; break;
                case "--help": return new("help", [], 7, false, false, false, false);
                default: throw new ArgumentException($"未知参数：{arg}");
            }
        }
        if (command != "clean" && (yes || dryRun))
            throw new ArgumentException("--yes 和 --dry-run 仅用于 clean 命令。");
        if (command == "list" && (cleaners.Count > 0 || seen.Any(flag => flag != "--json")))
            throw new ArgumentException("list 仅支持 --json。");
        if (all && cleaners.Count > 0)
            throw new ArgumentException("--all 不能与具体规则 ID 同时使用。");
        if (command == "clean" && !dryRun && top > 0)
            throw new ArgumentException("--top 只用于 scan 或 clean --dry-run 的排行，不表示只清理前 N 项；真正清理请移除 --top。");
        return new(command, cleaners.ToArray(), days, yes, dryRun, json, verbose, all,
            exclusions.ToArray(), minimumBytes, top);
    }
}

internal static class Cli
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int Run(string[] args, TextReader input, TextWriter output, TextWriter error,
        bool inputRedirected, CancellationToken cancellation = default)
    {
        try
        {
            if (args.Length > 0 && args[0] == "rules")
                return CleanerMlImporter.Run(args[1..], output, cancellation);
            if (args.Length > 0 && args[0] == "system")
                return SystemMaintenance.Run(args[1..], input, output, error, inputRedirected, cancellation);
            var options = Options.Parse(args);
            if (options.Command == "help")
            {
                output.WriteLine(Help);
                return 0;
            }
            if (options.Command == "version")
            {
                output.WriteLine("linuxcleaner 0.4.0");
                return 0;
            }
            if (!OperatingSystem.IsLinux())
                throw new ArgumentException("本工具仅支持 Linux。");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home) || Path.GetFullPath(home) == "/")
                throw new ArgumentException("无法确定安全的用户 HOME。");
            var cleaners = Cleaners.Create(home, Environment.GetEnvironmentVariable("XDG_CACHE_HOME"));
            if (options.Command == "list")
            {
                if (options.Json)
                    WriteJson(output, cleaners.Select(c => new { c.Id, c.Description, c.Default, c.Roots }));
                else
                    foreach (var cleaner in cleaners)
                        output.WriteLine($"{cleaner.Id,-12} {(cleaner.Default ? "[默认]" : "[可选]")} {cleaner.Description}");
                return 0;
            }
            if (NativeDirectory.UserId == 0)
                throw new ArgumentException("用户缓存请以普通用户运行，不要使用 sudo/root；系统清理请使用 system 子命令。");
            var selected = options.All ? cleaners.ToArray() : options.Cleaners.Length == 0
                ? cleaners.Where(c => c.Default).ToArray()
                : options.Cleaners.Select(id => cleaners.FirstOrDefault(c => c.Id == id)
                    ?? throw new ArgumentException($"未知清理规则：{id}（使用 list 查看）")).ToArray();
            if (options.Command == "clean" && !options.DryRun && !options.Yes &&
                (inputRedirected || options.Json))
                throw new ArgumentException("非交互或 JSON 清理必须显式提供 --yes；可先使用 scan / --dry-run。");

            var cutoff = DateTimeOffset.UtcNow.AddDays(-options.OlderThanDays);
            var filters = ScanFilters.Create(home, options.Exclusions ?? [], options.MinimumBytes);
            var plan = CleaningEngine.Scan(selected, cutoff, cancellation, filters);
            var dryRun = options.Command == "scan" || options.DryRun;
            var topFiles = options.Top > 0 ? plan.LargestFiles(options.Top) : null;
            if (!options.Json)
            {
                output.WriteLine($"仅选择最后修改时间早于 {cutoff:O} 的文件；最小逻辑大小 {filters.MinimumBytes} 字节。");
                foreach (var exclusion in filters.ExcludedPaths)
                    output.WriteLine($"排除路径（包含子目录）：{Display(exclusion)}");
                foreach (var cleaner in selected)
                {
                    var files = plan.Roots.Where(r => r.Cleaner == cleaner.Id).SelectMany(r => r.Files).ToArray();
                    var bytes = files.Aggregate(0UL, (sum, file) => checked(sum + file.Stat.Size));
                    output.WriteLine($"{cleaner.Id,-12} {files.Length,8} 文件  {Size(bytes)}");
                }
                output.WriteLine($"合计：{plan.FileCount} 个文件，逻辑大小 {Size(plan.Bytes)}；跳过特殊条目 {plan.Skipped}。");
                if (options.Verbose)
                    foreach (var file in plan.PreviewFiles())
                        output.WriteLine(Display(file.Path));
                if (topFiles is not null)
                {
                    output.WriteLine($"最大的 {topFiles.Length} 个候选文件（仅排行，不改变候选范围）：");
                    foreach (var file in topFiles)
                        output.WriteLine($"{Size(file.Bytes),12}  [{file.Cleaner}] {Display(file.Path)}");
                }
                foreach (var warning in plan.Warnings)
                    error.WriteLine($"警告：{Display(warning)}");
                if (plan.WarningCount > plan.Warnings.Count)
                    error.WriteLine($"另有 {plan.WarningCount - plan.Warnings.Count} 条警告未显示。");
            }

            CleanResult? result = null;
            var cancelled = false;
            if (!dryRun && plan.FileCount > 0)
            {
                if (!options.Yes)
                {
                    output.Write("删除不可撤销，请先关闭相关应用。输入 DELETE 确认：");
                    output.Flush();
                    cancelled = input.ReadLine() != "DELETE";
                }
                if (!cancelled)
                    result = CleaningEngine.Clean(plan, cancellation);
            }
            if (options.Json)
            {
                WriteJson(output, new
                {
                    Command = options.Command, DryRun = dryRun, CutoffUtc = cutoff,
                    SelectedCleaners = selected.Select(c => c.Id), CandidateFiles = plan.FileCount,
                    CandidateBytes = plan.Bytes, SkippedEntries = plan.Skipped, Filters = filters,
                    plan.WarningCount, plan.Warnings, Result = result,
                    Files = options.Verbose ? plan.PreviewFiles() : null, TopFiles = topFiles
                });
            }
            else if (dryRun)
                output.WriteLine("预览完成，未删除任何文件。");
            else if (cancelled)
                output.WriteLine("已取消，未删除任何文件。");
            else if (result is not null)
            {
                output.WriteLine($"已删除 {result.DeletedFiles} 个文件，逻辑大小 {Size(result.DeletedBytes)}；"
                    + $"变化/消失 {result.ChangedOrMissing}，失败 {result.ErrorCount}。");
                foreach (var message in result.Errors)
                    error.WriteLine($"错误：{Display(message)}");
            }
            else
                output.WriteLine("没有可清理的文件。");
            return plan.WarningCount > 0 || result?.ErrorCount > 0 ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("操作中断；已完成的删除不可撤销。");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException
            or OverflowException or PlatformNotSupportedException or System.ComponentModel.Win32Exception
            or System.Xml.XmlException)
        {
            error.WriteLine($"错误：{Display(exception.Message)}");
            return 2;
        }
    }

    private static void WriteJson<T>(TextWriter output, T value) =>
        output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    // Prevent filenames from injecting terminal control sequences.
    internal static string Display(string text) => string.Concat(text.Select(c =>
        char.IsControl(c) || c is '\u2028' or '\u2029' or >= '\u202a' and <= '\u202e'
            or >= '\u2066' and <= '\u2069' ? $"\\u{(int)c:x4}" : c.ToString()));

    private static string Size(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.##} {units[index]}";
    }

    private const string Help = """
        LinuxCleaner — 安全优先的 Linux 用户缓存清理 CLI

        用法：
          linuxcleaner rules --help  （本地 CleanerML 转待审报告，不自动启用）
          linuxcleaner list [--json]
          linuxcleaner scan  [规则 ...] [--older-than 天数] [--verbose] [--json]
          linuxcleaner clean [规则 ...] [--older-than 天数] [--dry-run]
                             [--yes] [--verbose] [--json]
          筛选：--exclude 路径（可重复）  --min-size 10MiB
          排行：scan / clean --dry-run 可加 --top N（1–1000）

        规则：cache、thumbnails、browsers（默认）；其他规则使用 list 查看。
        --all 显式选择所有用户规则（不能与规则 ID 混用，不包含系统清理）。
        默认只处理 7 天前修改的普通文件；--older-than 0 选择扫描开始前的文件。
        scan 和 clean --dry-run 不删除文件；clean 先预览，再要求输入 DELETE。
        --yes 跳过确认，供自动化使用；非交互/JSON 清理必须配合 --yes。
        --verbose 列出候选路径；--json 输出机器可读结果，诊断写入 stderr。
        --exclude 排除 HOME 内的路径及其子树；相对路径以 HOME 为基准，支持 ~/。
        --min-size 是包含边界的逻辑大小下限；KB=1000，KiB=1024，仅支持整数。
        --top 仅用于预览，不是只删除前 N 项；实际 clean 不能使用该参数。

        不跟随符号链接、不跨子挂载点、不处理硬链接/特殊文件或其他用户的文件。
        用户清理保留目录，不清空回收站、个人数据或浏览器历史/密码；禁止 sudo/root。
        请关闭相关应用后清理。系统包缓存/journal 使用独立的 system 子命令：
          linuxcleaner system --help

        退出码：0 完成/取消；1 部分失败或扫描警告；2 参数/环境错误；130 中断。
        """;
}
