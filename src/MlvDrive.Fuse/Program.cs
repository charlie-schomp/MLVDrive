using System.Globalization;
using MlvDrive.Core.Dng;
using MlvDrive.Core.VirtualFs;
using MlvDrive.Fuse;

// MLV Drive for macOS (and Linux): MLV clips on every mounted volume, shown as CinemaDNG
// folders under one mount point (default ~/MLV Drive), read-only, until Ctrl+C.
//
//   MLVDrive [--mount DIR] [--folder DIR]... [--no-volumes] [--fps N]

var opts = Options.Parse(args);
if (opts is null)
{
    Console.Error.WriteLine("usage: MLVDrive [--mount DIR] [--folder DIR]... [--no-volumes] [--fps N]");
    return 2;
}

if (Fuse2.LoadLibrary() is not { } lib)
{
    Console.Error.WriteLine(OperatingSystem.IsMacOS()
        ? "MLV Drive needs FUSE-T (recommended, https://www.fuse-t.org - or: brew install macos-fuse-t/homebrew-cask/fuse-t)\nor macFUSE (https://macfuse.github.io). Install one, then run this again."
        : "MLV Drive needs FUSE (apt install fuse3, dnf install fuse3, or pacman -S fuse3), or set MLVDRIVE_LIBFUSE to the library's path.");
    return 1;
}
Log.Write($"MLV Drive {typeof(Options).Assembly.GetName().Version} using {lib.Path}");

Directory.CreateDirectory(opts.MountPoint);
if (Directory.EnumerateFileSystemEntries(opts.MountPoint).Any())
{
    Console.Error.WriteLine($"{opts.MountPoint} is not empty (or MLV Drive is already running). Unmount it or choose another --mount folder.");
    return 1;
}

using var cache = new ClipCache(new DngOptions(opts.Fps));
using var watcher = new UnixSourceWatcher(opts, cache, tree => Fuse2.SetRoot(tree));
watcher.Start();
using var release = new Timer(_ => cache.ReleaseIdle(TimeSpan.FromSeconds(10)), null, 5000, 5000);

// Stop on Ctrl+C, closing the Terminal window, or kill: unmount, and libfuse's loop
// returns. (libfuse only installs its own handlers when none exist, and .NET has them.)
bool stopping = false;
void Stop(System.Runtime.InteropServices.PosixSignalContext ctx)
{
    ctx.Cancel = true;
    if (stopping) return;
    stopping = true;
    Log.Write("stopping...");
    Unmount.Run(opts.MountPoint);
}
using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, Stop);
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, Stop);
using var sighup = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, Stop);

Console.WriteLine($"MLV Drive: clips appear in \"{opts.MountPoint}\". Press Ctrl+C to stop.");
// Mount options differ between FUSE-T, macFUSE and libfuse; fall back to fewer if refused.
string volname = OperatingSystem.IsMacOS() ? "volname=MLV Drive" : "fsname=mlvdrive";
var attempts = new[] { $"ro,{volname}", volname, "" };
int result = 1;
foreach (var o in attempts)
{
    var started = DateTime.UtcNow;
    Log.Write($"mounting {opts.MountPoint} with options \"{o}\"");
    result = Fuse2.Run(lib.Handle, opts.MountPoint, o);
    if (stopping || result == 0 || DateTime.UtcNow - started > TimeSpan.FromSeconds(5)) break;   // served, then unmounted
    Log.Write($"mount refused (code {result}), trying fewer options");
}
Log.Write($"stopped (code {result})");
return result;

sealed record Options(string MountPoint, List<string> Folders, bool Volumes, double? Fps)
{
    public static Options? Parse(string[] args)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var o = new Options(Path.Combine(home, OperatingSystem.IsMacOS() ? "MLV Drive" : "mlvdrive"), new List<string>(), true, null);
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--mount" when Next() is { } m: o = o with { MountPoint = Path.GetFullPath(m) }; break;
                case "--folder" when Next() is { } f: o.Folders.Add(Path.GetFullPath(f)); break;
                case "--no-volumes": o = o with { Volumes = false }; break;
                case "--fps" when Next() is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps): o = o with { Fps = fps }; break;
                default: return null;
            }
        }
        return o;
    }
}

static class Unmount
{
    /// <summary>Unmount with the system tool; libfuse's loop then returns.</summary>
    public static void Run(string mountPoint)
    {
        var tries = OperatingSystem.IsMacOS()
            ? new[] { ("/sbin/umount", new[] { mountPoint }), ("/usr/sbin/diskutil", new[] { "unmount", "force", mountPoint }) }
            : new[] { ("fusermount3", new[] { "-u", mountPoint }), ("fusermount", new[] { "-u", mountPoint }), ("umount", new[] { mountPoint }) };
        foreach (var (tool, args) in tries)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit(10000);
                if (p.ExitCode == 0) return;
                Log.Write($"{tool}: {p.StandardError.ReadToEnd().Trim()}");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Write($"{tool}: {ex.Message}");
            }
        }
    }
}

