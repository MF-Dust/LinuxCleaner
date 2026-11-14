using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LinuxCleaner;

internal sealed record ImportSource(string Url, string Revision, string DeclaredLicense, string Sha256);
internal sealed record ImportAction(string? Command, string? Search, string? Path, string Status,
    string[] CandidateRoots, string[] Issues);
internal sealed record ImportOption(string? Id, string Label, string Description, string Status,
    string[] Warnings, string[] Issues, ImportAction[] Actions, string[] CandidateRoots);
internal sealed record ImportReport(int SchemaVersion, string Status, bool AutoEnabled, ImportSource Source,
    string? CleanerId, string[] SourceNotices, string[] Issues, ImportOption[] Options);

internal static class CleanerMlImporter
{
    internal const int MaximumBytes = 1024 * 1024;
    private const int MaximumExpansion = 128;
    private static readonly Regex Variable = new(@"\$\$([A-Za-z_][A-Za-z0-9_]*)\$\$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static int Run(string[] args, TextWriter output, CancellationToken cancellation)
    {
        if (args is ["--help"] or ["-h"] or [])
        {
            output.WriteLine(Help);
            return 0;
        }
        if (args.Length != 8 || args[0] != "import")
            throw new ArgumentException("用法：rules import FILE --source HTTPS_URL --revision REV --license SPDX");
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i += 2)
            if (args[i] is not ("--source" or "--revision" or "--license") || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("无效或重复的规则导入参数。");
        var report = Import(args[1], values["--source"], values["--revision"], values["--license"], cancellation);
        output.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Issues.Length > 0 || report.Options.Any(o => o.Status == "Blocked") ? 1 : 0;
    }

    internal static ImportReport Import(string file, string source, string revision, string license,
        CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length > 0 || string.IsNullOrWhiteSpace(revision) || revision.Length > 128
            || string.IsNullOrWhiteSpace(license) || license.Length > 128
            || revision.Any(char.IsControl) || license.Any(char.IsControl))
            throw new ArgumentException("导入需要 HTTPS 来源 URL、明确版本和声明的许可证（均不自动核实）。");
        cancellation.ThrowIfCancellationRequested();
        // Read one bounded snapshot: the hash and parser always see the same bytes.
        using var stream = File.OpenRead(file);
        var bytes = new byte[MaximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = stream.Read(bytes, length, bytes.Length - length);
            if (count == 0) break;
            length += count;
        }
        if (length > MaximumBytes)
            throw new ArgumentException("XML 超过 1 MiB 限制。");
        using var snapshot = new MemoryStream(bytes, 0, length, false);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes,
            MaxCharactersFromEntities = MaximumBytes
        };
        using (var guard = XmlReader.Create(snapshot, settings))
        {
            var nodes = 0;
            while (guard.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                if (guard.Depth > 32 || ++nodes > 20000)
                    throw new ArgumentException("XML 嵌套或节点数超出限制。");
            }
        }
        snapshot.Position = 0;
        using var reader = XmlReader.Create(snapshot, settings);
        var document = XDocument.Load(reader);
        var root = document.Root;
        if (root?.Name != "cleaner")
            throw new ArgumentException("输入必须是无命名空间的 CleanerML cleaner XML。");
        if (root.Elements("option").Count() > 128 || root.Descendants("action").Count() > 256
            || root.Elements("var").Count() > 64)
            throw new ArgumentException("XML 规则、动作或变量数量超出限制。");

