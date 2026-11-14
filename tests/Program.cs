using System.Diagnostics;
using System.Text.Json;
using LinuxCleaner;

if (!OperatingSystem.IsLinux() || NativeDirectory.UserId == 0)
{
    Console.Error.WriteLine("安全测试必须在 Linux 上以普通用户运行。");
    return 2;
}

(int Count, int Failed) result = (0, 0);
Test("参数默认值、帮助与版本", () =>
{
    Equal("help", Options.Parse([]).Command);
    Equal("help", Options.Parse(["--help"]).Command);
    Equal("version", Options.Parse(["--version"]).Command);
    Equal(7, Options.Parse(["scan"]).OlderThanDays);
    Equal(0, Options.Parse(["clean", "npm", "--older-than", "0", "--yes"]).OlderThanDays);
});
Test("错误参数不能触发清理", () =>
{
    foreach (var args in new string[][] {
        ["oops"], ["scan", "--yes"], ["scan", "--dry-run"], ["clean", "--older-than"],
        ["scan", "--older-than", "-1"], ["scan", "--older-than", "36501"],
        ["scan", "--older-than", "abc"], ["clean", "--yess"], ["list", "npm"],
        ["list", "--verbose"], ["scan", "npm", "npm"], ["scan", "--json", "--json"],
        ["scan", "--all", "npm"], ["list", "--all"] })
        Throws<ArgumentException>(() => Options.Parse(args));
});
Test("目录打开标志按 CPU 架构选择，未知架构拒绝操作", () =>
{
    Equal(0xb0000, NativeDirectory.DirectoryFlags(System.Runtime.InteropServices.Architecture.X64));
    Equal(0x8c000, NativeDirectory.DirectoryFlags(System.Runtime.InteropServices.Architecture.Arm64));
    Throws<PlatformNotSupportedException>(() =>
        NativeDirectory.DirectoryFlags(System.Runtime.InteropServices.Architecture.X86));
});
Test("XDG 缓存路径限制", () =>
{
    using var f = new Fixture();
    foreach (var cache in new[] { f.Home, "/", "/tmp", "relative",
        Path.Combine(f.Home, ".ssh"), Path.Combine(f.Home, ".local"),
        Path.Combine(f.Home, "Documents", "work") })
        Throws<ArgumentException>(() => Cleaners.Create(f.Home, cache));
    Equal(Path.Combine(f.Home, "custom-cache"), Cleaners.Create(f.Home,
        Path.Combine(f.Home, "custom-cache"))[0].Roots.Single());
});
Test("扫描只读、年龄筛选、默认排除可选缓存", () =>
{
    using var f = new Fixture();
    var old = f.File(".cache/app/old", 10);
    var fresh = f.File(".cache/app/fresh", 0);
    var future = f.File(".cache/app/future", -10);
    f.File(".npm/_cacache/package", 10);
    var plan = f.Scan();
    Equal(1L, plan.FileCount);
    Equal((ulong)new FileInfo(old).Length, plan.Bytes);
    True(System.IO.File.Exists(old) && System.IO.File.Exists(fresh) && System.IO.File.Exists(future));
    Equal(0, plan.WarningCount);
});
Test("默认清理规则不重叠", () =>
{
    using var f = new Fixture();
    f.File(".cache/app/a", 10);
    f.File(".cache/thumbnails/normal/b", 10);
    f.File(".cache/mozilla/firefox/profile/cache2/c", 10);
    f.File(".cache/BraveSoftware/Brave-Browser/d", 10);
    var plan = f.Scan();
    Equal(4L, plan.FileCount);
    Equal(4, plan.Roots.SelectMany(r => r.Files.Select(file =>
        Path.Combine(r.Path, file.RelativePath))).Distinct().Count());
    Equal(1L, f.Scan("cache").FileCount);
    Equal(2L, f.Scan("browsers").FileCount);
});
Test("自定义缓存与可选规则重叠时只计数一次", () =>
{
    using var f = new Fixture();
    f.File(".npm/_cacache/package", 20);
    var cleaners = Cleaners.Create(f.Home, Path.Combine(f.Home, ".npm"));
    var plan = CleaningEngine.Scan(cleaners.Where(c => c.Id is "cache" or "npm"),
        DateTimeOffset.UtcNow.AddDays(-7));
    Equal(1L, plan.FileCount);
    var clean = CleaningEngine.Clean(plan);
    Equal(1L, clean.DeletedFiles);
    Equal(0L, clean.ChangedOrMissing);
    Equal(0, clean.ErrorCount);
});
Test("零天年龄仍排除未来文件且选择刚创建的文件", () =>
{
    using var f = new Fixture();
    f.File(".cache/current", 0);
    f.File(".cache/future", -1);
    var plan = CleaningEngine.Scan(f.Cleaners.Where(c => c.Default), DateTimeOffset.UtcNow);
    Equal(1L, plan.FileCount);
});
Test("挂载点和身份校验的元数据判断", () =>
{
    var first = new EntryStat { Mask = 0x1000, DeviceMajor = 8, DeviceMinor = 1,
        MountId = 10, Inode = 50, Uid = 1000, Links = 1, Mode = 0x8000, Size = 5 };
    var second = first;
    True(first.SameIdentity(second) && first.SameMount(second) && first.Unchanged(second));
    second.MountId++;
    True(!first.SameMount(second));
    second = first;
    second.DeviceMinor++;
    True(!first.SameIdentity(second) && !first.SameMount(second));
    second = first;
    second.Uid++;
    True(!first.Unchanged(second));
    second = first;
    second.ChangeNanos++;
    True(!first.Unchanged(second));
});
Test("只删除已预览普通文件，保留目录和个人数据", () =>
{
    using var f = new Fixture();
    var old = f.File(".cache/app/old", 10);
    var fresh = f.File(".cache/app/fresh", 0);
    var document = f.File("Documents/Cache/important.txt", 20);
    var profile = f.File(".mozilla/firefox/profile/logins.json", 20);
    var plan = f.Scan();
    var added = f.File(".cache/app/added-after-preview", 20);
    var clean = CleaningEngine.Clean(plan);
    Equal(1L, clean.DeletedFiles);
    Equal(0, clean.ErrorCount);
    True(!System.IO.File.Exists(old));
    True(System.IO.File.Exists(fresh) && System.IO.File.Exists(document) &&
        System.IO.File.Exists(profile) && System.IO.File.Exists(added));
    True(Directory.Exists(Path.GetDirectoryName(old)));
    Equal(1L, CleaningEngine.Clean(f.Scan()).DeletedFiles); // The newly scanned old file is now eligible.
    Equal(0L, CleaningEngine.Clean(f.Scan()).DeletedFiles);
});
Test("符号链接文件、目录及悬空链接均不跟随", () =>
{
    using var f = new Fixture();
    var outside = f.File("Documents/important", 20);
    var cache = Path.Combine(f.Home, ".cache");
    Directory.CreateDirectory(cache);
    System.IO.File.CreateSymbolicLink(Path.Combine(cache, "link-file"), outside);
    Directory.CreateSymbolicLink(Path.Combine(cache, "link-dir"), Path.GetDirectoryName(outside)!);
    System.IO.File.CreateSymbolicLink(Path.Combine(cache, "dangling"), Path.Combine(f.Home, "missing"));
    var plan = f.Scan();
    Equal(0L, plan.FileCount);
    Equal(3, plan.Skipped);
    Equal(0L, CleaningEngine.Clean(plan).DeletedFiles);
    True(System.IO.File.Exists(outside));
});
Test("缓存根目录及祖先符号链接被拒绝", () =>
{
    using var f = new Fixture();
    var outside = f.File("Documents/important", 20);
    Directory.CreateSymbolicLink(Path.Combine(f.Home, ".cache"), Path.GetDirectoryName(outside)!);
    var plan = f.Scan("cache");
    Equal(0L, plan.FileCount);
    Equal(1, plan.WarningCount);
    Directory.CreateSymbolicLink(Path.Combine(f.Home, ".npm"), Path.GetDirectoryName(outside)!);
    var ancestor = f.Scan("npm");
    Equal(0L, ancestor.FileCount);
    Equal(1, ancestor.WarningCount);
    True(System.IO.File.Exists(outside));
});
Test("硬链接与 FIFO 被跳过", () =>
{
    using var f = new Fixture();
    var outside = f.File("Documents/original", 20);
    var link = Path.Combine(f.Home, ".cache", "hardlink");
    Directory.CreateDirectory(Path.GetDirectoryName(link)!);
    Equal(0, RunProcess("ln", [outside, link]).Code);
    var fifo = Path.Combine(f.Home, ".cache", "pipe");
    Equal(0, RunProcess("mkfifo", [fifo]).Code);
    var plan = f.Scan("cache");
    Equal(0L, plan.FileCount);
    Equal(2, plan.Skipped);
    True(System.IO.File.Exists(outside));
});
Test("预览后修改的文件不会删除", () =>
{
    using var f = new Fixture();
    var path = f.File(".cache/app/old", 20);
    var plan = f.Scan();
    System.IO.File.AppendAllText(path, "changed");
    var clean = CleaningEngine.Clean(plan);
    Equal(0L, clean.DeletedFiles);
    Equal(1L, clean.ChangedOrMissing);
    True(System.IO.File.Exists(path));
});
Test("预览后替换成符号链接的文件不会删除", () =>
{
    using var f = new Fixture();
    var path = f.File(".cache/app/old", 20);
    var outside = f.File("Documents/important", 20);
    var plan = f.Scan();
    System.IO.File.Delete(path);
    System.IO.File.CreateSymbolicLink(path, outside);
    var clean = CleaningEngine.Clean(plan);
    Equal(0L, clean.DeletedFiles);
    Equal(1L, clean.ChangedOrMissing);
    True(System.IO.File.Exists(outside));
});
Test("预览后替换父目录不能逃逸", () =>
{
    using var f = new Fixture();
    f.File(".cache/app/old", 20);
    var outside = f.File("Documents/old", 20);
    var plan = f.Scan();
    var app = Path.Combine(f.Home, ".cache", "app");
    Directory.Move(app, app + "-moved");
    Directory.CreateSymbolicLink(app, Path.Combine(f.Home, "Documents"));
    var clean = CleaningEngine.Clean(plan);
    Equal(0L, clean.DeletedFiles);
    Equal(1, clean.ErrorCount);
    True(System.IO.File.Exists(outside));
    True(System.IO.File.Exists(Path.Combine(app + "-moved", "old")));
});
Test("预览后替换根目录不能清理新目录", () =>
{
    using var f = new Fixture();
    f.File(".cache/app/old", 20);
    var plan = f.Scan("cache");
    var cache = Path.Combine(f.Home, ".cache");
    Directory.Move(cache, cache + "-moved");
    var replacement = f.File(".cache/app/old", 20);
    var clean = CleaningEngine.Clean(plan);
    Equal(0L, clean.DeletedFiles);
    Equal(1, clean.ErrorCount);
    True(System.IO.File.Exists(replacement));
});
Test("预览后消失的文件正常跳过", () =>
{
    using var f = new Fixture();
    var path = f.File(".cache/app/old", 20);
    var plan = f.Scan();
    System.IO.File.Delete(path);
    var clean = CleaningEngine.Clean(plan);
    Equal(0, clean.ErrorCount);
    Equal(1L, clean.ChangedOrMissing);
});
Test("无权限目录报告警告而非静默成功", () =>
{
    using var f = new Fixture();
    f.File(".cache/private/old", 20);
    var directory = Path.Combine(f.Home, ".cache", "private");
    System.IO.File.SetUnixFileMode(directory, UnixFileMode.None);
    try
    {
        var plan = f.Scan();
        Equal(1, plan.WarningCount);
        Equal(0L, plan.FileCount);
    }
    finally
    {
        System.IO.File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
    }
});
Test("CLI 扫描警告返回部分失败退出码", () =>
{
    using var f = new Fixture();
    var outside = f.File("Documents/important", 20);
    Directory.CreateSymbolicLink(Path.Combine(f.Home, ".cache"), Path.GetDirectoryName(outside)!);
    var scan = RunCli(f, ["scan", "cache", "--json"]);
    Equal(1, scan.Code);
    using var doc = JsonDocument.Parse(scan.Output);
    Equal(1, doc.RootElement.GetProperty("WarningCount").GetInt32());
    True(System.IO.File.Exists(outside));
});
Test("CLI JSON 空结果与清理规则列表", () =>
{
    using var f = new Fixture();
    var list = RunCli(f, ["list", "--json"]);
    Equal(0, list.Code);
    using var listDoc = JsonDocument.Parse(list.Output);
    Equal(f.Cleaners.Count, listDoc.RootElement.GetArrayLength());
    var clean = RunCli(f, ["clean", "--yes", "--json"]);
    Equal(0, clean.Code);
    using var doc = JsonDocument.Parse(clean.Output);
    Equal(0L, doc.RootElement.GetProperty("CandidateFiles").GetInt64());
    Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Result").ValueKind);
});
Test("可选 npm / NuGet 缓存不触及已安装包", () =>
{
    using var f = new Fixture();
    var npm = f.File(".npm/_cacache/content-v2/a", 20);
    var nuget = f.File(".local/share/NuGet/http-cache/b", 20);
    var installed = f.File(".nuget/packages/library/lib.dll", 20);
    var modules = f.File("project/node_modules/library/index.js", 20);
    var clean = CleaningEngine.Clean(f.Scan("npm", "nuget"));
    Equal(2L, clean.DeletedFiles);
    True(!System.IO.File.Exists(npm) && !System.IO.File.Exists(nuget));
    True(System.IO.File.Exists(installed) && System.IO.File.Exists(modules));
});
Test("新增开发缓存规则仅清理缓存，不触及源码和工具链", () =>
{
    using var f = new Fixture();
    string[] caches = [".cache/pip/http-v2/a", ".cache/yarn/v6/a", ".yarn/berry/cache/a.zip",
        ".cache/go-build/ab/a", ".cargo/registry/cache/index/a.crate", ".gradle/caches/build/a",
        ".npm/_logs/old.log"];
    foreach (var path in caches) f.File(path, 20);
    string[] protectedFiles = [".cargo/registry/src/index/pkg/src.rs", ".cargo/bin/tool",
        ".rustup/toolchains/stable/rustc", "go/pkg/mod/module/file.go",
        "project/.yarn/cache/pkg.zip", ".gradle/wrapper/gradle-wrapper.jar"];
    foreach (var path in protectedFiles) f.File(path, 20);
    var clean = CleaningEngine.Clean(f.Scan("pip", "yarn", "go", "cargo", "gradle", "npm-logs"));
    Equal((long)caches.Length, clean.DeletedFiles);
    Equal(0, clean.ErrorCount);
    foreach (var path in protectedFiles) True(System.IO.File.Exists(Path.Combine(f.Home, path)));
});
Test("Flatpak 和 Snap 只遍历明确的缓存子树", () =>
{
    using var f = new Fixture();
    f.File(".var/app/org.example.App/cache/old", 20);
    f.File("snap/app/123/.cache/old", 20);
    f.File("snap/app/common/.cache/old", 20);
    string[] personal = [".var/app/org.example.App/data/Cache/important",
        ".var/app/org.example.App/config/settings", ".var/app/root-file",
        "snap/app/123/Documents/important", "snap/app/common/data/Cache/important",
        "snap/app/revision-file"];
    foreach (var path in personal) f.File(path, 20);
    Directory.CreateSymbolicLink(Path.Combine(f.Home, "snap/app/current"),
        Path.Combine(f.Home, "snap/app/123"));
    var clean = CleaningEngine.Clean(f.Scan("flatpak", "snap"));
    Equal(3L, clean.DeletedFiles);
    Equal(0, clean.ErrorCount);
    foreach (var path in personal) True(System.IO.File.Exists(Path.Combine(f.Home, path)));
});
Test("沙箱中的同名普通文件和逃逸链接不会当作缓存", () =>
{
    using var f = new Fixture();
    var outside = f.File("Documents/important", 20);
    f.File(".var/app/app/cache", 20);
    f.File("snap/app/123/.cache", 20);
    Directory.CreateDirectory(Path.Combine(f.Home, ".var/app/linked"));
    Directory.CreateSymbolicLink(Path.Combine(f.Home, ".var/app/linked/cache"),
        Path.Combine(f.Home, "Documents"));
    Directory.CreateDirectory(Path.Combine(f.Home, "snap/linked/common"));
    Directory.CreateSymbolicLink(Path.Combine(f.Home, "snap/linked/common/.cache"),
        Path.Combine(f.Home, "Documents"));
    Equal(0L, f.Scan("flatpak", "snap").FileCount);
    True(System.IO.File.Exists(outside));
});
Test("浏览器配置内仅清理明确的缓存，不触及登录和离线网站数据", () =>
{
    using var f = new Fixture();
    string[] caches = [".config/chromium/Default/Cache/Cache_Data/old",
        ".config/google-chrome/Profile 1/Code Cache/js/old",
        ".config/BraveSoftware/Brave-Browser/Default/GPUCache/old",
        ".mozilla/firefox/abc.default/cache2/entries/old"];
    foreach (var path in caches) f.File(path, 20);
    string[] personal = [".config/chromium/Default/History", ".config/chromium/Default/Login Data",
        ".config/chromium/Default/Cookies", ".config/chromium/Default/Service Worker/CacheStorage/db",
        ".mozilla/firefox/abc.default/logins.json", ".mozilla/firefox/abc.default/places.sqlite",
        ".config/chromium/Local State"];
    foreach (var path in personal) f.File(path, 20);
    var clean = CleaningEngine.Clean(f.Scan("browser-profiles"));
    Equal((long)caches.Length, clean.DeletedFiles);
    Equal(0, clean.ErrorCount);
    foreach (var path in personal) True(System.IO.File.Exists(Path.Combine(f.Home, path)));
});
Test("--all 选择可选规则但不会重复计数或隐含系统清理", () =>
{
    using var f = new Fixture();
    f.File(".cache/pip/http/old", 20);
    f.File(".cargo/registry/cache/index/pkg.crate", 20);
    f.File(".var/app/app/cache/old", 20);
    f.File(".config/chromium/Default/History", 20);
    Equal(1L, f.Scan().FileCount);
    var scan = RunCli(f, ["scan", "--all", "--json"]);
    Equal(0, scan.Code);
    using var doc = JsonDocument.Parse(scan.Output);
    Equal(3L, doc.RootElement.GetProperty("CandidateFiles").GetInt64());
    Equal(f.Cleaners.Count, doc.RootElement.GetProperty("SelectedCleaners").GetArrayLength());
    var clean = RunCli(f, ["clean", "--all", "--yes", "--json"]);
    Equal(0, clean.Code);
    using var cleanDoc = JsonDocument.Parse(clean.Output);
    Equal(3L, cleanDoc.RootElement.GetProperty("Result").GetProperty("DeletedFiles").GetInt64());
    Equal(0L, cleanDoc.RootElement.GetProperty("Result").GetProperty("ChangedOrMissing").GetInt64());
    True(System.IO.File.Exists(Path.Combine(f.Home, ".config/chromium/Default/History")));
});
Test("终端输出转义控制符", () =>
{
    Equal("a\\u001b[31m\\u000ab\\u202ec", Cli.Display("a\u001b[31m\nb\u202ec"));
});
Test("取消令牌立即中断扫描和清理", () =>
{
    using var f = new Fixture();
    var path = f.File(".cache/app/old", 20);
    var plan = f.Scan();
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    Throws<OperationCanceledException>(() => CleaningEngine.Scan(f.Cleaners, DateTimeOffset.UtcNow, cts.Token));
    Throws<OperationCanceledException>(() => CleaningEngine.Clean(plan, cts.Token));
    True(System.IO.File.Exists(path));
});
Test("CLI JSON 预览与 dry-run 均不写入", () =>
{
    using var f = new Fixture();
    var path = f.File(".cache/app/old", 20);
    var scan = RunCli(f, ["scan", "--json", "--verbose"]);
    Equal(0, scan.Code);
    using var doc = JsonDocument.Parse(scan.Output);
    Equal(1L, doc.RootElement.GetProperty("CandidateFiles").GetInt64());
    True(doc.RootElement.GetProperty("DryRun").GetBoolean());
    True(doc.RootElement.GetProperty("Files")[0].GetProperty("Path").GetString()!.StartsWith(f.Home + "/"));
    var dry = RunCli(f, ["clean", "--dry-run", "--json"]);
    Equal(0, dry.Code);
    True(System.IO.File.Exists(path));
    var invalid = RunCli(f, ["clean", "typo", "--yes"]);
    Equal(2, invalid.Code);
    True(System.IO.File.Exists(path));
});
Test("CLI 非交互清理必须显式确认", () =>
{
    using var f = new Fixture();
    var path = f.File(".cache/app/old", 20);
    Equal(2, RunCli(f, ["clean"]).Code);
    Equal(2, RunCli(f, ["clean", "--json"]).Code);
    True(System.IO.File.Exists(path));
    var clean = RunCli(f, ["clean", "--yes", "--json"]);
    Equal(0, clean.Code);
    using var doc = JsonDocument.Parse(clean.Output);
    Equal(1L, doc.RootElement.GetProperty("Result").GetProperty("DeletedFiles").GetInt64());
    True(!System.IO.File.Exists(path));
});
Test("交互确认必须精确输入 DELETE", () =>
{
    using var f = new Fixture();
    var path = f.File(".cache/app/old", 20);
    var previousHome = Environment.GetEnvironmentVariable("HOME");
    var previousCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
    try
    {
        Environment.SetEnvironmentVariable("HOME", f.Home);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(f.Home, ".cache"));
        // Check before invoking clean so a runtime that caches HOME cannot target real user files.
        Equal(f.Home, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var output = new StringWriter();
        Equal(0, Cli.Run(["clean"], new StringReader("yes\n"), output, new StringWriter(), false));
        True(System.IO.File.Exists(path));
        Equal(0, Cli.Run(["clean"], new StringReader("DELETE\n"), output, new StringWriter(), false));
        True(!System.IO.File.Exists(path));
    }
    finally
    {
        Environment.SetEnvironmentVariable("HOME", previousHome);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previousCache);
    }
});

