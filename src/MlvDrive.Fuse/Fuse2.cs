using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MlvDrive.Core.VirtualFs;

namespace MlvDrive.Fuse;

/// <summary>
/// The virtual drive through the FUSE 2.9 high-level API (path based), which FUSE-T and
/// macFUSE provide on macOS and libfuse 2 on Linux. Read-only: everything that would
/// change the drive returns EROFS.
///
/// The only platform-specific parts are the library's location and the layout of
/// struct stat, which libfuse fills from what getattr writes.
/// </summary>
static unsafe class Fuse2
{
    // errno values (identical on macOS and Linux for these)
    const int ENOENT = 2, EIO = 5, EACCES = 13, ENOTDIR = 20, EISDIR = 21, EROFS = 30;
    const int O_ACCMODE = 3, W_OK = 2;

    static volatile VDirectory _root = new StaticDirectory("", DateTime.UtcNow);
    static uint _uid, _gid;

    public static void SetRoot(VDirectory root) => _root = root;

    [DllImport("libc", EntryPoint = "getuid")] static extern uint GetUid();
    [DllImport("libc", EntryPoint = "getgid")] static extern uint GetGid();

    delegate int FuseMainReal(int argc, IntPtr argv, IntPtr ops, nuint opSize, IntPtr userData);

    /// <summary>Candidate locations of the FUSE 2 library, in order of preference.</summary>
    public static IEnumerable<string> LibraryCandidates()
    {
        if (Environment.GetEnvironmentVariable("MLVDRIVE_LIBFUSE") is { Length: > 0 } custom) yield return custom;
        if (OperatingSystem.IsMacOS())
        {
            yield return "/usr/local/lib/libfuse-t.dylib";        // FUSE-T (no kernel extension)
            yield return "/opt/homebrew/lib/libfuse-t.dylib";
            yield return "/usr/local/lib/libfuse.2.dylib";        // macFUSE
            yield return "/usr/local/lib/libfuse.dylib";
            yield return "/opt/homebrew/lib/libfuse.2.dylib";
        }
        else
        {
            yield return "libfuse.so.2";
            yield return "/usr/lib/x86_64-linux-gnu/libfuse.so.2";
            yield return "/lib/x86_64-linux-gnu/libfuse.so.2";
        }
    }

    public static (IntPtr Handle, string Path)? LoadLibrary()
    {
        foreach (var path in LibraryCandidates())
        {
            if (path.StartsWith('/') && !File.Exists(path)) continue;
            if (NativeLibrary.TryLoad(path, out var h)) return (h, path);
        }
        return null;
    }

