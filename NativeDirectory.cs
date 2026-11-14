using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LinuxCleaner;

// Linux directory descriptors keep traversal anchored; user paths are never followed through symlinks.
internal sealed class NativeDirectory : IDisposable
{
    private static int ReadDirectory => DirectoryFlags(RuntimeInformation.ProcessArchitecture);

    // Linux arm64 uses different DIRECTORY/NOFOLLOW bits from x86-64.
    internal static int DirectoryFlags(Architecture architecture) => architecture switch
    {
        Architecture.X64 => 0x10000 | 0x20000 | 0x80000,
        Architecture.Arm64 => 0x4000 | 0x8000 | 0x80000,
        _ => throw new PlatformNotSupportedException("仅支持 Linux x64 / arm64。")
    };
    private const int NoFollow = 0x100;
    private const int EmptyPath = 0x1000;
    private const uint StatMask = 0x7ff | 0x1000; // BASIC_STATS | MNT_ID
    private readonly SafeFileHandle handle;
    internal int Fd => handle.DangerousGetHandle().ToInt32();

    private NativeDirectory(int fd) => handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);

    internal static uint UserId => GetEffectiveUserId();

    internal static NativeDirectory OpenAbsolute(string path)
    {
        var fd = Open("/", ReadDirectory);
        if (fd < 0)
            throw Error("open /");
        var directory = new NativeDirectory(fd);
        try
        {
            foreach (var part in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var child = directory.OpenChild(part);
                directory.Dispose();
                directory = child;
            }
            return directory;
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    internal NativeDirectory OpenChild(string name)
    {
        ValidateName(name);
        var fd = OpenAt(Fd, name, ReadDirectory);
        if (fd < 0)
            throw Error($"open {name}");
        return new NativeDirectory(fd);
    }

    internal NativeDirectory Duplicate()
    {
        var fd = OpenAt(Fd, ".", ReadDirectory);
        if (fd < 0)
            throw Error("open descriptor");
        return new NativeDirectory(fd);
    }

    internal EntryStat StatSelf() => Stat("", EmptyPath);

    internal EntryStat StatEntry(string name)
    {
        ValidateName(name);
        return Stat(name, NoFollow);
    }

    private EntryStat Stat(string name, int flags)
    {
        if (Statx(Fd, name, flags, StatMask, out var stat) != 0)
            throw Error($"stat {name}");
        if ((stat.Mask & StatMask) != StatMask)
            throw new IOException("文件系统未提供完整 statx 元数据（含挂载点 ID），拒绝清理。");
        return stat;
    }

    internal IEnumerable<string> Names()
    {
        // /proc resolves our own live descriptor, not a user-controlled path.
        foreach (var path in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{Fd}"))
            yield return Path.GetFileName(path);
    }

    internal void DeleteFile(string name)
    {
        ValidateName(name);
        if (UnlinkAt(Fd, name, 0) != 0)
            throw Error($"unlink {name}");
    }

    private static void ValidateName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
            throw new ArgumentException("无效的目录项名称。");
    }

    private static IOException Error(string operation)
    {
        var code = Marshal.GetLastPInvokeError();
        return new IOException($"{operation}: {new Win32Exception(code).Message} (errno {code})",
            new Win32Exception(code));
    }

    internal static bool Missing(IOException error) =>
        error.InnerException is Win32Exception { NativeErrorCode: 2 };

    public void Dispose() => handle.Dispose();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int fd, string path, int flags);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int fd, string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int fd, string path, int flags, uint mask, out EntryStat stat);
}

[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct EntryStat
{
    [FieldOffset(0)] internal uint Mask;
    [FieldOffset(16)] internal uint Links;
    [FieldOffset(20)] internal uint Uid;
    [FieldOffset(28)] internal ushort Mode;
    [FieldOffset(32)] internal ulong Inode;
    [FieldOffset(40)] internal ulong Size;
    [FieldOffset(96)] internal long ChangeSeconds;
    [FieldOffset(104)] internal uint ChangeNanos;
    [FieldOffset(112)] internal long ModifySeconds;
    [FieldOffset(120)] internal uint ModifyNanos;
    [FieldOffset(136)] internal uint DeviceMajor;
    [FieldOffset(140)] internal uint DeviceMinor;
    [FieldOffset(144)] internal ulong MountId;

    internal bool IsDirectory => (Mode & 0xf000) == 0x4000;
    internal bool IsRegular => (Mode & 0xf000) == 0x8000;
    internal bool SameIdentity(EntryStat other) =>
        Inode == other.Inode && DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor;
    internal bool SameMount(EntryStat other) =>
        DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor
        && MountId == other.MountId;
    internal bool Unchanged(EntryStat other) =>
        SameIdentity(other) && Mode == other.Mode && Uid == other.Uid && Links == other.Links
        && Size == other.Size && ChangeSeconds == other.ChangeSeconds && ChangeNanos == other.ChangeNanos
        && ModifySeconds == other.ModifySeconds && ModifyNanos == other.ModifyNanos;
}