Test("规则目录始终在 HOME 内，ID 唯一", () =>
{
    using var f = new Fixture();
    Equal(31, f.Cleaners.Count);
    Equal(f.Cleaners.Count, f.Cleaners.Select(c => c.Id).Distinct().Count());
    foreach (var root in f.Cleaners.SelectMany(c => c.Roots)) True(Cleaners.IsWithin(root, f.Home));
});
foreach (var sample in NewRuleSamples())
{
    Test($"新增规则 {sample.Id} 只删除缓存，保留相邻数据", () =>
    {
        using var f = new Fixture();
        var cached = f.File(sample.Cache, 20);
        var preserved = f.File(sample.Preserved, 20);
        var plan = f.Scan(sample.Id);
        Equal(1L, plan.FileCount);
        Equal(0, plan.WarningCount);
        var clean = CleaningEngine.Clean(plan);
        Equal(1L, clean.DeletedFiles);
        Equal(0, clean.ErrorCount);
        True(!System.IO.File.Exists(cached));
        True(System.IO.File.Exists(preserved));
    });
}
Test("所有规则共同运行不会重复计数或删除关键数据", () =>
{
    using var f = new Fixture();
    var samples = NewRuleSamples();
    foreach (var sample in samples) { f.File(sample.Cache, 20); f.File(sample.Preserved, 20); }
    var plan = CleaningEngine.Scan(f.Cleaners, DateTimeOffset.UtcNow.AddDays(-7));
    Equal((long)samples.Length, plan.FileCount);
    Equal(0, plan.WarningCount);
    var clean = CleaningEngine.Clean(plan);
    Equal((long)samples.Length, clean.DeletedFiles);
    Equal(0L, clean.ChangedOrMissing);
    Equal(0, clean.ErrorCount);
    foreach (var sample in samples) True(System.IO.File.Exists(Path.Combine(f.Home, sample.Preserved)));
});
Test("默认缓存规则保留托管环境、本地历史、配置和离线媒体", () =>
{
    using var f = new Fixture();
    string[] preserved = [".cache/pypoetry/virtualenvs/env/bin/python", ".cache/pypoetry/artifacts/pkg.whl",
        ".cache/JetBrains/Idea2026.1/LocalHistory/history", ".cache/JetBrains/Idea2026.1/caches/index",
        ".cache/Google/AndroidStudio2026.1/LocalHistory/history", ".cache/ccache/ccache.conf",
        ".cache/uv/archive-v0/dependency/file.py", ".cache/spotify/Storage/offline-song"];
    foreach (var path in preserved) f.File(path, 20);
    f.File(".cache/ordinary/cache", 20);
    var clean = CleaningEngine.Clean(f.Scan());
    Equal(1L, clean.DeletedFiles);
    foreach (var path in preserved) True(System.IO.File.Exists(Path.Combine(f.Home, path)));
});
Test("Flatpak 和 Snap 缓存也保护环境、历史及离线媒体", () =>
{
    using var f = new Fixture();
    var paths = new List<string>();
    foreach (var root in new[] { ".var/app/app/cache", "snap/app/common/.cache" })
    {
        foreach (var suffix in new[] { "pypoetry/virtualenvs/env/bin/python", "JetBrains/Idea/LocalHistory/history",
            "uv/archive-v0/pkg/file", "spotify/Storage/offline", "ccache/ccache.conf",
            "Google/AndroidStudio2026.1/LocalHistory/history" })
            paths.Add(f.File(root + "/" + suffix, 20));
        f.File(root + "/ordinary/file", 20);
    }
    Equal(2L, CleaningEngine.Clean(f.Scan("flatpak", "snap")).DeletedFiles);
    foreach (var path in paths) True(System.IO.File.Exists(path));
});
Test("编辑器分支不清理设置、扩展、本地历史、备份或工作区状态", () =>
{
    using var f = new Fixture();
    var paths = new List<string>();
    string[] editors = ["Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf", "Windsurf - Next",
        "Antigravity", "Devin", "Devin - Next"];
    foreach (var editor in editors)
    {
        f.File(".config/" + editor + "/DawnWebGPUCache/gpu", 20);
        foreach (var suffix in new[] { "User/settings.json", "User/History/file/old", "User/globalStorage/state.vscdb",
            "User/workspaceStorage/id/state.vscdb", "Backups/unsaved" })
            paths.Add(f.File(".config/" + editor + "/" + suffix, 20));
    }
    paths.Add(f.File(".vscode/extensions/extension/installed", 20));
    Equal((long)editors.Length, CleaningEngine.Clean(f.Scan("vscode")).DeletedFiles);
    foreach (var path in paths) True(System.IO.File.Exists(path));
});
Test("扩展浏览器规则覆盖 Vivaldi、Opera 和 LibreWolf 且保留会话", () =>
{
    using var f = new Fixture();
    string[] caches = [".cache/librewolf/a", ".cache/vivaldi/a", ".cache/opera/a",
        ".config/vivaldi/Default/Code Cache/a", ".librewolf/profile/cache2/entries/a",
        ".config/opera/GPUCache/a", ".config/opera/Default/DawnGraphiteCache/a"];
    foreach (var path in caches) f.File(path, 20);
    string[] preserved = [".librewolf/profile/logins.json", ".config/vivaldi/Default/Sessions/tab",
        ".config/opera/Cookies", ".config/opera/Default/Sessions/tab"];
    foreach (var path in preserved) f.File(path, 20);
    var plan = f.Scan("browsers", "browser-profiles", "opera");
    Equal((long)caches.Length, plan.FileCount);
    Equal((long)caches.Length, CleaningEngine.Clean(plan).DeletedFiles);
    foreach (var path in preserved) True(System.IO.File.Exists(Path.Combine(f.Home, path)));
});
Test("ccache 只清理十六进制分桶和 tmp，配置及未知根目录保留", () =>
{
    using var f = new Fixture();
    f.File(".cache/ccache/a/objR", 20);
    f.File(".ccache/f/objM", 20);
    f.File(".ccache/tmp/old", 20);
    string[] preserved = [".cache/ccache/ccache.conf", ".ccache/ccache.conf", ".ccache/stats",
        ".ccache/z/unknown", ".ccache/b"];
    foreach (var path in preserved) f.File(path, 20);
    Equal(3L, CleaningEngine.Clean(f.Scan("ccache")).DeletedFiles);
    foreach (var path in preserved) True(System.IO.File.Exists(Path.Combine(f.Home, path)));
});
Test("大小单位解析支持十进制与二进制，拒绝溢出和歧义格式", () =>
{
    Equal(0UL, ScanFilters.ParseBytes("0"));
    Equal(1000UL, ScanFilters.ParseBytes("1KB"));
    Equal(1024UL, ScanFilters.ParseBytes("1kib"));
    Equal(2UL * 1024 * 1024, ScanFilters.ParseBytes("2MiB"));
    Equal(1000UL * 1000 * 1000, ScanFilters.ParseBytes("1GB"));
    Equal(1UL << 40, ScanFilters.ParseBytes("1TiB"));
    Equal(ulong.MaxValue, ScanFilters.ParseBytes("18446744073709551615B"));
    foreach (var invalid in new[] { "", "-1", "+1", "1.5MiB", "1M", "1 MB", " 1", "1PB",
        "18446744073709551616", "18446744073709551615KiB" })
        Throws<ArgumentException>(() => ScanFilters.ParseBytes(invalid));
});
Test("筛选参数严格校验，重复排除路径可以接受", () =>
{
    foreach (var arguments in new string[][] {
        ["scan", "--exclude"], ["scan", "--exclude", "--json"], ["scan", "--exclude", ""],
        ["scan", "--min-size"], ["scan", "--min-size", "bad"],
        ["scan", "--min-size", "1", "--min-size", "2"], ["list", "--exclude", ".cache/pip"],
        ["list", "--min-size", "10"], ["scan", "--top", "0"], ["scan", "--top", "1001"],
        ["scan", "--top"], ["clean", "--yes", "--top", "1"] })
        Throws<ArgumentException>(() => Options.Parse(arguments));
    var parsed = Options.Parse(["scan", "--exclude", ".cache/pip", "--exclude", ".cache/yarn", "--min-size", "1MiB"]);
    Equal(2, parsed.Exclusions!.Length);
    Equal(1UL << 20, parsed.MinimumBytes);
});
Test("排除路径相对于 HOME，支持 ~/、规范化和去重", () =>
{
    using var f = new Fixture();
    var filters = ScanFilters.Create(f.Home, [".cache/pip/", "~/.cache/./pip", Path.Combine(f.Home, ".cache/pip")], 0);
    Equal(1, filters.ExcludedPaths.Length);
    True(filters.Excludes(Path.Combine(f.Home, ".cache/pip")));
    True(filters.Excludes(Path.Combine(f.Home, ".cache/pip/http/file")));
    True(!filters.Excludes(Path.Combine(f.Home, ".cache/pip-other/file")));
    foreach (var invalid in new[] { "", " ", "/tmp", "../outside", "~other/.cache", "../../../../etc" })
        Throws<ArgumentException>(() => ScanFilters.Create(f.Home, [invalid], 0));
});
Test("目录和单文件排除优先于 --all，并保留相似前缀目录", () =>
{
    using var f = new Fixture();
    var preserved = new[] { f.File(".cache/pip/http/old", 20), f.File(".cache/app/preserve", 20),
        f.File(".cargo/registry/cache/index/old.crate", 20) };
    var deleted = f.File(".cache/pip-other/old", 20);
    var filters = ScanFilters.Create(f.Home, [".cache/pip", ".cache/app/preserve", ".cargo"], 0);
    var plan = CleaningEngine.Scan(f.Cleaners, DateTimeOffset.UtcNow.AddDays(-7), filters: filters);
    Equal(1L, plan.FileCount);
    var result = CleaningEngine.Clean(plan);
    Equal(1L, result.DeletedFiles);
    True(!System.IO.File.Exists(deleted));
    foreach (var path in preserved) True(System.IO.File.Exists(path));
});
Test("被排除的无权限目录不访问，HOME 可整体排除", () =>
{
    using var f = new Fixture();
    f.File(".cache/blocked/old", 20);
    f.File(".npm/_cacache/old", 20);
    var blocked = Path.Combine(f.Home, ".cache/blocked");
    System.IO.File.SetUnixFileMode(blocked, UnixFileMode.None);
    try
    {
        var filters = ScanFilters.Create(f.Home, [".cache/blocked"], 0);
        var plan = CleaningEngine.Scan(f.Cleaners, DateTimeOffset.UtcNow, filters: filters);
        Equal(0, plan.WarningCount);
        Equal(1L, plan.FileCount);
        var allExcluded = CleaningEngine.Scan(f.Cleaners, DateTimeOffset.UtcNow,
            filters: ScanFilters.Create(f.Home, ["~"], 0));
        Equal(0L, allExcluded.FileCount);
        Equal(0, allExcluded.WarningCount);
    }
    finally
    {
        System.IO.File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
});
Test("大小下限包含边界，年龄限制仍然生效", () =>
{
    using var f = new Fixture();
    var small = f.SizedFile(".cache/small", 1023);
    var boundary = f.SizedFile(".cache/boundary", 1024);
    var large = f.SizedFile(".cache/large", 2048);
    var fresh = f.SizedFile(".cache/fresh", 4096, ageDays: 0);
    var plan = CleaningEngine.Scan(f.Cleaners.Where(c => c.Default), DateTimeOffset.UtcNow.AddDays(-7),
        filters: ScanFilters.Create(f.Home, [], 1024));
    Equal(2L, plan.FileCount);
    Equal(3072UL, plan.Bytes);
    Equal(2L, CleaningEngine.Clean(plan).DeletedFiles);
    True(System.IO.File.Exists(small) && System.IO.File.Exists(fresh));
    True(!System.IO.File.Exists(boundary) && !System.IO.File.Exists(large));
});
Test("CLI 实际删除使用相同筛选条件，JSON 回显规范化路径", () =>
{
    using var f = new Fixture();
    var excluded = f.SizedFile(".cache/preserve/large", 2048);
    var small = f.SizedFile(".cache/small", 100);
    var removed = f.SizedFile(".cache/large", 2048);
    var run = RunCli(f, ["clean", "--all", "--yes", "--json", "--min-size", "1KiB", "--exclude", "~/.cache/preserve"]);
    Equal(0, run.Code);
    using var doc = JsonDocument.Parse(run.Output);
    Equal(1L, doc.RootElement.GetProperty("Result").GetProperty("DeletedFiles").GetInt64());
    Equal(1024UL, doc.RootElement.GetProperty("Filters").GetProperty("MinimumBytes").GetUInt64());
    Equal(Path.Combine(f.Home, ".cache/preserve"),
        doc.RootElement.GetProperty("Filters").GetProperty("ExcludedPaths")[0].GetString());
    True(System.IO.File.Exists(excluded) && System.IO.File.Exists(small));
    True(!System.IO.File.Exists(removed));
});
Test("无效排除路径在扫描和删除前拒绝", () =>
{
    using var f = new Fixture();
    var path = f.File(".cache/old", 20);
    Equal(2, RunCli(f, ["clean", "--yes", "--exclude", "/tmp"]).Code);
    True(System.IO.File.Exists(path));
});
Test("大文件排行按大小降序、同大小按路径排序且不改变候选", () =>
{
    using var f = new Fixture();
    var b = f.SizedFile(".cache/b", 2048);
    var a = f.SizedFile(".cache/a", 2048);
    f.SizedFile(".cache/small", 1024);
    var plan = f.Scan();
    var top = plan.LargestFiles(2);
    Equal(3L, plan.FileCount);
    Equal(2, top.Length);
    Equal(a, top[0].Path);
    Equal(b, top[1].Path);
    Equal(2048UL, top[0].Bytes);
    Equal(0, plan.LargestFiles(0).Length);
});
Test("JSON 排行反映筛选后的候选，dry-run 不删除文件", () =>
{
    using var f = new Fixture();
    f.SizedFile(".cache/excluded", 8192);
    var large = f.SizedFile(".cache/large", 4096);
    var small = f.SizedFile(".cache/small", 10);
    var run = RunCli(f, ["clean", "--dry-run", "--min-size", "1KiB", "--exclude", ".cache/excluded",
        "--top", "10", "--json", "--verbose"]);
    Equal(0, run.Code);
    using var doc = JsonDocument.Parse(run.Output);
    Equal(1L, doc.RootElement.GetProperty("CandidateFiles").GetInt64());
    Equal(1, doc.RootElement.GetProperty("TopFiles").GetArrayLength());
    Equal(large, doc.RootElement.GetProperty("TopFiles")[0].GetProperty("Path").GetString());
    Equal(1, doc.RootElement.GetProperty("Files").GetArrayLength());
    True(System.IO.File.Exists(large) && System.IO.File.Exists(small));
});
Test("排行不可作为实际清理数量限制，空候选返回空排行", () =>
{
    using var f = new Fixture();
    var path = f.File(".cache/old", 20);
    Equal(2, RunCli(f, ["clean", "--yes", "--top", "1"]).Code);
    True(System.IO.File.Exists(path));
    var empty = RunCli(f, ["scan", "--min-size", "1GiB", "--top", "10", "--json"]);
    Equal(0, empty.Code);
    using var doc = JsonDocument.Parse(empty.Output);
    Equal(0, doc.RootElement.GetProperty("TopFiles").GetArrayLength());
});
Test("用户筛选参数不适用于系统命令", () =>
{
    foreach (var flag in new[] { "--exclude", "--min-size", "--top" })
        Throws<ArgumentException>(() => SystemOptions.Parse(["scan", "journal", flag, "1"]));
});
Test("系统参数默认保留策略及严格验证", () =>
{
    Equal(14, SystemOptions.Parse(["scan", "journal"]).KeepDays);
    Equal(2, SystemOptions.Parse(["scan", "packages"]).KeepPackages);
    foreach (var arguments in new string[][] {
        ["clean"], ["scan"], ["clean", "packages", "journal"], ["clean", "unknown"],
        ["scan", "journal", "--keep-days", "0"], ["scan", "journal", "--keep-days", "36501"],
        ["scan", "journal", "--keep-days", "14;rm -rf"], ["scan", "journal", "--manager", "apt"],
        ["scan", "packages", "--keep-days", "14"], ["scan", "packages", "--manager", "/tmp/fake"],
        ["scan", "packages", "--manager", "pacman", "--keep-packages", "1"],
        ["scan", "packages", "--manager", "pacman", "--keep-packages", "101"],
        ["scan", "packages", "--keep-packages", "3"], ["scan", "packages", "--yes"],
        ["scan", "journal", "--dry-run"], ["list", "journal"], ["list", "--yes"],
        ["clean", "journal", "--json", "--json"], ["clean", "journal", "--older-than", "7"] })
        Throws<ArgumentException>(() => SystemOptions.Parse(arguments));
});
Test("系统清理只构建固定命令，不卸载包或轮转日志", () =>
{
    var expected = new Dictionary<string, string> {
        ["apt"] = "/usr/bin/apt-get|clean", ["dnf"] = "/usr/bin/dnf|clean|packages",
        ["dnf5"] = "/usr/bin/dnf5|clean|packages",
        ["pacman"] = "/usr/bin/paccache|--remove|--keep|3|--nocolor"
    };
    foreach (var manager in expected.Keys)
    {
        string[] arguments = manager == "pacman"
            ? ["scan", "packages", "--manager", manager, "--keep-packages", "3"]
            : ["scan", "packages", "--manager", manager];
        var plan = SystemMaintenance.CreatePlan(SystemOptions.Parse(arguments), _ => true);
        Equal(expected[manager], plan.Command.Executable + "|" + string.Join('|', plan.Command.Arguments));
    }
    var journal = SystemMaintenance.CreatePlan(SystemOptions.Parse(["scan", "journal", "--keep-days", "30"]), _ => true);
    Equal("/usr/bin/journalctl|--no-pager|--vacuum-time=30d",
        journal.Command.Executable + "|" + string.Join('|', journal.Command.Arguments));
});
Test("包管理器自动识别拒绝歧义并报告缺失工具", () =>
{
    var options = SystemOptions.Parse(["scan", "packages"]);
    Throws<ArgumentException>(() => SystemMaintenance.CreatePlan(options, _ => false));
    Throws<ArgumentException>(() => SystemMaintenance.CreatePlan(options, _ => true));
    Throws<ArgumentException>(() => SystemMaintenance.CreatePlan(options, path => path == "/usr/bin/pacman"));
    var pacman = SystemMaintenance.CreatePlan(options, path => path is "/usr/bin/pacman" or "/usr/bin/paccache");
    Equal("pacman", pacman.Manager);
    var dnf = SystemMaintenance.CreatePlan(options, path => path is "/usr/bin/dnf" or "/usr/bin/dnf5");
    Equal("dnf5", dnf.Manager);
    Throws<ArgumentException>(() => SystemMaintenance.CreatePlan(SystemOptions.Parse(["scan", "journal"]), _ => false));
});
Test("系统扫描和 dry-run 绝不启动外部清理工具", () =>
{
    foreach (var arguments in new string[][] {
        ["scan", "packages", "--json"], ["scan", "journal", "--json"],
        ["clean", "packages", "--dry-run", "--yes", "--json"],
        ["clean", "journal", "--dry-run", "--json"] })
    {
        var run = RunSystem(arguments);
        Equal(0, run.Code);
        Equal(0, run.Executed.Count);
        using var doc = JsonDocument.Parse(run.Output);
        True(doc.RootElement.GetProperty("DryRun").GetBoolean());
        Equal(JsonValueKind.Null, doc.RootElement.GetProperty("Result").ValueKind);
    }
});
Test("系统清理必须 root，非交互和 JSON 模式必须 --yes", () =>
{
    Throws<ArgumentException>(() => RunSystem(["clean", "journal", "--yes"], root: false));
    Throws<ArgumentException>(() => RunSystem(["clean", "journal"], root: true));
    Throws<ArgumentException>(() => RunSystem(["clean", "journal", "--json"], root: true, redirected: false));
    using var f = new Fixture();
    Equal(2, RunCli(f, ["system", "clean", "journal", "--yes"]).Code);
});
Test("系统交互确认仅接受 DELETE，取消不调用工具", () =>
{
    var cancelled = RunSystem(["clean", "journal"], root: true, redirected: false, input: "yes\n");
    Equal(0, cancelled.Code);
    Equal(0, cancelled.Executed.Count);
    var confirmed = RunSystem(["clean", "journal"], root: true, redirected: false, input: "DELETE\n");
    Equal(0, confirmed.Code);
    Equal(1, confirmed.Executed.Count);
});
Test("系统 JSON 结果保留工具输出和失败状态", () =>
{
    var run = RunSystem(["clean", "packages", "--yes", "--json"], root: true, exitCode: 7);
    Equal(1, run.Code);
    Equal(1, run.Executed.Count);
    using var doc = JsonDocument.Parse(run.Output);
    Equal(7, doc.RootElement.GetProperty("Result").GetProperty("ExitCode").GetInt32());
    Equal("fake output", doc.RootElement.GetProperty("Result").GetProperty("Output").GetString());
});
Test("系统工具启动前清空危险环境并固定 PATH", () =>
{
    var previous = Environment.GetEnvironmentVariable("LINUXCLEANER_TEST_SENTINEL");
    try
    {
        Environment.SetEnvironmentVariable("LINUXCLEANER_TEST_SENTINEL", "must-not-inherit");
        var run = SystemMaintenance.Execute(new("/usr/bin/env", []), CancellationToken.None);
        Equal(0, run.ExitCode);
        True(!run.Output.Contains("LINUXCLEANER_TEST_SENTINEL"));
        True(run.Output.Contains("PATH=/usr/sbin:/usr/bin:/sbin:/bin\n"));
        True(run.Output.Contains("HOME=/root\n"));
    }
    finally { Environment.SetEnvironmentVariable("LINUXCLEANER_TEST_SENTINEL", previous); }
});
Test("系统工具输出有界，失败退出码不会丢失", () =>
{
    var output = SystemMaintenance.Execute(new("/usr/bin/head", ["--bytes=100000", "/dev/zero"]), CancellationToken.None);
    Equal(0, output.ExitCode);
    True(output.Output.Length > 65536 && output.Output.Length < 66000);
    True(output.Output.Contains("输出已截断"));
    Equal(1, SystemMaintenance.Execute(new("/usr/bin/false", []), CancellationToken.None).ExitCode);
});
Test("中断系统操作会结束子进程", () =>
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    var watch = Stopwatch.StartNew();
    Throws<OperationCanceledException>(() => SystemMaintenance.Execute(new("/usr/bin/sleep", ["30"]), cancellation.Token));
    True(watch.Elapsed < TimeSpan.FromSeconds(10));
});
Test("系统 CLI 帮助和列表不依赖 HOME 或缓存配置", () =>
{
    using var f = new Fixture();
    Equal(0, RunCli(f, ["system", "--help"]).Code);
    var list = RunCli(f, ["system", "list", "--json"]);
    Equal(0, list.Code);
    using var doc = JsonDocument.Parse(list.Output);
    Equal(2, doc.RootElement.GetArrayLength());
});

