// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
using System.Buffers.Binary;
using System.Text;

namespace MlvDrive.Tests;

/// <summary>Minimal TIFF reader for checking generated DNG headers.</summary>
public sealed class TiffReader
{
    public sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Value);
    public Dictionary<ushort, Entry> Ifd0 { get; } = new();
    public Dictionary<ushort, Entry> Exif { get; } = new();
    public List<ushort> Ifd0Order { get; } = new();

    public TiffReader(byte[] file)
    {
        if (file[0] != 'I' || file[1] != 'I' || BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2)) != 42)
            throw new InvalidDataException("not a little-endian TIFF");
        ReadIfd(file, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)), Ifd0, Ifd0Order);
        if (Ifd0.TryGetValue(34665, out var exif))
            ReadIfd(file, BinaryPrimitives.ReadUInt32LittleEndian(exif.Value), Exif, new List<ushort>());
    }

    static readonly int[] TypeSize = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8 };

    static void ReadIfd(byte[] f, uint at, Dictionary<ushort, Entry> into, List<ushort> order)
    {
        int n = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan((int)at));
        for (int i = 0; i < n; i++)
        {
            int p = (int)at + 2 + i * 12;
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(p));
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(p + 2));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(p + 4));
            int size = TypeSize[type] * (int)count;
            byte[] value = size <= 4
                ? f.AsSpan(p + 8, size).ToArray()
                : f.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(p + 8)), size).ToArray();
            into[tag] = new Entry(tag, type, count, value);
            order.Add(tag);
        }
    }

    public uint Long(ushort tag) => Ifd0[tag].Type == 3
        ? BinaryPrimitives.ReadUInt16LittleEndian(Ifd0[tag].Value)
        : BinaryPrimitives.ReadUInt32LittleEndian(Ifd0[tag].Value);

    public string Ascii(ushort tag, bool exif = false) =>
        Encoding.ASCII.GetString((exif ? Exif : Ifd0)[tag].Value).TrimEnd('\0');

    public double[] Rationals(ushort tag)
    {
        var e = Ifd0[tag];
        var r = new double[e.Count];
        for (int i = 0; i < r.Length; i++)
        {
            long num = e.Type == 10 ? BinaryPrimitives.ReadInt32LittleEndian(e.Value.AsSpan(i * 8)) : BinaryPrimitives.ReadUInt32LittleEndian(e.Value.AsSpan(i * 8));
            long den = e.Type == 10 ? BinaryPrimitives.ReadInt32LittleEndian(e.Value.AsSpan(i * 8 + 4)) : BinaryPrimitives.ReadUInt32LittleEndian(e.Value.AsSpan(i * 8 + 4));
            r[i] = (double)num / den;
        }
        return r;
    }
}
