using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinuxCleaner;

internal sealed record Cleaner(string Id, string Description, bool Default, string[] Roots);
internal sealed record CacheSelector(string? WhenFirstSegment, int Index, string[] Names);
internal sealed record RulePolicy(CacheSelector[] Include, string[] Exclude);
internal sealed record RuleDefinition(string Id, string Description, bool Default, string[] Roots, string? Policy);
internal sealed record RuleCatalog(int SchemaVersion, string Provenance,
    Dictionary<string, RulePolicy> Policies, RuleDefinition[] Rules);

internal static class Cleaners
{
    // Only this embedded, code-reviewed catalog is executable; import reports are never loaded here.
    private static readonly RuleCatalog Catalog = LoadCatalog();
    private static readonly Dictionary<string, (CacheSelector[] Include, string[][] Exclude)> Policies =
        Catalog.Rules.Where(r => r.Policy is not null).ToDictionary(r => r.Id, r =>
        {
            var policy = Catalog.Policies[r.Policy!];
            return (policy.Include, policy.Exclude.Select(p => p.Split('/')).ToArray());
        }, StringComparer.Ordinal);

    private static RuleCatalog LoadCatalog()
    {
        using var stream = typeof(Cleaners).Assembly.GetManifestResourceStream("LinuxCleaner.rules.builtin.json")
            ?? throw new InvalidOperationException("缺少内置规则包。");
        var catalog = JsonSerializer.Deserialize<RuleCatalog>(stream, new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        }) ?? throw new InvalidOperationException("规则包为空。");
        ValidateCatalog(catalog);
        return catalog;
    }

    internal static void ValidateCatalog(RuleCatalog catalog)
    {
        if (catalog.SchemaVersion != 1 || string.IsNullOrWhiteSpace(catalog.Provenance)
            || catalog.Rules is not { Length: > 0 } || catalog.Policies is null)
            throw new ArgumentException("无效规则包版本或元数据。");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var rule in catalog.Rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id)
                || !rule.Id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
                || string.IsNullOrWhiteSpace(rule.Description) || rule.Roots is not { Length: > 0 }
                || (rule.Policy is not null && !catalog.Policies.ContainsKey(rule.Policy)))
                throw new ArgumentException("无效规则 ID、根目录或策略引用。");
            foreach (var root in rule.Roots)
            {
                if (root == "${CACHE}") continue;
                var prefix = root?.StartsWith("${HOME}/", StringComparison.Ordinal) == true ? "${HOME}/"
                    : root?.StartsWith("${CACHE}/", StringComparison.Ordinal) == true ? "${CACHE}/" : null;
                if (prefix is null || !SafeSegments(root![prefix.Length..], false))
                    throw new ArgumentException("规则根目录必须使用 HOME/CACHE 模板和明确路径段。");
            }
        }
        foreach (var policy in catalog.Policies.Values)
        {
            if (policy is null || policy.Include is null || policy.Exclude is null)
                throw new ArgumentException("无效筛选策略。");
            for (var i = 0; i < policy.Include.Length; i++)
            {
                var selector = policy.Include[i];
                if (selector is null || selector.Index is < 0 or > 8 || selector.Names is not { Length: > 0 }
                    || selector.Names.Any(n => !SafeSegments(n, false) || n.Contains('/'))
                    || (selector.WhenFirstSegment is not null &&
                        (!SafeSegments(selector.WhenFirstSegment, false) || selector.WhenFirstSegment.Contains('/')))
                    || (selector.WhenFirstSegment is null && i != policy.Include.Length - 1))
                    throw new ArgumentException("无效缓存子树选择器或默认选择器顺序。");
            }
            if (policy.Exclude.Any(p => !SafeSegments(p, true)))
                throw new ArgumentException("无效排除模式。");
        }
    }

    private static bool SafeSegments(string? path, bool patterns) => !string.IsNullOrEmpty(path)
        && path.Split('/').All(p => p.Length > 0 && p is not ("." or "..")
            && !p.Any(c => char.IsControl(c) || c is '\\' or '$' or '?' or '[' or ']')
            && (patterns ? !p[..^1].Contains('*') : !p.Contains('*')));

    internal static IReadOnlyList<Cleaner> Create(string home, string? xdgCache)
    {
        home = Path.GetFullPath(home);
        var cache = string.IsNullOrEmpty(xdgCache) ? Path.Combine(home, ".cache") : xdgCache;
        if (!Path.IsPathFullyQualified(cache))
            throw new ArgumentException("XDG_CACHE_HOME 必须是绝对路径。");
        cache = Path.GetFullPath(cache);
        if (!IsWithin(cache, home))
            throw new ArgumentException("缓存目录必须位于当前用户 HOME 内，且不能是 HOME 本身。");

        string[] protectedPaths = [".ssh", ".gnupg", ".config", ".local/share", ".local/state",
            "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos"];
        foreach (var name in protectedPaths)
        {
            var path = Path.Combine(home, name);
            if (cache == path || IsWithin(cache, path) || IsWithin(path, cache))
                throw new ArgumentException($"拒绝将受保护目录用作缓存：{cache}");
        }
        return Catalog.Rules.Select(r => new Cleaner(r.Id, r.Description, r.Default,
            r.Roots.Select(root => root == "${CACHE}" ? cache
                : root.StartsWith("${HOME}/", StringComparison.Ordinal) ? Path.Combine(home, root[8..])
                : Path.Combine(cache, root[9..])).ToArray())).ToArray();
    }

    // Import only directory scopes already reviewed here; selectors cannot be dropped during conversion.
    internal static bool IsImportableCacheRoot(string root) => Catalog.Rules.Where(r => r.Policy is null)
        .SelectMany(r => r.Roots).Where(r => r.StartsWith("${CACHE}/", StringComparison.Ordinal))
        .Any(allowed => root == allowed || IsWithin(root, allowed));

    internal static bool IsWithin(string path, string parent) =>
        path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal);

    internal static bool Excluded(string cleaner, string relativePath, bool isDirectory)
    {
        if (!Policies.TryGetValue(cleaner, out var policy))
            return false;
        var parts = relativePath.Split('/');
        if (policy.Exclude.Any(pattern => MatchesPrefix(parts, pattern)))
            return true;
        if (policy.Include.Length == 0)
            return false;
        var selector = policy.Include.FirstOrDefault(s => s.WhenFirstSegment is null
            || s.WhenFirstSegment == parts[0]);
        if (selector is null)
            return true;
        if (parts.Length <= selector.Index)
            return !isDirectory;
        return !selector.Names.Contains(parts[selector.Index], StringComparer.Ordinal)
            || (parts.Length == selector.Index + 1 && !isDirectory);
    }

    private static bool MatchesPrefix(string[] parts, string[] pattern)
    {
        if (parts.Length < pattern.Length) return false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var segment = pattern[i];
            if (!(segment.EndsWith('*') ? parts[i].StartsWith(segment[..^1], StringComparison.Ordinal)
                : parts[i] == segment)) return false;
        }
        return true;
    }
}
