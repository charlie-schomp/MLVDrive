using MlvDrive.Core.Audio;
using MlvDrive.Core.Dng;
using MlvDrive.Core.Mlv;

namespace MlvDrive.Core.VirtualFs;

/// <summary>A node of the virtual drive. Paths use '\' and are case-insensitive.</summary>
public abstract class VNode
{
    public string Name { get; }
    public DateTime Modified { get; }
    protected VNode(string name, DateTime modified) { Name = name; Modified = modified; }
}

public abstract class VDirectory : VNode
{
    protected VDirectory(string name, DateTime modified) : base(name, modified) { }
    /// <summary>The directory's entries (may index a clip on first call).</summary>
    public abstract IReadOnlyList<VNode> Children();
    public virtual VNode? Child(string name) =>
        Children().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class VFile : VNode
{
    readonly IVirtualFileContent _content;
    public VFile(string name, DateTime modified, IVirtualFileContent content) : base(name, modified) => _content = content;
    public long Size => _content.Size;
    public int Read(long offset, Span<byte> destination) => _content.Read(offset, destination);
}

/// <summary>A fixed set of children (the root, a drive, a folder on a drive).</summary>
public sealed class StaticDirectory : VDirectory
{
    readonly List<VNode> _children = new();
    readonly Dictionary<string, VNode> _byName = new(StringComparer.OrdinalIgnoreCase);

    public StaticDirectory(string name, DateTime modified) : base(name, modified) { }

    public override IReadOnlyList<VNode> Children() => _children;
    public override VNode? Child(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Add a child; a name already taken gets " (2)", " (3)" ... appended.</summary>
    public VNode Add(Func<string, VNode> make, string preferredName)
    {
        string name = preferredName;
        for (int i = 2; _byName.ContainsKey(name); i++) name = $"{preferredName} ({i})";
        var node = make(name);
        _children.Add(node);
        _byName[name] = node;
        return node;
    }

    public StaticDirectory GetOrAddDirectory(string name, DateTime modified)
    {
        if (_byName.TryGetValue(name, out var existing) && existing is StaticDirectory d) return d;
        return (StaticDirectory)Add(n => new StaticDirectory(n, modified), name);
    }

    public void SortChildren() => _children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One clip as a folder: NAME_000000.dng ... plus NAME.wav. The clip is indexed the
/// first time the folder is listed, and again whenever the .MLV (or a chunk) changes size
/// or time - a clip that is still being copied fills in as the copy progresses.
/// </summary>
public sealed class ClipDirectory : VDirectory
{
    readonly string _mlvPath;
    readonly ClipCache _cache;

    public string MlvPath => _mlvPath;

    public ClipDirectory(string name, string mlvPath, DateTime modified, ClipCache cache) : base(name, modified)
    {
        _mlvPath = mlvPath;
        _cache = cache;
    }

    public override IReadOnlyList<VNode> Children() => _cache.Get(_mlvPath, Name).Entries;
    public override VNode? Child(string name) => _cache.Get(_mlvPath, Name).Lookup(name);

    /// <summary>What happened when the clip was indexed (null if it opened cleanly).</summary>
    public string? Problem => _cache.Get(_mlvPath, Name).Problem;
}

/// <summary>Opened clips, keyed by path and invalidated when their files change.</summary>
public sealed class ClipCache : IDisposable
{
    public sealed class Entry
    {
        public required IReadOnlyList<VNode> Entries { get; init; }
        public required Dictionary<string, VNode> ByName { get; init; }
        public required string Signature { get; init; }
        public MlvClip? Clip { get; init; }
        public DngSequence? Sequence { get; init; }
        public string? Problem { get; init; }
        public VNode? Lookup(string name) => ByName.GetValueOrDefault(name);
    }

    readonly object _lock = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> _lru = new();
    readonly int _capacity;

    public DngOptions Options { get; set; }

    public ClipCache(DngOptions? options = null, int capacity = 64)
    {
        Options = options ?? new DngOptions();
        _capacity = capacity;
    }

    public Entry Get(string mlvPath, string clipName)
    {
        string sig = Signature(mlvPath);
        lock (_lock)
        {
            if (_entries.TryGetValue(mlvPath, out var e) && e.Signature == sig)
            {
                _lru.Remove(mlvPath);
                _lru.AddFirst(mlvPath);
                return e;
            }
        }

        var built = Build(mlvPath, clipName, sig);
        lock (_lock)
        {
            if (_entries.Remove(mlvPath, out var old)) { _lru.Remove(mlvPath); old.Clip?.Dispose(); }
            _entries[mlvPath] = built;
            _lru.AddFirst(mlvPath);
            while (_lru.Count > _capacity)
            {
                var last = _lru.Last!.Value;
                _lru.RemoveLast();
                if (_entries.Remove(last, out var evicted)) evicted.Clip?.Dispose();
            }
        }
        return built;
    }

    /// <summary>
    /// Close the files of clips not read for <paramref name="idle"/>. They reopen on the
    /// next read, so this is invisible to an editor but lets a drive be ejected.
    /// </summary>
    public void ReleaseIdle(TimeSpan idle)
    {
        List<MlvClip> clips;
        lock (_lock) clips = _entries.Values.Select(e => e.Clip).OfType<MlvClip>().ToList();
        foreach (var c in clips)
            if (c.IdleMilliseconds >= idle.TotalMilliseconds) c.ReleaseHandles();
    }

    /// <summary>Close and forget every clip under <paramref name="rootPath"/> (a drive that was removed).</summary>
    public void Forget(string rootPath)
    {
        var prefix = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        lock (_lock)
        {
            foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _entries.Remove(key, out var e);
                _lru.Remove(key);
                e?.Clip?.Dispose();
            }
        }
    }

    /// <summary>Forget every opened clip (after a settings change).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var e in _entries.Values) e.Clip?.Dispose();
            _entries.Clear();
            _lru.Clear();
        }
    }