    /// <summary>Mount at <paramref name="mountPoint"/> and serve until unmounted. Returns libfuse's exit code.</summary>
    public static int Run(IntPtr library, string mountPoint, string options)
    {
        _uid = GetUid();
        _gid = GetGid();
        var main = Marshal.GetDelegateForFunctionPointer<FuseMainReal>(NativeLibrary.GetExport(library, "fuse_main_real"));

        // struct fuse_operations: the first 38 members are function pointers in this
        // order in libfuse 2.9, FUSE-T and macFUSE; libfuse copies only op_size bytes.
        const int OpCount = 38;
        var ops = (IntPtr*)NativeMemory.AllocZeroed((nuint)(OpCount * sizeof(IntPtr)));
        ops[0] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, int>)&GetAttr;
        ops[3] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int>)&ReadOnly; // mknod
        ops[4] = ops[3];   // mkdir
        ops[5] = ops[3];   // unlink
        ops[6] = ops[3];   // rmdir
        ops[7] = ops[3];   // symlink
        ops[8] = ops[3];   // rename
        ops[9] = ops[3];   // link
        ops[10] = ops[3];  // chmod
        ops[11] = ops[3];  // chown
        ops[12] = ops[3];  // truncate
        ops[13] = ops[3];  // utime
        ops[14] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int*, int>)&Open;
        ops[15] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, byte*, nuint, long, IntPtr, int>)&Read;
        ops[16] = ops[3];  // write
        ops[19] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&Release;
        ops[21] = ops[3];  // setxattr
        ops[25] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&OpenDir;
        ops[26] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, IntPtr, long, IntPtr, int>)&ReadDir;
        ops[27] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, IntPtr, int>)&Release;  // releasedir
        ops[31] = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, int>)&Access;
        ops[32] = ops[3];  // create
        ops[33] = ops[3];  // ftruncate
        ops[36] = ops[3];  // utimens

        var args = new[] { "MLVDrive", mountPoint, "-f" }.Concat(options.Length > 0 ? new[] { "-o", options } : Array.Empty<string>()).ToArray();
        var argv = (IntPtr*)NativeMemory.AllocZeroed((nuint)((args.Length + 1) * sizeof(IntPtr)));
        for (int i = 0; i < args.Length; i++) argv[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        try
        {
            return main(args.Length, (IntPtr)argv, (IntPtr)ops, (nuint)(OpCount * sizeof(IntPtr)), IntPtr.Zero);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++) Marshal.FreeCoTaskMem(argv[i]);
            NativeMemory.Free(argv);
            NativeMemory.Free(ops);
        }
    }

    static string PathOf(byte* p) => Marshal.PtrToStringUTF8((IntPtr)p) ?? "/";

    static VNode? Find(byte* path) => VirtualPath.Resolve(_root, PathOf(path));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int GetAttr(byte* path, byte* stat)
    {
        try
        {
            var node = Find(path);
            if (node is null) return -ENOENT;
            StatLayout.Write(stat, node, _uid, _gid);
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int Open(byte* path, int* fileInfo)
    {
        try
        {
            var node = Find(path);
            if (node is null) return -ENOENT;
            if (node is VDirectory) return -EISDIR;
            if ((fileInfo[0] & O_ACCMODE) != 0) return -EROFS;   // fuse_file_info.flags
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int Read(byte* path, byte* buffer, nuint size, long offset, IntPtr fileInfo)
    {
        try
        {
            if (Find(path) is not VFile file) return -ENOENT;
            if (offset < 0) return -EIO;
            int want = (int)Math.Min(size, int.MaxValue);
            return file.Read(offset, new Span<byte>(buffer, want));
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int Release(byte* path, IntPtr fileInfo) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int OpenDir(byte* path, IntPtr fileInfo)
    {
        try { return Find(path) switch { VDirectory => 0, null => -ENOENT, _ => -ENOTDIR }; }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int ReadDir(byte* path, IntPtr buffer, IntPtr filler, long offset, IntPtr fileInfo)
    {
        try
        {
            if (Find(path) is not VDirectory dir) return -ENOENT;
            var fill = (delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr, long, int>)filler;
            Span<byte> name = stackalloc byte[1024];
            foreach (var entry in new[] { ".", ".." }.Concat(dir.Children().Select(c => c.Name)))
            {
                int n = System.Text.Encoding.UTF8.GetBytes(entry, name[..^1]);
                name[n] = 0;
                fixed (byte* p = name)
                    if (fill(buffer, p, IntPtr.Zero, 0) != 0) break;
            }
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int Access(byte* path, int mask)
    {
        try
        {
            if (Find(path) is null) return -ENOENT;
            return (mask & W_OK) != 0 ? -EROFS : 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    /// <summary>Every operation that would change the drive (any argument list: the caller cleans up).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int ReadOnly(IntPtr a, IntPtr b, IntPtr c, IntPtr d, IntPtr e, IntPtr f) => -EROFS;

    static int Fail(Exception ex)
    {
        Log.Write("filesystem error: " + ex.Message);
        return ex is IOException or UnauthorizedAccessException ? -EIO : -EACCES;
    }
}

/// <summary>struct stat as libfuse expects it on this platform.</summary>
static unsafe class StatLayout
{
    const uint S_IFDIR = 0x4000, S_IFREG = 0x8000;

    public static void Write(byte* st, VNode node, uint uid, uint gid)
    {
        new Span<byte>(st, 144).Clear();
        bool dir = node is VDirectory;
        uint mode = dir ? S_IFDIR | 0x16D /* 0555 */ : S_IFREG | 0x124 /* 0444 */;
        long size = node is VFile f ? f.Size : 0;
        long blocks = (size + 511) / 512;
        long secs = new DateTimeOffset(DateTime.SpecifyKind(node.Modified, DateTimeKind.Utc)).ToUnixTimeSeconds();

        if (OperatingSystem.IsMacOS())
        {
            // <sys/stat.h> with 64-bit inodes (the default): 144 bytes
            *(ushort*)(st + 4) = (ushort)mode;          // st_mode
            *(ushort*)(st + 6) = (ushort)(dir ? 2 : 1); // st_nlink
            *(uint*)(st + 16) = uid;
            *(uint*)(st + 20) = gid;
            foreach (int t in new[] { 32, 48, 64, 80 })  // atime, mtime, ctime, birthtime (timespec)
                *(long*)(st + t) = secs;
            *(long*)(st + 96) = size;
            *(long*)(st + 104) = blocks;
            *(int*)(st + 112) = 4096;                    // st_blksize
        }
        else
        {
            // glibc x86_64: 144 bytes
            *(ulong*)(st + 16) = (ulong)(dir ? 2 : 1);  // st_nlink
            *(uint*)(st + 24) = mode;
            *(uint*)(st + 28) = uid;
            *(uint*)(st + 32) = gid;
            *(long*)(st + 48) = size;
            *(long*)(st + 56) = 4096;                    // st_blksize
            *(long*)(st + 64) = blocks;
            foreach (int t in new[] { 72, 88, 104 })     // atime, mtime, ctime (timespec)
                *(long*)(st + t) = secs;
        }
    }
}
