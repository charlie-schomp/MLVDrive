// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
using System.Globalization;
using System.Text;

namespace MlvDrive.Core.Mlv;

/// <summary>
/// Slate metadata written by the 6D Mark II port (mlv_lite "Clip info", v119 on): an INFO
/// block of space-separated key=value pairs, "camera=A reel=001 scene=12 shot=B take=3".
/// Any field may be missing.
/// </summary>
public sealed record ClipSlate(string? Camera, int? Reel, int? Scene, string? Shot, int? Take)
{
    /// <summary>The slate in an INFO block's text, or null if the text is not one.</summary>
    public static ClipSlate? Parse(string text)
    {
        if (!text.Contains("take=", StringComparison.Ordinal) && !text.Contains("scene=", StringComparison.Ordinal))
            return null;
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.TrimEnd('\0').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) kv[part[..eq]] = part[(eq + 1)..].TrimEnd('\0');
        }
        static int? Num(Dictionary<string, string> d, string k) =>
            d.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
        static string? Str(Dictionary<string, string> d, string k) =>
            d.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        return new ClipSlate(Str(kv, "camera"), Num(kv, "reel"), Num(kv, "scene"), Str(kv, "shot"), Num(kv, "take"));
    }

    /// <summary>The camera's roll, as editors write it: camera letter and reel, "A001".</summary>
    public string? RollCard => Reel is { } r ? $"{Camera ?? ""}{r:000}" : null;
}

/// <summary>
/// The header blocks of one clip, read without indexing its frames: everything before the
/// first VIDF (and at most the first megabyte), which is where mlv_lite writes them.
/// </summary>
public sealed record ClipHeaderInfo(ClipSlate? Slate, ExpoHeader? Expo, LensHeader? Lens, IdntHeader? Idnt)
{
    const int Limit = 1 << 20;

    public static ClipHeaderInfo Read(string mlvPath)
    {
        ClipSlate? slate = null; ExpoHeader? expo = null; LensHeader? lens = null; IdntHeader? idnt = null;
        try
        {
            using var f = new FileStream(mlvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hdr = new byte[MlvFormat.BlockHeaderSize];
            long pos = 0;
            while (pos < Limit && pos + hdr.Length <= f.Length)
            {
                f.Position = pos;
                if (f.Read(hdr, 0, hdr.Length) < hdr.Length) break;
                var type = Encoding.ASCII.GetString(hdr, 0, 4);
                uint size = BitConverter.ToUInt32(hdr, 4);
                if (size < MlvFormat.BlockHeaderSize || type == "VIDF") break;
                if (size <= 4096 && type is "INFO" or "EXPO" or "LENS" or "IDNT")
                {
                    var b = new byte[size];
                    f.Position = pos;
                    if (f.Read(b, 0, b.Length) < b.Length) break;
                    switch (type)
                    {
                        case "INFO":
                            slate ??= ClipSlate.Parse(Encoding.ASCII.GetString(b, MlvFormat.BlockHeaderSize, b.Length - MlvFormat.BlockHeaderSize));
                            break;
                        case "EXPO" when b.Length >= ExpoHeader.Size: expo ??= ExpoHeader.Parse(b); break;
                        case "LENS" when b.Length >= LensHeader.Size: lens ??= LensHeader.Parse(b); break;
                        case "IDNT" when b.Length >= IdntHeader.Size: idnt ??= IdntHeader.Parse(b); break;
                    }
                }
                pos += size;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new ClipHeaderInfo(slate, expo, lens, idnt);
    }
}

/// <summary>
/// A CSV of every clip under a source, for DaVinci Resolve's Media Pool "Import Metadata",
/// matched by reel name (which the DNGs carry as the clip's name). The column names are
/// Resolve's own metadata fields.
/// </summary>
public static class MetadataCsv
{
    public const string FileName = "_metadata.csv";

    public static string Build(IEnumerable<string> mlvPaths)
    {
        var sb = new StringBuilder();
        sb.Append("Reel Name,Scene,Shot,Take,Camera #,Roll Card #,ISO,Shutter,Lens Type,Camera Serial #\r\n");
        foreach (var path in mlvPaths.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            sb.Append(Row(Path.GetFileNameWithoutExtension(path), ClipHeaderInfo.Read(path))).Append("\r\n");
        return sb.ToString();
    }

    public static string Row(string reelName, ClipHeaderInfo h)
    {
        var s = h.Slate;
        string shutter = h.Expo is { ShutterMicroseconds: > 0 } e
            ? $"1/{Math.Round(1e6 / e.ShutterMicroseconds).ToString(CultureInfo.InvariantCulture)}" : "";
        string[] cells =
        {
            reelName,
            s?.Scene?.ToString(CultureInfo.InvariantCulture) ?? "",
            s?.Shot ?? "",
            s?.Take?.ToString(CultureInfo.InvariantCulture) ?? "",
            s?.Camera ?? "",
            s?.RollCard ?? "",
            h.Expo is { IsoValue: > 0 } x ? x.IsoValue.ToString(CultureInfo.InvariantCulture) : "",
            shutter,
            h.Lens?.LensName ?? "",
            h.Idnt?.CameraSerial ?? "",
        };
        return string.Join(",", cells.Select(Quote));
    }

    static string Quote(string v) =>
        v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
