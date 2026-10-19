using MlvDrive.Core.VirtualFs;

namespace MlvDrive.Windows;

/// <summary>
/// Keeps the virtual drive's tree in step with the computer: rescans when a drive is
/// plugged in or removed (Windows device notifications) and when clips appear, change or
/// disappear on a watched drive (file notifications, e.g. while copying from the card).
/// Rescans are debounced and run in the background; the old tree serves until the new
/// one is ready.
/// </summary>
sealed class SourceWatcher : IDisposable
{
    readonly Func<Settings> _settings;
    readonly ClipCache _cache;
    readonly Action<StaticDirectory, IReadOnlyList<ClipSource>> _onTree;
    readonly System.Threading.Timer _debounce;
    readonly List<FileSystemWatcher> _watchers = new();
    readonly DeviceNotificationWindow _devices;
    readonly object _lock = new();
    CancellationTokenSource? _scan;
    HashSet<string> _lastRoots = new(StringComparer.OrdinalIgnoreCase);

    public SourceWatcher(Func<Settings> settings, ClipCache cache, Action<StaticDirectory, IReadOnlyList<ClipSource>> onTree)
    {
        _settings = settings;
        _cache = cache;
        _onTree = onTree;
        _debounce = new System.Threading.Timer(_ => StartScan(), null, Timeout.Infinite, Timeout.Infinite);
        _devices = new DeviceNotificationWindow(() => RequestScan(1500));
    }

    /// <summary>Rescan after <paramref name="delayMs"/> (further requests in the meantime just postpone it).</summary>
    public void RequestScan(int delayMs = 1000) => _debounce.Change(delayMs, Timeout.Infinite);

    /// <summary>The places to show: drives (not the system drive or our own), plus extra folders.</summary>
    public static List<ClipSource> CurrentSources(Settings s)
    {
        var list = new List<ClipSource>();
        string system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        string own = s.DriveLetter + ":\\";
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (string.Equals(d.RootDirectory.FullName, own, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(d.DriveFormat, "MLVDrive", StringComparison.OrdinalIgnoreCase)) continue;
                if (!s.IncludeSystemDrive && string.Equals(d.RootDirectory.FullName, system, StringComparison.OrdinalIgnoreCase)) continue;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? $"Drive {d.Name[0]}" : d.VolumeLabel;
                list.Add(new ClipSource(label, d.RootDirectory.FullName));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var f in s.ExtraFolders.Where(Directory.Exists))
            list.Add(new ClipSource(Path.GetFileName(Path.TrimEndingDirectorySeparator(f)) is { Length: > 0 } n ? n : f, f));
        return list;
    }

    void StartScan()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _scan?.Cancel();
            _scan = cts = new CancellationTokenSource();
        }
        Task.Run(() =>
        {
            try
            {
                var s = _settings();
                var sources = CurrentSources(s);

                // drives that went away: drop their clips so nothing holds them open
                var roots = sources.Select(x => x.RootPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var gone in _lastRoots.Except(roots)) _cache.Forget(gone);
                _lastRoots = roots;

                var started = DateTime.UtcNow;
                var tree = SourceScanner.Build(sources, _cache, s.ScanDepth, cts.Token);
                if (cts.IsCancellationRequested) return;
                Log.Write($"scan: {string.Join(", ", sources.Select(x => $"{x.DisplayName}={x.RootPath}"))} -> " +
                          $"{string.Join(", ", tree.Children().Select(c => c.Name))} ({(DateTime.UtcNow - started).TotalMilliseconds:0} ms)");
                _onTree(tree, sources);
                Rewatch(sources);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write("scan failed: " + ex);
            }
        });
    }

    void Rewatch(IReadOnlyList<ClipSource> sources)
    {
        lock (_lock)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            foreach (var src in sources)
            {
                try
                {
                    var w = new FileSystemWatcher(src.RootPath)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        InternalBufferSize = 64 * 1024,
                    };
                    FileSystemEventHandler changed = (_, e) => { if (Relevant(e.FullPath)) RequestScan(2000); };
                    w.Created += changed;
                    w.Deleted += changed;
                    w.Renamed += (_, e) => { if (Relevant(e.FullPath) || Relevant(e.OldFullPath)) RequestScan(2000); };
                    w.Error += (_, _) => RequestScan(5000);
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    Log.Write($"cannot watch {src.RootPath}: {ex.Message}");
                }
            }
        }
    }

    // A clip, a chunk, a scratch WAV, or a folder (which may hold clips) appearing or going.
    static bool Relevant(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return true;
        return ext.Equals(".mlv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
               (ext.Length == 4 && char.ToUpperInvariant(ext[1]) == 'M' && char.IsDigit(ext[2]) && char.IsDigit(ext[3]));
    }

    public void Dispose()
    {
        _debounce.Dispose();
        _devices.Dispose();
        lock (_lock)
        {
            _scan?.Cancel();
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }

    /// <summary>Hidden window that receives WM_DEVICECHANGE (drive arrival / removal).</summary>
    sealed class DeviceNotificationWindow : NativeWindow, IDisposable
    {
        const int WmDeviceChange = 0x0219, DbtDeviceArrival = 0x8000, DbtDeviceRemoveComplete = 0x8004;
        readonly Action _changed;

        public DeviceNotificationWindow(Action changed)
        {
            _changed = changed;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmDeviceChange && (int)m.WParam is DbtDeviceArrival or DbtDeviceRemoveComplete)
                _changed();
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
