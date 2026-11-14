namespace LinuxCleaner;

internal sealed record Cleaner(string Id, string Description, bool Default, string[] Roots);

internal static class Cleaners
{
    private static readonly string[] FlatpakCacheNames = ["cache"];
    private static readonly string[] SnapCacheNames = [".cache"];
    private static readonly string[] ProfileCacheNames = ["Cache", "Code Cache", "GPUCache", "DawnCache",
        "GrShaderCache", "ShaderCache", "DawnGraphiteCache", "DawnWebGPUCache", "cache2", "startupCache"];
    private static readonly string[] BrowserNames = ["mozilla", "google-chrome", "google-chrome-beta", "chromium",
        "microsoft-edge", "BraveSoftware", "librewolf", "vivaldi", "vivaldi-snapshot", "opera", "opera-beta", "opera-developer"];
    private static readonly string[] EditorCacheNames = ["Cache", "CachedData", "CachedExtensionVSIXs",
        "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache"];
    private static readonly string[] WebCacheNames = ["Cache", "Code Cache", "GPUCache"];
    private static readonly string[] DiscordCacheNames = ["Code Cache", "GPUCache"];
    private static readonly string[] MailCacheNames = ["Cache", "cache2", "startupCache"];
    private static readonly string[] IdeCacheNames = ["caches", "index", "tmp"];
    private static readonly string[] SensitiveCacheNames = ["uv", "pypoetry", "JetBrains", "ccache", "spotify"];

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