Console.WriteLine($"\n{result.Count - result.Failed}/{result.Count} 测试通过");
return result.Failed == 0 ? 0 : 1;

void Test(string name, Action action)
{
    result.Count++;
    try { action(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { result.Failed++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
}

static void True(bool condition)
{
    if (!condition) throw new Exception("断言失败");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"预期 {expected}，实际 {actual}");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"预期异常 {typeof(T).Name}");
}

static (string Id, string Cache, string Preserved)[] NewRuleSamples() => [
    ("composer", ".composer/cache/files/pkg.zip", ".composer/auth.json"),
    ("poetry", ".cache/pypoetry/artifacts/pkg.whl", ".cache/pypoetry/virtualenvs/env/bin/python"),
    ("ccache", ".ccache/a/b/objR", ".ccache/ccache.conf"),
    ("sccache", ".cache/sccache/a/obj", ".config/sccache/config"),
    ("vscode", ".config/Code/CachedData/hash/cache", ".config/Code/User/History/id/entry"),
    ("discord", ".config/discord/Code Cache/js/cache", ".config/discord/Cache/gifs/favorite"),
    ("slack", ".config/Slack/Cache/Cache_Data/entry", ".config/Slack/Local Storage/leveldb/state"),
    ("thunderbird", ".cache/thunderbird/profile/cache2/entry", ".thunderbird/profile/Mail/Local Folders/Inbox"),
    ("thunderbird-profiles", ".thunderbird/Profiles/id.default/Cache/entry", ".thunderbird/Profiles/id.default/ImapMail/host/Inbox"),
    ("steam-shaders", ".local/share/Steam/steamapps/shadercache/480/cache", ".local/share/Steam/steamapps/compatdata/480/pfx/save"),
    ("steam-cache", ".local/share/Steam/config/htmlcache/Default/Cache/entry", ".local/share/Steam/config/htmlcache/Default/Cookies"),
    ("mesa-shaders", ".cache/mesa_shader_cache_sf/game/data", ".local/share/Steam/steamapps/common/game/bin"),
    ("gstreamer", ".cache/gstreamer-1.0/registry-x86_64.bin", ".local/share/gstreamer-1.0/plugins/plugin.so"),
    ("fontconfig", ".cache/fontconfig/hash-le64.cache-8", ".local/share/fonts/font.ttf"),
    ("winetricks", ".cache/winetricks/dotnet/install.exe", ".wine/drive_c/users/user/Documents/file"),
    ("jetbrains", ".cache/JetBrains/Idea2026.1/index/data", ".cache/JetBrains/Idea2026.1/LocalHistory/history"),
    ("opera", ".config/opera/Code Cache/js/entry", ".config/opera/Login Data")
];

static (int Code, string Output, List<SystemCommand> Executed) RunSystem(string[] args,
    bool root = false, bool redirected = true, string input = "", int exitCode = 0)
{
    var output = new StringWriter();
    var executed = new List<SystemCommand>();
    var code = SystemMaintenance.Run(args, new StringReader(input), output, new StringWriter(), redirected,
        isRoot: root, exists: path => path is "/usr/bin/pacman" or "/usr/bin/paccache" or "/usr/bin/journalctl",
        execute: (command, _) => { executed.Add(command); return new(exitCode, "fake output", "fake stderr"); });
    return (code, output.ToString(), executed);
}

static (int Code, string Output, string Error) RunCli(Fixture fixture, string[] args) =>
    RunProcess("dotnet", [typeof(Cli).Assembly.Location, .. args], fixture.Home);

static (int Code, string Output, string Error) RunProcess(string file, string[] args, string? home = null)
{
    var start = new ProcessStartInfo(file) { RedirectStandardOutput = true,
        RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    if (home is not null)
    {
        start.Environment["HOME"] = home;
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(home, ".cache");
    }
    using var process = Process.Start(start)!;
    process.StandardInput.Close();
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(15000))
    {
        process.Kill(entireProcessTree: true);
        throw new Exception("子进程超时");
    }
    return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
}

sealed class Fixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "linuxcleaner-tests-" + Guid.NewGuid().ToString("N"));
    internal string Home { get; }
    internal IReadOnlyList<Cleaner> Cleaners { get; }

    internal Fixture()
    {
        Home = Path.Combine(root, "home");
        Directory.CreateDirectory(Home);
        Cleaners = LinuxCleaner.Cleaners.Create(Home, null);
    }

    internal string File(string relative, int ageDays)
    {
        var path = Path.Combine(Home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "fixture data: " + relative);
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    internal string SizedFile(string relative, int bytes, int ageDays = 20)
    {
        var path = File(relative, ageDays);
        using (var stream = System.IO.File.OpenWrite(path)) stream.SetLength(bytes);
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    internal ScanPlan Scan(params string[] ids) => CleaningEngine.Scan(
        Cleaners.Where(c => ids.Length == 0 ? c.Default : ids.Contains(c.Id)),
        DateTimeOffset.UtcNow.AddDays(-7));

    public void Dispose() => Directory.Delete(root, recursive: true);
}
