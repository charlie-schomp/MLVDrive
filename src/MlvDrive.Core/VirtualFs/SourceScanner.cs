namespace MlvDrive.Core.VirtualFs;

/// <summary>A place to look for clips, shown on the virtual drive under <see cref="DisplayName"/>.</summary>
public sealed record ClipSource(string DisplayName, string RootPath);

/// <summary>
/// Builds the virtual drive's tree: one folder per source, mirroring the source's folders
/// but keeping only those that lead to .MLV clips; each clip becomes a folder of DNGs.
/// Scanning only lists directories (clips are indexed lazily), so it is cheap even for
/// big drives, and it never descends into system or hidden folders.
/// </summary>
public static class SourceScanner
{
    public const int DefaultMaxDepth = 8;

    static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "RECYCLER", ".Trashes", ".Spotlight-V100", ".fseventsd",
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$WinREAgent", "node_modules",
    };

    public static StaticDirectory Build(IEnumerable<ClipSource> sources, ClipCache cache, int maxDepth = DefaultMaxDepth,
                                        CancellationToken cancel = default)
    {
        var root = new StaticDirectory("", DateTime.UtcNow);
        foreach (var source in sources)
        {
            cancel.ThrowIfCancellationRequested();
            if (!Directory.Exists(source.RootPath)) continue;
            var clips = FindClips(source.RootPath, maxDepth, cancel).ToList();
            if (clips.Count == 0) continue;

            var top = (StaticDirectory)root.Add(n => new StaticDirectory(n, SafeTime(source.RootPath)), SafeName(source.DisplayName));
            foreach (var mlv in clips)
            {
                var rel = Path.GetRelativePath(source.RootPath, Path.GetDirectoryName(mlv)!);
                var dir = top;
                if (rel != ".")
                    foreach (var part in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        dir = dir.GetOrAddDirectory(part, SafeTime(mlv));
                var stem = Path.GetFileNameWithoutExtension(mlv);
                dir.Add(n => new ClipDirectory(n, mlv, SafeTime(mlv), cache), stem);
            }
            // every clip's slate, ISO, shutter and lens, for Resolve's Import Metadata
            // (headers only - the frames are not indexed for this)
            top.Add(n => new VFile(n, DateTime.UtcNow, new TextContent(Mlv.MetadataCsv.Build(clips))), Mlv.MetadataCsv.FileName);
            SortRecursive(top);
        }
        root.SortChildren();
        return root;
    }

    /// <summary>Every .MLV under <paramref name="root"/>, at most <paramref name="maxDepth"/> folders deep.</summary>
    public static IEnumerable<string> FindClips(string root, int maxDepth = DefaultMaxDepth, CancellationToken cancel = default)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (dir, depth) = pending.Pop();
            IEnumerable<string> files, subdirs;
            try
            {
                // not "._CLIP.MLV": the sidecars macOS leaves on exFAT cards are not clips
                files = Directory.EnumerateFiles(dir, "*.*").Where(f => string.Equals(Path.GetExtension(f), ".mlv", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith('.')).ToList();
                subdirs = depth < maxDepth ? Directory.EnumerateDirectories(dir).ToList() : Enumerable.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var f in files) yield return f;
            foreach (var d in subdirs)
            {
                var name = Path.GetFileName(d);
                if (SkipNames.Contains(name) || name.StartsWith('.')) continue;
                try
                {
                    var attr = File.GetAttributes(d);
                    if ((attr & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                pending.Push((d, depth + 1));
            }
        }
    }

    static void SortRecursive(StaticDirectory d)
    {
        d.SortChildren();
        foreach (var c in d.Children())
            if (c is StaticDirectory s) SortRecursive(s);
    }

    /// <summary>A name safe for a path component on every platform.</summary>
    public static string SafeName(string name)
    {
        var bad = new HashSet<char>(Path.GetInvalidFileNameChars()) { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        var chars = name.Trim().Select(c => bad.Contains(c) || c < ' ' ? '_' : c).ToArray();
        var s = new string(chars).TrimEnd('.', ' ');
        return s.Length == 0 ? "Drive" : s;
    }

    static DateTime SafeTime(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.UtcNow; }
    }
}