        var globalIssues = new List<string>();
        CheckAttributes(root, ["id", "os"], globalIssues);
        CheckChildren(root, ["label", "description", "var", "option", "warning"], globalIssues);
        var rootPlatform = Platform(root, globalIssues);
        var rootWarnings = root.Elements("warning").Select(e => e.Value).ToArray();
        if (rootWarnings.Length > 0) globalIssues.Add("上游 cleaner 含 warning，整个 cleaner 被阻止。");
        var variables = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var variable in root.Elements("var"))
        {
            if (!Platform(variable, globalIssues)) continue;
            CheckAttributes(variable, ["name", "os"], globalIssues);
            CheckChildren(variable, ["value"], globalIssues);
            var name = (string?)variable.Attribute("name");
            var options = new List<string>();
            foreach (var value in variable.Elements("value"))
            {
                if (!Platform(value, globalIssues)) continue;
                CheckAttributes(value, ["os"], globalIssues);
                if (value.HasElements) globalIssues.Add("变量 value 不允许嵌套元素。");
                options.Add(value.Value);
            }
            if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_]*\z")
                || !variables.TryAdd(name, options.ToArray()))
                globalIssues.Add("变量名称无效或重复。");
        }

        var optionReports = new List<ImportOption>();
        var ids = new HashSet<string?>(StringComparer.Ordinal);
        var duplicateIds = root.Elements("option").GroupBy(o => (string?)o.Attribute("id"))
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var remainingExpansion = 4096;
        foreach (var option in root.Elements("option"))
        {
            cancellation.ThrowIfCancellationRequested();
            var issues = new List<string>(globalIssues);
            var id = (string?)option.Attribute("id");
            var enabledPlatform = Platform(option, issues) && rootPlatform;
            CheckAttributes(option, ["id", "os"], issues);
            CheckChildren(option, ["label", "description", "warning", "action"], issues);
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || duplicateIds.Contains(id))
                issues.Add("option ID 为空或重复。");
            var warnings = rootWarnings.Concat(option.Elements("warning").Select(e => e.Value)).ToArray();
            if (warnings.Length > 0) issues.Add("上游含 warning，整个 option 被阻止。");
            var actions = new List<ImportAction>();
            foreach (var action in option.Elements("action"))
            {
                cancellation.ThrowIfCancellationRequested();
                var actionIssues = new List<string>();
                var command = (string?)action.Attribute("command");
                var search = (string?)action.Attribute("search");
                var path = (string?)action.Attribute("path");
                var actionPlatform = Platform(action, actionIssues) && enabledPlatform;
                var proposed = new List<string>();
                if (actionPlatform)
                {
                    CheckAttributes(action, ["command", "search", "path", "os"], actionIssues);
                    if (action.HasElements || !string.IsNullOrWhiteSpace(action.Value))
                        actionIssues.Add("不支持带内容的 action。");
                    if (command != "delete" || search is not ("walk.all" or "walk.files"))
                        actionIssues.Add("仅支持 delete + walk.all/walk.files 目录动作；不移植目录删除。");
                    if (string.IsNullOrEmpty(path)) actionIssues.Add("缺少 path。");
                    if (actionIssues.Count == 0)
                    {
                        try
                        {
                            foreach (var expanded in Expand(path!, variables, [], 0))
                            {
                                if (--remainingExpansion < 0)
                                    throw new ArgumentException("所有动作的变量展开总量超过 4096。");
                                proposed.Add(CacheRoot(expanded));
                            }
                        }
                        catch (ArgumentException error) { actionIssues.Add(error.Message); }
                    }
                }
                var status = !actionPlatform && actionIssues.Count == 0 ? "SkippedPlatform"
                    : actionIssues.Count > 0 ? "Blocked" : "PendingReview";
                actions.Add(new(command, search, path, status,
                    status == "PendingReview" ? proposed.Distinct(StringComparer.Ordinal).ToArray() : [],
                    actionIssues.ToArray()));
            }
            if (actions.Any(a => a.Status == "Blocked")) issues.Add("含不支持的动作，整个 option 被阻止，不能部分启用。");
            if (actions.Count == 0) issues.Add("option 没有动作。");
            var statusOption = !enabledPlatform && issues.Count == 0 ? "SkippedPlatform"
                : issues.Count > 0 ? "Blocked" : actions.All(a => a.Status == "SkippedPlatform") ? "SkippedPlatform" : "PendingReview";
            optionReports.Add(new(id, option.Element("label")?.Value ?? "", option.Element("description")?.Value ?? "",
                statusOption, warnings, issues.ToArray(), actions.ToArray(), statusOption == "PendingReview"
                    ? actions.SelectMany(a => a.CandidateRoots).Distinct(StringComparer.Ordinal).ToArray() : []));
        }
        if (optionReports.Count == 0) globalIssues.Add("没有可审核的 option。");
        return new(1, "PendingReview", false,
            new(source, revision, license, Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, length)))),
            (string?)root.Attribute("id"), document.DescendantNodes().OfType<XComment>().Select(c => c.Value).ToArray(),
            globalIssues.ToArray(), optionReports.ToArray());
    }

    private static IEnumerable<string> Expand(string input, Dictionary<string, string[]> variables,
        HashSet<string> stack, int depth)
    {
        if (input.Length > 4096) throw new ArgumentException("展开路径超过 4096 字符。");
        if (depth > 8) throw new ArgumentException("变量嵌套超过 8 层。");
        var match = Variable.Match(input);
        if (!match.Success)
        {
            if (input.Contains("$$", StringComparison.Ordinal)) throw new ArgumentException("无效变量引用。");
            return [input];
        }
        var name = match.Groups[1].Value;
        if (stack.Contains(name)) throw new ArgumentException("变量循环引用。");
        if (!variables.TryGetValue(name, out var values) || values.Length == 0)
            throw new ArgumentException($"变量未定义或没有 Linux 值：{name}");
        var result = new List<string>();
        foreach (var value in values)
        foreach (var expandedValue in Expand(value, variables, new(stack, StringComparer.Ordinal) { name }, depth + 1))
        foreach (var expanded in Expand(input.Replace(match.Value, expandedValue, StringComparison.Ordinal), variables, stack, depth + 1))
        {
            if (result.Count >= MaximumExpansion) throw new ArgumentException("单个动作的变量展开超过 128。");
            result.Add(expanded);
        }
        return result;
    }

    private static string CacheRoot(string path)
    {
        string[] xdgPrefixes = ["$XDG_CACHE_HOME/", "${XDG_CACHE_HOME}/"];
        string[] homePrefixes = ["~/.cache/", "$HOME/.cache/", "${HOME}/.cache/"];
        var prefix = xdgPrefixes.FirstOrDefault(p => path.StartsWith(p, StringComparison.Ordinal));
        var template = "${CACHE}/";
        if (prefix is null)
        {
            prefix = homePrefixes.FirstOrDefault(p => path.StartsWith(p, StringComparison.Ordinal));
            template = "${HOME}/.cache/";
        }
        if (prefix is null) throw new ArgumentException("仅接受 XDG_CACHE_HOME 或 ~/.cache 下已审核的缓存目录。");
        var suffix = path[prefix.Length..].TrimEnd('/');
        if (path.Length > 4096 || suffix.Split('/').Any(p => p.Length == 0 || p is "." or ".."
            || p.Any(c => char.IsControl(c) || c is '\\' or '*' or '?' or '[' or ']' or '$' or '%' or '`' or '{' or '}')))
            throw new ArgumentException("拒绝宽泛、非规范、含通配符或未解析变量的缓存路径。");
        if (!Cleaners.IsImportableCacheRoot("${CACHE}/" + suffix))
            throw new ArgumentException("缓存范围不在已审核白名单内，或需要不能丢弃的子树筛选策略。");
        return template + suffix;
    }

    private static bool Platform(XElement element, List<string> issues)
    {
        var os = (string?)element.Attribute("os");
        if (os is null or "linux" or "unix") return true;
        if (os is "windows" or "macos") return false;
        issues.Add($"不支持的 os：{os}");
        return false;
    }

    private static void CheckAttributes(XElement element, string[] allowed, List<string> issues)
    {
        foreach (var attribute in element.Attributes())
            if (!allowed.Contains(attribute.Name.ToString(), StringComparer.Ordinal))
                issues.Add($"不支持的 {element.Name} 属性：{attribute.Name}");
    }

    private static void CheckChildren(XElement element, string[] allowed, List<string> issues)
    {
        foreach (var child in element.Elements())
        {
            if (!allowed.Contains(child.Name.ToString(), StringComparer.Ordinal))
                issues.Add($"不支持的 {element.Name} 子元素：{child.Name}");
            if (child.Name == "label" || child.Name == "description")
            {
                CheckAttributes(child, ["translators"], issues);
                if (child.HasElements) issues.Add("label/description 不允许嵌套元素。");
            }
        }
    }

    private const string Help = """
        用法：linuxcleaner rules import FILE --source HTTPS_URL --revision REV --license SPDX
        只读取本地 CleanerML XML，向 stdout 输出待审核 JSON；不下载、不执行、不启用规则。
        必须手动审核来源、许可证、警告和动作，并修改内置规则/测试后重新构建才能启用。
        仅接受已审核缓存白名单内的 delete + walk.all/walk.files，目录本身不会删除。
        不支持的动作/条件/属性或 warning 会阻止整个 option；非 Linux 动作标记为跳过。
        退出码：0 生成待审报告；1 报告含阻止项；2 输入或参数错误；130 中断。
        """;
}