        string[] steamRoots = [Path.Combine(home, ".local", "share", "Steam"), Path.Combine(home, ".steam")];
        return [
            new("cache", "其他应用缓存（排除浏览器、缩略图和含用户数据的缓存树）", true, [cache]),
            new("thumbnails", "桌面缩略图缓存", true, [Path.Combine(cache, "thumbnails")]),
            new("browsers", "Firefox / Chromium 系及 LibreWolf / Vivaldi / Opera 缓存", true,
                BrowserNames.Select(name => Path.Combine(cache, name)).ToArray()),
            new("npm", "npm 内容寻址下载缓存（不删除 node_modules）", false,
                [Path.Combine(home, ".npm", "_cacache")]),
            new("nuget", "NuGet 默认 HTTP 下载缓存（不删除已安装包）", false,
                [Path.Combine(home, ".local", "share", "NuGet", "http-cache")]),
            new("pip", "pip 下载和 wheel 缓存", false, [Path.Combine(cache, "pip")]),
            new("yarn", "Yarn 用户级下载缓存（不处理项目内 .yarn）", false,
                [Path.Combine(cache, "yarn"), Path.Combine(home, ".yarn", "berry", "cache")]),
            new("go", "Go 编译缓存（不删除模块或源代码）", false, [Path.Combine(cache, "go-build")]),
            new("cargo", "Cargo registry 下载归档（不删除源码或工具链）", false,
                [Path.Combine(home, ".cargo", "registry", "cache")]),
            new("gradle", "Gradle 构建缓存（不删除 wrapper 或项目）", false,
                [Path.Combine(home, ".gradle", "caches")]),
            new("npm-logs", "npm 历史调试日志", false, [Path.Combine(home, ".npm", "_logs")]),
            new("browser-profiles", "浏览器配置内的缓存子目录（保留账号和历史）", false,
                [Path.Combine(home, ".mozilla", "firefox"), Path.Combine(home, ".librewolf"),
                 .. new[] { "google-chrome", "google-chrome-beta", "chromium", "microsoft-edge",
                     "BraveSoftware/Brave-Browser", "vivaldi", "vivaldi-snapshot" }
                     .Select(name => Path.Combine(home, ".config", name))]),
            new("flatpak", "Flatpak 每个应用的 cache（不处理 data/config）", false,
                [Path.Combine(home, ".var", "app")]),
            new("snap", "Snap 各版本的 .cache（不处理应用数据或 current 链接）", false,
                [Path.Combine(home, "snap")]),
            new("composer", "Composer 下载缓存（保留 auth.json 和 vendor）", false,
                [Path.Combine(cache, "composer"), .. new[] { "cache", "files", "repo", "vcs" }
                    .Select(name => Path.Combine(home, ".composer", name))]),
            new("poetry", "Poetry 下载缓存和归档（保留 virtualenvs）", false,
                [Path.Combine(cache, "pypoetry", "cache"), Path.Combine(cache, "pypoetry", "artifacts")]),
            new("ccache", "ccache 编译对象缓存（保留 ccache.conf）", false,
                [Path.Combine(cache, "ccache"), Path.Combine(home, ".ccache")]),
            new("sccache", "sccache 编译对象缓存", false, [Path.Combine(cache, "sccache")]),
            new("vscode", "VS Code / Cursor / VSCodium 等编辑器缓存（保留设置、扩展和历史）", false,
                new[] { "Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf", "Windsurf - Next",
                    "Antigravity", "Devin", "Devin - Next" }.Select(name => Path.Combine(home, ".config", name)).ToArray()),
            new("discord", "Discord JS/GPU 缓存（保留 HTTP 缓存、收藏 GIF 和登录数据）", false,
                new[] { "discord", "discordptb", "discordcanary", "discorddevelopment" }
                    .Select(name => Path.Combine(home, ".config", name)).ToArray()),
            new("slack", "Slack Web/JS/GPU 缓存（保留会话和工作区数据）", false,
                [Path.Combine(home, ".config", "Slack")]),
            new("thunderbird", "Thunderbird 独立缓存（不删除邮件）", false,
                [Path.Combine(cache, "thunderbird")]),
            new("thunderbird-profiles", "Thunderbird profile 内的缓存（保留邮件、账号和通讯录）", false,
                [Path.Combine(home, ".thunderbird"), Path.Combine(home, ".mozilla-thunderbird"),
                 Path.Combine(home, ".config", "thunderbird"), Path.Combine(home, ".config", "mozilla", "thunderbird")]),
            new("steam-shaders", "Steam 着色器缓存（保留游戏、存档及 Proton 前缀）", false,
                steamRoots.Select(root => Path.Combine(root, "steamapps", "shadercache")).ToArray()),
            new("steam-cache", "Steam HTTP 和指定 Chromium 缓存（保留账号、Cookie 和用户数据）", false,
                steamRoots.SelectMany(root => new[] { "appcache/httpcache", "config/htmlcache/Cache",
                    "config/htmlcache/Code Cache", "config/htmlcache/GPUCache", "config/htmlcache/Default/Cache",
                    "config/htmlcache/Default/Code Cache", "config/htmlcache/Default/GPUCache",
                    "htmlcache/Cache", "htmlcache/Code Cache", "htmlcache/GPUCache" }
                    .Select(name => Path.Combine(root, name))).ToArray()),
            new("mesa-shaders", "Mesa 着色器缓存（下次运行需重新编译）", false,
                new[] { "mesa_shader_cache", "mesa_shader_cache_db", "mesa_shader_cache_sf" }
                    .Select(name => Path.Combine(cache, name)).ToArray()),
            new("gstreamer", "GStreamer 插件注册缓存（不删除插件）", false,
                [Path.Combine(cache, "gstreamer-1.0")]),
            new("fontconfig", "Fontconfig 字体缓存（不删除字体和配置）", false,
                [Path.Combine(cache, "fontconfig")]),
            new("winetricks", "Winetricks 下载缓存（不删除 Wine 前缀）", false,
                [Path.Combine(cache, "winetricks"), Path.Combine(home, ".winetrickscache")]),
            new("jetbrains", "JetBrains 索引/缓存/tmp（保留本地历史和配置）", false,
                [Path.Combine(cache, "JetBrains")]),
            new("opera", "Opera 配置内的指定缓存（保留历史、密码和会话）", false,
                new[] { "opera", "opera-beta", "opera-developer" }.Select(name => Path.Combine(home, ".config", name)).ToArray())
        ];
    }

    internal static bool IsWithin(string path, string parent) =>
        path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal);

    internal static bool Excluded(string cleaner, string relativePath, bool isDirectory)
    {
        if (cleaner is not ("cache" or "flatpak" or "snap" or "browser-profiles" or "vscode"
            or "discord" or "slack" or "thunderbird-profiles" or "jetbrains" or "ccache" or "opera"))
            return false;
        var parts = relativePath.Split('/');
        return cleaner switch
        {
            "cache" => parts[0] == "thumbnails" || BrowserNames.Contains(parts[0], StringComparer.Ordinal)
                || SensitiveCacheSubtree(parts, 0),
            "flatpak" => !InCacheSubtree(parts, 1, FlatpakCacheNames, isDirectory) || SensitiveCacheSubtree(parts, 2),
            "snap" => !InCacheSubtree(parts, 2, SnapCacheNames, isDirectory) || SensitiveCacheSubtree(parts, 3),
            "browser-profiles" => !InCacheSubtree(parts, 1, ProfileCacheNames, isDirectory),
            "vscode" => !InCacheSubtree(parts, 0, EditorCacheNames, isDirectory),
            "discord" => !InCacheSubtree(parts, 0, DiscordCacheNames, isDirectory),
            "slack" => !InCacheSubtree(parts, 0, WebCacheNames, isDirectory),
            "thunderbird-profiles" => !InCacheSubtree(parts, parts[0] == "Profiles" ? 2 : 1, MailCacheNames, isDirectory),
            "jetbrains" => !InCacheSubtree(parts, 1, IdeCacheNames, isDirectory),
            "ccache" => !((parts[0] == "tmp" || (parts[0].Length == 1 && "0123456789abcdef".Contains(parts[0][0])))
                && (parts.Length > 1 || isDirectory)),
            "opera" => !InCacheSubtree(parts, parts[0] == "Default" ? 1 : 0, ProfileCacheNames, isDirectory),
            _ => false
        };
    }

    // These nominal cache roots contain environments, history, configuration or offline media.
    private static bool SensitiveCacheSubtree(string[] parts, int index) => parts.Length > index
        && (SensitiveCacheNames.Contains(parts[index], StringComparer.Ordinal)
            || (parts[index] == "Google" && parts.Length > index + 1
                && parts[index + 1].StartsWith("AndroidStudio", StringComparison.Ordinal)));

    private static bool InCacheSubtree(string[] parts, int cacheIndex, string[] names, bool isDirectory)
    {
        if (parts.Length <= cacheIndex)
            return isDirectory;
        return names.Contains(parts[cacheIndex], StringComparer.Ordinal)
            && (parts.Length > cacheIndex + 1 || isDirectory);
    }
}