    static string Signature(string mlvPath)
    {
        var parts = new List<string>();
        foreach (var p in MlvClip.FindChunks(mlvPath).Prepend(mlvPath))
        {
            try
            {
                var fi = new FileInfo(p);
                parts.Add($"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}");
            }
            catch (IOException) { parts.Add("?"); }
        }
        return string.Join("|", parts);
    }

    Entry Build(string mlvPath, string clipName, string sig)
    {
        var modified = SafeTime(mlvPath);
        MlvClip? clip = null;
        try
        {
            clip = MlvClip.Open(mlvPath);
            if (clip.Unsupported is { } why)
                return Problem(sig, clip, $"{Path.GetFileName(mlvPath)}: {why}", modified);
            if (clip.Frames.Count == 0)
                return Problem(sig, clip, $"{Path.GetFileName(mlvPath)}: no complete frames yet", modified);

            // audio first: the scratch WAV's LTC is the DNGs' timecode
            var (audio, scratch, start) = ClipAudioSource.For(clip, mlvPath, Options);
            var seq = new DngSequence(clip, Options, start);
            var list = new List<VNode>(clip.Frames.Count + 1);
            for (int i = 0; i < clip.Frames.Count; i++)
                list.Add(new VFile($"{clipName}_{i:000000}.dng", modified, new FrameContent(seq, i)));
            if (audio is not null) list.Add(new VFile($"{clipName}.wav", modified, audio));

            string? note = null;
            if (scratch?.StartTimecode is { } ltc && start is null)
                note = $"scratch WAV timecode runs at {ltc.Fps:0.###} fps, the clip at another rate: DNGs keep their own timecode";
            else if (seq.ProfileSource == ProfileSource.Fallback)
                note = $"{clip.Idnt?.CameraName ?? "unknown camera"}: colour matrix unknown, using {seq.Profile.Name}'s";
            else if (clip.Warnings.Count > 0)
                note = clip.Warnings[0];

            return new Entry
            {
                Entries = list,
                ByName = list.ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase),
                Signature = sig, Clip = clip, Sequence = seq, Problem = note,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            clip?.Dispose();
            return Problem(sig, null, $"{Path.GetFileName(mlvPath)}: {ex.Message}", modified);
        }
    }

    static Entry Problem(string sig, MlvClip? clip, string message, DateTime modified)
    {
        clip?.Dispose();
        // A readable explanation in the folder, so a clip that cannot be shown is not just empty.
        var note = new VFile("_why-is-this-empty.txt", modified, new TextContent(message + Environment.NewLine));
        return new Entry
        {
            Entries = new[] { note },
            ByName = new Dictionary<string, VNode>(StringComparer.OrdinalIgnoreCase) { [note.Name] = note },
            Signature = sig, Problem = message,
        };
    }

    static DateTime SafeTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.UtcNow; }
    }

    public void Dispose() => Clear();

    sealed class FrameContent : IVirtualFileContent
    {
        readonly DngSequence _seq;
        readonly int _frame;
        public FrameContent(DngSequence seq, int frame) { _seq = seq; _frame = frame; }
        public long Size => _seq.FileSize;
        public int Read(long offset, Span<byte> destination) => _seq.Read(_frame, offset, destination);
    }
}

/// <summary>A small text file (used to explain why a clip folder is empty).</summary>
public sealed class TextContent : IVirtualFileContent
{
    readonly byte[] _bytes;
    public TextContent(string text) => _bytes = System.Text.Encoding.UTF8.GetBytes(text);
    public long Size => _bytes.Length;
    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= _bytes.Length) return 0;
        int n = (int)Math.Min(destination.Length, _bytes.Length - offset);
        _bytes.AsSpan((int)offset, n).CopyTo(destination);
        return n;
    }
}

/// <summary>Resolves '\'-separated paths against a root.</summary>
public static class VirtualPath
{
    public static VNode? Resolve(VDirectory root, string path)
    {
        VNode node = root;
        foreach (var part in path.Split('\\', '/'))
        {
            if (part.Length == 0) continue;
            if (node is not VDirectory d) return null;
            var next = d.Child(part);
            if (next is null) return null;
            node = next;
        }
        return node;
    }
}
