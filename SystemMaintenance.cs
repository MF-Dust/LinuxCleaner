using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LinuxCleaner;

internal sealed record SystemOptions(string Command, string? Operation, string Manager,
    int KeepDays, int KeepPackages, bool Yes, bool DryRun, bool Json)
{
    internal static SystemOptions Parse(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"] || args.Contains("--help"))
            return new("help", null, "auto", 14, 2, false, false, false);
        var command = args[0];
        if (command is not ("list" or "scan" or "clean"))
            throw new ArgumentException("system 需要 list、scan 或 clean 子命令。");
        string? operation = null;
        var manager = "auto";
        var days = 14;
        var keep = 2;
        bool yes = false, dryRun = false, json = false;
        HashSet<string> seen = [];
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-'))
            {
                if (operation is not null || arg is not ("packages" or "journal"))
                    throw new ArgumentException("system 每次必须且只能选择 packages 或 journal 之一。");
                operation = arg;
                continue;
            }
            if (!seen.Add(arg))
                throw new ArgumentException($"重复参数：{arg}");
            switch (arg)
            {
                case "--manager":
                    if (++i >= args.Length || args[i] is not ("auto" or "apt" or "pacman" or "dnf" or "dnf5"))
                        throw new ArgumentException("--manager 只支持 auto、apt、pacman、dnf 或 dnf5。");
                    manager = args[i];
                    break;
                case "--keep-days": days = ReadNumber(args, ref i, 1, 36500); break;
                case "--keep-packages": keep = ReadNumber(args, ref i, 2, 100); break;
                case "--yes": yes = true; break;
                case "--dry-run": dryRun = true; break;
                case "--json": json = true; break;
                default: throw new ArgumentException($"未知 system 参数：{arg}");
            }
        }
        if (command == "list")
        {
            if (operation is not null || seen.Any(flag => flag != "--json"))
                throw new ArgumentException("system list 仅支持 --json。");
        }
        else if (operation is null)
            throw new ArgumentException("请明确选择系统清理项目：packages 或 journal。");
        if (command != "clean" && (yes || dryRun))
            throw new ArgumentException("--yes 和 --dry-run 仅用于 system clean。");
        if (operation != "journal" && seen.Contains("--keep-days"))
            throw new ArgumentException("--keep-days 仅用于 journal，不是软件包的年龄筛选。");
        if (operation != "packages" && (seen.Contains("--manager") || seen.Contains("--keep-packages")))
            throw new ArgumentException("--manager 和 --keep-packages 仅用于 packages。");
        if (seen.Contains("--keep-packages") && manager != "pacman")
            throw new ArgumentException("--keep-packages 必须显式配合 --manager pacman。");
        return new(command, operation, manager, days, keep, yes, dryRun, json);
    }

    private static int ReadNumber(string[] args, ref int index, int min, int max)
    {
        var flag = args[index];
        if (++index >= args.Length || !int.TryParse(args[index], NumberStyles.None,
            CultureInfo.InvariantCulture, out var number) || number < min || number > max)
            throw new ArgumentException($"{flag} 需要 {min} 到 {max} 之间的整数。");
        return number;
    }
}

internal sealed record SystemCommand(string Executable, string[] Arguments);
internal sealed record SystemPlan(string Operation, string? Manager, string Description, SystemCommand Command);
internal sealed record CommandResult(int ExitCode, string Output, string Error);

