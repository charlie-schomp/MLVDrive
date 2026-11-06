// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
using System.Buffers.Binary;
using System.Text;

namespace MlvDrive.Core.Dng;

/// <summary>TIFF field types used by DNG.</summary>
public enum TiffType : ushort
{
    Byte = 1, Ascii = 2, Short = 3, Long = 4, Rational = 5, Undefined = 7, SRational = 10,
}

/// <summary>One IFD under construction. Entries are written sorted by tag, as TIFF requires.</summary>
public sealed class TiffIfd
{
    internal readonly SortedDictionary<ushort, (TiffType Type, uint Count, byte[] Value)> Entries = new();

    public TiffIfd Add(ushort tag, TiffType type, uint count, byte[] value)
    {
        Entries[tag] = (type, count, value);
        return this;
    }

    public TiffIfd Short(ushort tag, params ushort[] v)
    {
        var b = new byte[v.Length * 2];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), v[i]);
        return Add(tag, TiffType.Short, (uint)v.Length, b);
    }

    public TiffIfd Long(ushort tag, params uint[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return Add(tag, TiffType.Long, (uint)v.Length, b);
    }

    public TiffIfd Bytes(ushort tag, params byte[] v) => Add(tag, TiffType.Byte, (uint)v.Length, v);

    public TiffIfd Undefined(ushort tag, byte[] v) => Add(tag, TiffType.Undefined, (uint)v.Length, v);

    public TiffIfd Ascii(ushort tag, string s)
    {
        var b = Encoding.ASCII.GetBytes(Sanitize(s) + "\0");
        return Add(tag, TiffType.Ascii, (uint)b.Length, b);
    }

    public TiffIfd Rational(ushort tag, params (uint Num, uint Den)[] v)
    {
        var b = new byte[v.Length * 8];
        for (int i = 0; i < v.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 8), v[i].Num);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 8 + 4), v[i].Den == 0 ? 1 : v[i].Den);
        }
        return Add(tag, TiffType.Rational, (uint)v.Length, b);
    }

    public TiffIfd SRational(ushort tag, params (int Num, int Den)[] v)
    {
        var b = new byte[v.Length * 8];
        for (int i = 0; i < v.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 8), v[i].Num);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 8 + 4), v[i].Den == 0 ? 1 : v[i].Den);
        }
        return Add(tag, TiffType.SRational, (uint)v.Length, b);
    }

    /// <summary>A 3x3 matrix scaled by 10000 as nine signed rationals.</summary>
    public TiffIfd Matrix(ushort tag, int[] m)
    {
        var v = new (int, int)[9];
        for (int i = 0; i < 9; i++) v[i] = (m[i], 10000);
        return SRational(tag, v);
    }

    static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c is >= ' ' and <= '~' ? c : '?');
        return sb.ToString();
    }

    internal int DirectorySize => 2 + Entries.Count * 12 + 4;
}

/// <summary>
/// Writes a little-endian TIFF header with IFD0 and an optional EXIF sub-IFD into a
/// fixed-size buffer; out-of-line values follow the directories. IFD0 can point at the
/// EXIF IFD with the <see cref="ExifIfdTag"/> entry, which is filled in here.
/// </summary>
public static class TiffWriter
{
    public const ushort ExifIfdTag = 34665;

    public static void Write(Span<byte> buffer, TiffIfd ifd0, TiffIfd? exif)
    {
        buffer.Clear();
        buffer[0] = (byte)'I'; buffer[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], 8);

        uint ifd0Offset = 8;
        if (exif is not null) ifd0.Long(ExifIfdTag, 0);           // placeholder, so the count is right
        uint exifOffset = ifd0Offset + (uint)ifd0.DirectorySize;
        if (exif is not null) ifd0.Long(ExifIfdTag, exifOffset);
        uint data = exifOffset + (uint)(exif?.DirectorySize ?? 0);

        data = WriteIfd(buffer, ifd0, ifd0Offset, data);
        if (exif is not null) WriteIfd(buffer, exif, exifOffset, data);
    }

    static uint WriteIfd(Span<byte> buffer, TiffIfd ifd, uint at, uint data)
    {
        int p = (int)at;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[p..], (ushort)ifd.Entries.Count);
        p += 2;
        foreach (var (tag, (type, count, value)) in ifd.Entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[p..], tag);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[(p + 2)..], (ushort)type);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[(p + 4)..], count);
            if (value.Length <= 4)
            {
                value.CopyTo(buffer[(p + 8)..]);
            }
            else
            {
                if (data + value.Length > buffer.Length) throw new InvalidOperationException("DNG header does not fit its reserved size");
                BinaryPrimitives.WriteUInt32LittleEndian(buffer[(p + 8)..], data);
                value.CopyTo(buffer[(int)data..]);
                data += (uint)value.Length;
                if ((data & 1) != 0) data++;                         // word-align values
            }
            p += 12;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[p..], 0); // no next IFD
        return data;
    }
}