static class Log
{
    static readonly object Lock = new();
    public static void Write(string message)
    {
        lock (Lock) Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {message}");
    }
}

/// <summary>
/// Volumes and extra folders to show, kept current: the volume list is polled (cheap) and
/// each source is watched for clips appearing or disappearing; changes trigger a rescan.
/// </summary>
sealed class UnixSourceWatcher : IDisposable
{
    readonly Options _options;
    readonly ClipCache _cache;
    readonly Action<StaticDirectory> _onTree;
    readonly Timer _poll, _debounce;
    readonly List<FileSystemWatcher> _watchers = new();
    readonly object _lock = new();
    string _lastKey = "";
    HashSet<string> _lastRoots = new();

    public UnixSourceWatcher(Options options, ClipCache cache, Action<StaticDirectory> onTree)
    {
        _options = options;
        _cache = cache;
        _onTree = onTree;
        _debounce = new Timer(_ => Scan(), null, Timeout.Infinite, Timeout.Infinite);
        _poll = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _debounce.Change(0, Timeout.Infinite);
        _poll.Change(3000, 3000);
    }

    List<ClipSource> Sources()
    {
        var list = new List<ClipSource>();
        if (_options.Volumes)
        {
            var bases = OperatingSystem.IsMacOS()
                ? new[] { "/Volumes" }
                : new[] { $"/media/{Environment.UserName}", "/media", "/run/media/" + Environment.UserName };
            foreach (var b in bases.Where(Directory.Exists))
            {
                foreach (var dir in SafeDirs(b))
                {
                    var info = new DirectoryInfo(dir);
                    if (info.LinkTarget is not null) continue;                 // "Macintosh HD" -> /
                    if (info.Name.StartsWith('.')) continue;
                    if (Path.GetFullPath(dir) == _options.MountPoint) continue;
                    if (info.Name == "MLV Drive") continue;
                    if (bases.Contains(dir)) continue;                         // /media/<user>, listed itself
                    list.Add(new ClipSource(info.Name, dir));
                }
            }
        }
        foreach (var f in _options.Folders.Where(Directory.Exists))
            list.Add(new ClipSource(Path.GetFileName(Path.TrimEndingDirectorySeparator(f)) is { Length: > 0 } n ? n : f, f));
        return list;
    }

    static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    void Poll()
    {
        var key = string.Join("|", Sources().Select(s => s.RootPath).OrderBy(x => x));
        if (key != _lastKey) _debounce.Change(1000, Timeout.Infinite);
    }

    void Scan()
    {
        try
        {
            var sources = Sources();
            _lastKey = string.Join("|", sources.Select(s => s.RootPath).OrderBy(x => x));
            var roots = sources.Select(s => s.RootPath).ToHashSet();
            foreach (var gone in _lastRoots.Except(roots)) _cache.Forget(gone);
            _lastRoots = roots;

            var started = DateTime.UtcNow;
            var tree = SourceScanner.Build(sources, _cache);
            _onTree(tree);
            int clips = Count(tree);
            Log.Write($"{clips} clip{(clips == 1 ? "" : "s")} from {(tree.Children().Count == 0 ? "no volume" : string.Join(", ", tree.Children().Select(c => c.Name)))} ({(DateTime.UtcNow - started).TotalMilliseconds:0} ms)");
            Rewatch(sources);
        }
        catch (Exception ex)
        {
            Log.Write("scan failed: " + ex.Message);
        }
    }

    static int Count(VDirectory d) => d.Children().Sum(c => c is ClipDirectory ? 1 : c is StaticDirectory s ? Count(s) : 0);

    void Rewatch(List<ClipSource> sources)
    {
        lock (_lock)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            foreach (var s in sources)
            {
                try
                {
                    var w = new FileSystemWatcher(s.RootPath) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                    FileSystemEventHandler changed = (_, e) => { if (Relevant(e.FullPath)) _debounce.Change(2000, Timeout.Infinite); };
                    w.Created += changed;
                    w.Deleted += changed;
                    w.Renamed += (_, e) => { if (Relevant(e.FullPath) || Relevant(e.OldFullPath)) _debounce.Change(2000, Timeout.Infinite); };
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Log.Write($"cannot watch {s.RootPath}: {ex.Message} (rescans still happen when volumes change)");
                }
            }
        }
    }

    static bool Relevant(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return true;
        return ext.Equals(".mlv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
               (ext.Length == 4 && char.ToUpperInvariant(ext[1]) == 'M' && char.IsDigit(ext[2]) && char.IsDigit(ext[3]));
    }

    public void Dispose()
    {
        _poll.Dispose();
        _debounce.Dispose();
        lock (_lock)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }
}
