namespace LinuxCleaner;

internal sealed record PlannedFile(string RelativePath, EntryStat Stat);
internal sealed record PlannedRoot(string Cleaner, string Path, EntryStat Stat, List<PlannedFile> Files);
internal sealed record FilePreview(string Cleaner, string Path, ulong Bytes);

internal sealed class ScanPlan
{
    internal List<PlannedRoot> Roots { get; } = [];
    internal List<string> Warnings { get; } = [];
    internal int WarningCount { get; private set; }
    internal int Skipped { get; set; }
    internal long FileCount => Roots.Sum(root => (long)root.Files.Count);
    internal ulong Bytes => Roots.Aggregate(0UL,
        (sum, root) => root.Files.Aggregate(sum, (total, file) => checked(total + file.Stat.Size)));

    internal IEnumerable<FilePreview> PreviewFiles() => Roots.SelectMany(root => root.Files.Select(file =>
        new FilePreview(root.Cleaner, Path.Combine(root.Path, file.RelativePath), file.Stat.Size)));

    internal FilePreview[] LargestFiles(int count) => PreviewFiles().OrderByDescending(file => file.Bytes)
        .ThenBy(file => file.Path, StringComparer.Ordinal).Take(count).ToArray();

    internal void Warn(string message)
    {
        WarningCount++;
        if (Warnings.Count < 100)
            Warnings.Add(message);
    }
}

internal sealed record CleanResult(long DeletedFiles, ulong DeletedBytes, long ChangedOrMissing,
    int ErrorCount, IReadOnlyList<string> Errors);

internal static class CleaningEngine
{
    internal static ScanPlan Scan(IEnumerable<Cleaner> cleaners, DateTimeOffset cutoff,
        CancellationToken cancellation = default, ScanFilters? filters = null)
    {
        filters ??= ScanFilters.None;
        var plan = new ScanPlan();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var uid = NativeDirectory.UserId;
        var seconds = cutoff.ToUnixTimeSeconds();
        var nanos = (uint)((cutoff.UtcTicks % TimeSpan.TicksPerSecond) * 100);
        foreach (var cleaner in cleaners)
        foreach (var rootPath in cleaner.Roots)
        {
            cancellation.ThrowIfCancellationRequested();
            if (filters.Excludes(rootPath))
                continue;
            try
            {
                using var root = NativeDirectory.OpenAbsolute(rootPath);
                var rootStat = root.StatSelf();
                if (rootStat.Uid != uid)
                {
                    plan.Warn($"跳过非当前用户拥有的缓存目录：{rootPath}");
                    continue;
                }
                var files = new List<PlannedFile>();
                plan.Roots.Add(new(cleaner.Id, rootPath, rootStat, files));
                var pending = new Stack<(string Path, EntryStat Stat)>();
                pending.Push(("", rootStat));
                while (pending.TryPop(out var item))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        using var directory = OpenRelative(root, item.Path, rootStat, uid);
                        if (!directory.StatSelf().SameIdentity(item.Stat))
                        {
                            plan.Warn($"扫描期间目录发生变化：{Path.Combine(rootPath, item.Path)}");
                            continue;
                        }
                        foreach (var name in directory.Names())
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var relative = item.Path.Length == 0 ? name : item.Path + "/" + name;
                            if (filters.Excludes(Path.Combine(rootPath, relative)))
                                continue;
                            try
                            {
                                var stat = directory.StatEntry(name);
                                if (Cleaners.Excluded(cleaner.Id, relative, stat.IsDirectory))
                                    continue;
                                if (stat.Uid != uid || !stat.SameMount(rootStat))
                                {
                                    plan.Skipped++;
                                    continue;
                                }
                                if (stat.IsDirectory)
                                    pending.Push((relative, stat));
                                else if (stat.IsRegular && stat.Links == 1)
                                {
                                    if (stat.Size >= filters.MinimumBytes && (stat.ModifySeconds < seconds ||
                                        (stat.ModifySeconds == seconds && stat.ModifyNanos <= nanos)))
                                        if (seenPaths.Add(Path.Combine(rootPath, relative)))
                                            files.Add(new(relative, stat));
                                }
                                else
                                    plan.Skipped++;
                            }
                            catch (IOException error)
                            {
                                plan.Warn($"{Path.Combine(rootPath, relative)}: {error.Message}");
                            }
                        }
                    }
                    catch (IOException error)
                    {
                        plan.Warn($"{Path.Combine(rootPath, item.Path)}: {error.Message}");
                    }
                }
            }
            catch (IOException error) when (NativeDirectory.Missing(error))
            {
                // A cleaner not installed on this machine is normal.
            }
            catch (IOException error)
            {
                plan.Warn($"{rootPath}: {error.Message}");
            }
        }
        return plan;
    }

    internal static CleanResult Clean(ScanPlan plan, CancellationToken cancellation = default)
    {
        long deleted = 0, changed = 0;
        ulong bytes = 0;
        var errorCount = 0;
        var errors = new List<string>();
        var uid = NativeDirectory.UserId;
        foreach (var target in plan.Roots)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var root = NativeDirectory.OpenAbsolute(target.Path);
                var rootStat = root.StatSelf();
                if (!rootStat.SameIdentity(target.Stat) || rootStat.Uid != uid ||
                    !rootStat.SameMount(target.Stat))
                    throw new IOException("预览后缓存根目录发生变化，拒绝清理。");
                foreach (var file in target.Files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        var parent = Path.GetDirectoryName(file.RelativePath) ?? "";
                        using var directory = OpenRelative(root, parent, rootStat, uid);
                        var name = Path.GetFileName(file.RelativePath);
                        var current = directory.StatEntry(name);
                        if (!current.IsRegular || !current.Unchanged(file.Stat) || !current.SameMount(rootStat))
                        {
                            changed++;
                            continue;
                        }
                        directory.DeleteFile(name);
                        deleted++;
                        bytes = checked(bytes + current.Size);
                    }
                    catch (IOException error) when (NativeDirectory.Missing(error))
                    {
                        changed++;
                    }
                    catch (IOException error)
                    {
                        AddError($"{Path.Combine(target.Path, file.RelativePath)}: {error.Message}");
                    }
                }
            }
            catch (IOException error)
            {
                AddError($"{target.Path}: {error.Message}");
            }
        }
        return new(deleted, bytes, changed, errorCount, errors);

        void AddError(string message)
        {
            errorCount++;
            if (errors.Count < 100)
                errors.Add(message);
        }
    }

    private static NativeDirectory OpenRelative(NativeDirectory root, string relative,
        EntryStat rootStat, uint uid)
    {
        var directory = root.Duplicate();
        try
        {
            foreach (var component in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var child = directory.OpenChild(component);
                directory.Dispose();
                directory = child;
                var stat = directory.StatSelf();
                if (stat.Uid != uid || !stat.SameMount(rootStat))
                    throw new IOException("拒绝跨挂载点或访问非当前用户拥有的目录。");
            }
            return directory;
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }
}