internal static class SystemMaintenance
{
    internal static SystemPlan CreatePlan(SystemOptions options, Func<string, bool> exists)
    {
        if (options.Operation == "journal")
        {
            Require("/usr/bin/journalctl");
            return new("journal", null, $"删除 {options.KeepDays} 天前的归档 journal；不 rotate，不处理活跃日志。",
                new("/usr/bin/journalctl", ["--no-pager", $"--vacuum-time={options.KeepDays}d"]));
        }
        if (options.Operation != "packages")
            throw new ArgumentException("未知系统清理项目。");
        var manager = options.Manager;
        if (manager == "auto")
        {
            // Do not mistake an installed compatibility tool for the host package manager.
            var detected = new[] { ("pacman", "/usr/bin/pacman"), ("apt", "/usr/bin/apt-get"),
                ("dnf5", "/usr/bin/dnf5"), ("dnf", "/usr/bin/dnf") }
                .Where(item => exists(item.Item2)).Select(item => item.Item1).ToList();
            if (detected.Contains("dnf5")) detected.Remove("dnf");
            if (detected.Count != 1)
                throw new ArgumentException("无法唯一识别包管理器，请用 --manager 显式指定；支持 apt/pacman/dnf/dnf5。");
            manager = detected[0];
        }
        var plan = manager switch
        {
            "pacman" => new SystemPlan("packages", manager,
                $"清理 pacman 下载缓存，每个软件包至少保留 {options.KeepPackages} 个版本；不卸载软件包。",
                new("/usr/bin/paccache", ["--remove", "--keep", options.KeepPackages.ToString(CultureInfo.InvariantCulture),
                    "--nocolor"])),
            "apt" => new SystemPlan("packages", manager,
                "清空 APT 已下载的软件包缓存；不卸载软件包，不运行 autoremove。",
                new("/usr/bin/apt-get", ["clean"])),
            "dnf" or "dnf5" => new SystemPlan("packages", manager,
                "清理 DNF 已下载的软件包缓存；不删除元数据，不卸载软件包。",
                new($"/usr/bin/{manager}", ["clean", "packages"])),
            _ => throw new ArgumentException("不支持的包管理器。")
        };
        Require(plan.Command.Executable);
        return plan;

        void Require(string executable)
        {
            if (!exists(executable))
                throw new ArgumentException($"缺少系统工具：{executable}"
                    + (executable.EndsWith("/paccache", StringComparison.Ordinal) ? "（请安装 pacman-contrib）。" : "。"));
        }
    }

    internal static int Run(string[] args, TextReader input, TextWriter output, TextWriter error,
        bool inputRedirected, CancellationToken cancellation = default, bool? isRoot = null,
        Func<string, bool>? exists = null, Func<SystemCommand, CancellationToken, CommandResult>? execute = null)
    {
        var options = SystemOptions.Parse(args);
        if (options.Command == "help")
        {
            output.WriteLine(Help);
            return 0;
        }
        if (!OperatingSystem.IsLinux())
            throw new ArgumentException("系统清理仅支持 Linux。");
        if (options.Command == "list")
        {
            var operations = new[] {
                new { Id = "packages", Description = "软件包下载缓存（APT / pacman / DNF / DNF5）" },
                new { Id = "journal", Description = "旧的归档 journal 日志（默认保留 14 天）" }
            };
            if (options.Json) WriteJson(output, operations);
            else foreach (var operation in operations)
                output.WriteLine($"{operation.Id,-12} {operation.Description}");
            return 0;
        }
        var preview = options.Command == "scan" || options.DryRun;
        if (!preview)
        {
            if (!(isRoot ?? NativeDirectory.UserId == 0))
                throw new ArgumentException("系统清理需要 root；请先普通用户预览，再显式使用 sudo 运行 system clean。用户缓存命令仍禁止 root。");
            if (!options.Yes && (inputRedirected || options.Json))
                throw new ArgumentException("非交互或 JSON 系统清理必须显式提供 --yes。");
        }
        cancellation.ThrowIfCancellationRequested();
        var plan = CreatePlan(options, exists ?? File.Exists);
        const string notice = "系统预览仅展示命令和保留策略，不执行外部工具，也不估算可释放空间。"
            + "实际候选由系统工具在执行时决定，不是用户缓存的 inode 快照清理。";
        if (!options.Json)
        {
            output.WriteLine(plan.Description);
            output.WriteLine($"计划命令：{plan.Command.Executable} {string.Join(' ', plan.Command.Arguments)}");
            output.WriteLine(notice);
        }
        CommandResult? result = null;
        var cancelled = false;
        if (!preview)
        {
            if (!options.Yes)
            {
                output.Write("系统清理不可撤销。输入 DELETE 确认：");
                output.Flush();
                cancelled = input.ReadLine() != "DELETE";
            }
            if (!cancelled)
                result = (execute ?? Execute)(plan.Command, cancellation);
        }
        if (options.Json)
            WriteJson(output, new { DryRun = preview, Notice = notice, Plan = plan, Cancelled = cancelled, Result = result });
        else if (preview)
            output.WriteLine("预览完成，未执行清理。实际执行需 root 和确认。");
        else if (cancelled)
            output.WriteLine("已取消，未执行清理。");
        else if (result is not null)
        {
            WriteLines(output, result.Output);
            WriteLines(error, result.Error);
            output.WriteLine($"系统工具退出码：{result.ExitCode}");
        }
        return result is { ExitCode: not 0 } ? 1 : 0;
    }

