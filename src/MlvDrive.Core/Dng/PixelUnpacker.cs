// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
using System.Buffers.Binary;

namespace MlvDrive.Core.Dng;

/// <summary>
/// Magic Lantern packs raw pixels as one bitstream of 16-bit little-endian words, most
/// significant bit first, at 10, 12 or 14 bits per pixel (any depth 8-16 works here).
/// </summary>
public static class PixelUnpacker
{
    /// <summary>First 16-bit word holding any bit of pixel <paramref name="pixel"/>.</summary>
    public static long FirstWord(long pixel, int bpp) => pixel * bpp >> 4;

    /// <summary>Words to read to unpack pixels [first, first+count), including the one-word lookahead.</summary>
    public static long WordSpan(long first, int count, int bpp) =>
        ((first + count) * bpp + 15 >> 4) - FirstWord(first, bpp) + 1;

    /// <summary>
    /// Unpack <paramref name="count"/> pixels starting at absolute pixel index
    /// <paramref name="first"/>. <paramref name="packed"/> must start at
    /// <see cref="FirstWord"/>(first) and hold <see cref="WordSpan"/> words (zero-pad past the data).
    /// </summary>
    public static void Unpack(ReadOnlySpan<byte> packed, int bpp, long first, int count, Span<ushort> output)
    {
        if (bpp is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(bpp));
        if (output.Length < count) throw new ArgumentException("output too small");
        long baseBit = FirstWord(first, bpp) << 4;
        uint mask = (1u << bpp) - 1;
        long bit = first * bpp - baseBit;
        for (int i = 0; i < count; i++, bit += bpp)
        {
            int byteIndex = (int)(bit >> 4) << 1;
            uint hi = BinaryPrimitives.ReadUInt16LittleEndian(packed[byteIndex..]);
            uint lo = BinaryPrimitives.ReadUInt16LittleEndian(packed[(byteIndex + 2)..]);
            uint v = hi << 16 | lo;
            int shift = 32 - (int)(bit & 15) - bpp;
            output[i] = (ushort)(v >> shift & mask);
        }
    }

    /// <summary>Pack pixels into Magic Lantern's bitstream (used by tests and tools).</summary>
    public static byte[] Pack(ReadOnlySpan<ushort> pixels, int bpp)
    {
        long bits = (long)pixels.Length * bpp;
        var words = new ushort[(bits + 15) / 16 + 1];
        long bit = 0;
        foreach (var px in pixels)
        {
            for (int b = bpp - 1; b >= 0; b--, bit++)
            {
                if ((px >> b & 1) != 0)
                    words[bit >> 4] |= (ushort)(1 << (15 - (int)(bit & 15)));
            }
        }
        var bytes = new byte[(bits + 15) / 16 * 2];
        for (int i = 0; i < bytes.Length / 2; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), words[i]);
        return bytes;
    }
}
