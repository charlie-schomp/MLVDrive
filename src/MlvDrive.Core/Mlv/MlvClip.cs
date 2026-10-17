using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace MlvDrive.Core.Mlv;

/// <summary>One video frame's raw payload inside one of the clip's files.</summary>
public readonly record struct FrameRef(int FileIndex, long PayloadOffset, int PayloadSize, uint FrameNumber, ulong Timestamp);

/// <summary>One audio packet's payload inside one of the clip's files.</summary>
public readonly record struct AudioRef(int FileIndex, long PayloadOffset, int PayloadSize, ulong Timestamp);

/// <summary>
/// A recorded clip: the .MLV file plus its spanned chunks (.M00, .M01 ...), indexed once.
///
/// Everything read from the files is treated as untrusted. A block with an impossible
/// size ends the walk of that file (the frames before it stay usable), so a clip that
/// is still being copied, or was cut short by a full card, still opens.
/// </summary>
public sealed class MlvClip : IDisposable
{
    public string MainPath { get; }
    public string BaseName { get; }
    public IReadOnlyList<string> FilePaths => _paths;
    public MlviHeader Header { get; private set; }
    public RawiHeader? Rawi { get; private set; }
    public IdntHeader? Idnt { get; private set; }
    public ExpoHeader? Expo { get; private set; }
    public LensHeader? Lens { get; private set; }
    public RtciHeader? Rtci { get; private set; }
    public WbalHeader? Wbal { get; private set; }
    public WaviHeader? Wavi { get; private set; }
    public IReadOnlyList<FrameRef> Frames => _frames;
    public IReadOnlyList<AudioRef> Audio => _audio;
    /// <summary>Why the clip cannot be shown as DNGs, or null if it can.</summary>
    public string? Unsupported { get; private set; }
    /// <summary>Problems found while indexing that did not stop the clip from opening.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    readonly List<string> _paths = new();
    readonly List<SafeFileHandle> _handles = new();
    readonly List<FrameRef> _frames = new();
    readonly List<AudioRef> _audio = new();
    readonly List<string> _warnings = new();

    MlvClip(string mainPath)
    {
        MainPath = mainPath;
        BaseName = Path.GetFileNameWithoutExtension(mainPath);
    }

    /// <summary>Open and index a clip from its .MLV file. Throws IOException if the main file cannot be read.</summary>
    public static MlvClip Open(string mainPath)
    {
        var clip = new MlvClip(mainPath);
        try
        {
            clip.Index();
            return clip;
        }
        catch
        {
            clip.Dispose();
            throw;
        }
    }