    internal static CommandResult Execute(SystemCommand command, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = "/"
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        // Elevated tools must not inherit shell startup hooks, loader settings or a user-controlled PATH.
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["HOME"] = "/root";
        start.Environment["USER"] = "root";
        start.Environment["LOGNAME"] = "root";
        start.Environment["LC_ALL"] = "C.UTF-8";
        start.Environment["SYSTEMD_PAGER"] = "cat";
        start.Environment["SYSTEMD_COLORS"] = "0";
        using var process = Process.Start(start) ?? throw new IOException("无法启动系统工具。");
        process.StandardInput.Close();
        var stdout = ReadOutput(process.StandardOutput);
        var stderr = ReadOutput(process.StandardError);
        try
        {
            process.WaitForExitAsync(cancellation).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.WaitForExit();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            throw;
        }
        return new(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static async Task<string> ReadOutput(StreamReader reader)
    {
        const int limit = 65536;
        var text = new StringBuilder();
        var buffer = new char[4096];
        bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var keep = Math.Min(count, limit - text.Length);
            text.Append(buffer, 0, keep);
            truncated |= keep != count;
        }
        if (truncated) text.Append("\n[输出已截断至 65536 字符]");
        return text.ToString();
    }

    private static void WriteLines(TextWriter writer, string text)
    {
        foreach (var line in text.TrimEnd('\n').Split('\n'))
            if (line.Length > 0) writer.WriteLine(Cli.Display(line.TrimEnd('\r')));
    }

    private static void WriteJson<T>(TextWriter output, T value) =>
        output.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private const string Help = """
        LinuxCleaner — 显式系统清理（与用户缓存隔离）

        linuxcleaner system list [--json]
        linuxcleaner system scan  packages [--manager auto|apt|pacman|dnf|dnf5] [--json]
        linuxcleaner system clean packages [--manager ...] [--keep-packages 2]
                                          [--dry-run] [--yes] [--json]
        linuxcleaner system scan  journal [--keep-days 14] [--json]
        linuxcleaner system clean journal [--keep-days 14] [--dry-run] [--yes] [--json]

        scan / --dry-run 仅展示计划命令和策略，不启动系统工具，不估算可释放空间。
        clean 必须由 root 显式运行并确认；非交互/JSON 清理必须加 --yes。
        pacman 使用 paccache（pacman-contrib），默认至少保留两版本；
        --keep-packages 仅与 --manager pacman 配合，范围 2–100。
        APT / DNF 清空已下载包缓存，不卸载包；journal 只处理归档日志，不 rotate。
        --keep-days 范围 1–36500。用户缓存的 --older-than 不用于系统命令。
        不自动 sudo，不执行 autoremove，不接受自定义可执行文件或 shell 命令。
        """;
}