    /// <summary>The spanned chunks of a clip, in order: NAME.M00 ... NAME.M99 next to NAME.MLV.</summary>
    public static IEnumerable<string> FindChunks(string mainPath)
    {
        var dir = Path.GetDirectoryName(mainPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(mainPath);
        for (int i = 0; i < 100; i++)
        {
            var p = Path.Combine(dir, $"{stem}.M{i:00}");
            if (File.Exists(p)) yield return p;
            else yield break;
        }
    }

    void Index()
    {
        _paths.Add(MainPath);
        _paths.AddRange(FindChunks(MainPath));

        var frames = new Dictionary<uint, FrameRef>();
        for (int fi = 0; fi < _paths.Count; fi++)
        {
            SafeFileHandle h;
            try
            {
                h = File.OpenHandle(_paths[fi], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            }
            catch (IOException) when (fi > 0)
            {
                _warnings.Add($"{Path.GetFileName(_paths[fi])}: cannot be opened, frames from it are missing");
                _paths.RemoveRange(fi, _paths.Count - fi);
                break;
            }
            _handles.Add(h);
            WalkFile(fi, h, frames);
        }

        if (!Header.IsRaw)
            Unsupported = "not a raw video clip";
        else if (Header.IsLzma)
            Unsupported = "LZMA-compressed clips are not supported";
        else if (Header.IsLj92)
            Unsupported = "lossless (LJ92) clips are not supported yet";
        else if (Rawi is null)
            Unsupported = "no RAWI block (frame format unknown)";
        else if (Rawi.Value.BitsPerPixel is < 8 or > 16 || Rawi.Value.XRes <= 0 || Rawi.Value.YRes <= 0)
            Unsupported = $"unexpected frame format {Rawi.Value.XRes}x{Rawi.Value.YRes} {Rawi.Value.BitsPerPixel}-bit";

        if (Unsupported is null)
        {
            long need = PackedFrameSize;
            foreach (var f in frames.Values.OrderBy(f => f.FrameNumber))
            {
                if (f.PayloadSize >= need) _frames.Add(f);
                else _warnings.Add($"frame {f.FrameNumber} is short ({f.PayloadSize} of {need} bytes), skipped");
            }
        }
        _audio.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }

    /// <summary>Bytes of packed pixel data in one frame.</summary>
    public long PackedFrameSize => Rawi is { } r ? (long)r.XRes * r.YRes * r.BitsPerPixel / 8 : 0;

    void WalkFile(int fileIndex, SafeFileHandle h, Dictionary<uint, FrameRef> frames)
    {
        long length = RandomAccess.GetLength(h);
        var name = Path.GetFileName(_paths[fileIndex]);
        Span<byte> hdr = stackalloc byte[MlvFormat.VidfHeaderSize];
        byte[] block = new byte[256];
        long pos = 0;
        bool first = true;

        while (pos + MlvFormat.BlockHeaderSize <= length)
        {
            int got = RandomAccess.Read(h, hdr[..MlvFormat.BlockHeaderSize], pos);
            if (got < MlvFormat.BlockHeaderSize) break;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(hdr[4..]);
            string type = System.Text.Encoding.ASCII.GetString(hdr[..4]);
            if (first && type != "MLVI")
            {
                if (fileIndex == 0) throw new InvalidDataException($"{name} is not an MLV file");
                _warnings.Add($"{name}: no MLV header, ignored");
                break;
            }
            if (size < MlvFormat.BlockHeaderSize || pos + size > length || !IsBlockType(hdr[..4]))
            {
                if (pos + size > length && size >= MlvFormat.BlockHeaderSize && IsBlockType(hdr[..4]))
                    _warnings.Add($"{name}: ends inside a {type} block (file incomplete?)");
                else
                    _warnings.Add($"{name}: unreadable data at byte {pos}, rest of file ignored");
                break;
            }

            first = false;

            ulong ts = BinaryPrimitives.ReadUInt64LittleEndian(hdr[8..]);
            switch (type)
            {
                case "VIDF":
                {
                    if (size < MlvFormat.VidfHeaderSize) break;
                    RandomAccess.Read(h, hdr, pos);
                    uint frameNumber = BinaryPrimitives.ReadUInt32LittleEndian(hdr[16..]);
                    uint frameSpace = BinaryPrimitives.ReadUInt32LittleEndian(hdr[28..]);
                    long payloadOffset = pos + MlvFormat.VidfHeaderSize + frameSpace;
                    long payloadSize = size - MlvFormat.VidfHeaderSize - (long)frameSpace;
                    if (frameSpace > size || payloadSize <= 0 || payloadSize > int.MaxValue) break;
                    frames.TryAdd(frameNumber, new FrameRef(fileIndex, payloadOffset, (int)payloadSize, frameNumber, ts));
                    break;
                }
                case "AUDF":
                {
                    if (size < MlvFormat.AudfHeaderSize) break;
                    RandomAccess.Read(h, hdr[..MlvFormat.AudfHeaderSize], pos);
                    uint frameSpace = BinaryPrimitives.ReadUInt32LittleEndian(hdr[20..]);
                    long payloadOffset = pos + MlvFormat.AudfHeaderSize + frameSpace;
                    long payloadSize = size - MlvFormat.AudfHeaderSize - (long)frameSpace;
                    if (frameSpace > size || payloadSize <= 0 || payloadSize > int.MaxValue) break;
                    _audio.Add(new AudioRef(fileIndex, payloadOffset, (int)payloadSize, ts));
                    break;
                }
                default:
                {
                    int want = (int)Math.Min(size, block.Length);
                    var b = block.AsSpan(0, want);
                    if (RandomAccess.Read(h, b, pos) < want) break;
                    ReadMetadata(fileIndex, type, b, (int)size);
                    break;
                }
            }
            pos += size;
        }
    }

    void ReadMetadata(int fileIndex, string type, ReadOnlySpan<byte> b, int size)
    {
        // Each parser needs the block's full fixed part; a shorter block is ignored.
        switch (type)
        {
            case "MLVI" when size >= MlviHeader.Size:
                if (fileIndex == 0) Header = MlviHeader.Parse(b);
                break;
            case "RAWI" when size >= RawiHeader.Size:
                Rawi ??= RawiHeader.Parse(b);
                break;
            case "IDNT" when size >= IdntHeader.Size:
                Idnt ??= IdntHeader.Parse(b);
                break;
            case "EXPO" when size >= ExpoHeader.Size:
                Expo ??= ExpoHeader.Parse(b);
                break;
            case "LENS" when size >= LensHeader.Size:
                Lens ??= LensHeader.Parse(b);
                break;
            case "RTCI" when size >= RtciHeader.Size:
                Rtci ??= RtciHeader.Parse(b);
                break;
            case "WBAL" when size >= WbalHeader.Size:
                Wbal ??= WbalHeader.Parse(b);
                break;
            case "WAVI" when size >= WaviHeader.Size:
                Wavi ??= WaviHeader.Parse(b);
                break;
        }
    }

    static bool IsBlockType(ReadOnlySpan<byte> t)
    {
        foreach (var c in t)
            if (!(c is >= (byte)'A' and <= (byte)'Z' || c is >= (byte)'0' and <= (byte)'9' || c is >= (byte)'a' and <= (byte)'z'))
                return false;
        return true;
    }

    /// <summary>Frames per second from the header, or the override if one is given.</summary>
    public (int Num, int Den) FrameRate(double? overrideFps = null)
    {
        if (overrideFps is > 0)
            return ((int)Math.Round(overrideFps.Value * 1000), 1000);
        if (Header.SourceFpsNom > 0 && Header.SourceFpsDenom > 0 && Header.SourceFpsNom <= int.MaxValue && Header.SourceFpsDenom <= int.MaxValue)
            return ((int)Header.SourceFpsNom, (int)Header.SourceFpsDenom);
        return (24000, 1001);
    }

    /// <summary>Read raw bytes from one of the clip's files at an absolute position.</summary>
    public int ReadFile(int fileIndex, long offset, Span<byte> destination)
    {
        var handles = _handles;
        if (_disposed || (uint)fileIndex >= (uint)handles.Count) throw new IOException($"{BaseName}: clip was closed");
        var h = handles[fileIndex];
        if (h.IsClosed) throw new IOException($"{BaseName}: clip was closed");
        int total = 0;
        while (total < destination.Length)
        {
            int n = RandomAccess.Read(h, destination[total..], offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    volatile bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        foreach (var h in _handles) h.Dispose();
    }
}
