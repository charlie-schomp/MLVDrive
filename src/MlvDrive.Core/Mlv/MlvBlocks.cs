// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 charlie-schomp
using System.Buffers.Binary;
using System.Text;

namespace MlvDrive.Core.Mlv;

/// <summary>
/// MLV block layouts (Magic Lantern Video, format v2.0). All fields are little-endian
/// and packed; offsets are from the start of the block, including its 16-byte header
/// (4-char type, uint32 size, uint64 timestamp in microseconds).
/// </summary>
public static class MlvFormat
{
    public const int BlockHeaderSize = 16;
    public const int VidfHeaderSize = 32;
    public const int AudfHeaderSize = 24;

    public const ushort VideoClassRaw = 0x01;
    public const ushort VideoClassFlagLj92 = 0x20;   // current Magic Lantern
    public const ushort VideoClassFlagLj92Old = 0x100;
    public const ushort VideoClassFlagLzma = 0x80;
    public const ushort VideoClassFlagDelta = 0x40;

    public static string ReadAscii(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end < 0) end = bytes.Length;
        var s = Encoding.Latin1.GetString(bytes[..end]);
        return s.Trim();
    }
}

/// <summary>MLVI: the file header at the start of every .MLV / .Mxx chunk.</summary>
public readonly record struct MlviHeader(
    ulong FileGuid, ushort FileNum, ushort FileCount, uint FileFlags,
    ushort VideoClass, ushort AudioClass, uint VideoFrameCount, uint AudioFrameCount,
    uint SourceFpsNom, uint SourceFpsDenom)
{
    public const int Size = 52;

    public static MlviHeader Parse(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(b[16..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[24..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[26..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[28..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[32..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[34..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[36..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[40..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[44..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[48..]));

    public bool IsRaw => (VideoClass & 0x0F) == MlvFormat.VideoClassRaw;
    public bool IsLj92 => (VideoClass & (MlvFormat.VideoClassFlagLj92 | MlvFormat.VideoClassFlagLj92Old)) != 0;
    public bool IsLzma => (VideoClass & MlvFormat.VideoClassFlagLzma) != 0;
}

/// <summary>
/// RAWI: frame geometry plus Magic Lantern's raw_info structure (a 32-bit layout of
/// raw.c's struct raw_info, starting 4 bytes into the block payload).
/// </summary>
public readonly record struct RawiHeader(
    int XRes, int YRes, int BufferHeight, int BufferWidth, int Pitch, int BitsPerPixel,
    int BlackLevel, int WhiteLevel, int CropOriginX, int CropOriginY, int CropWidth, int CropHeight,
    int ActiveY1, int ActiveX1, int ActiveY2, int ActiveX2,
    int ExposureBiasNum, int ExposureBiasDen, int CfaPattern, int CalibrationIlluminant1,
    int[] ColorMatrix1)
{
    public const int Size = 16 + 4 + 160;

    public static RawiHeader Parse(ReadOnlySpan<byte> b)
    {
        int x = BinaryPrimitives.ReadUInt16LittleEndian(b[16..]);
        int y = BinaryPrimitives.ReadUInt16LittleEndian(b[18..]);
        var ri = new int[40];                                       // raw_info as 32-bit words
        for (int k = 0; k < ri.Length; k++) ri[k] = BinaryPrimitives.ReadInt32LittleEndian(b[(20 + k * 4)..]);
        int I(int index) => ri[index];
        var cm = new int[18];
        for (int i = 0; i < 18; i++) cm[i] = I(21 + i);
        return new RawiHeader(
            x, y, I(2), I(3), I(4), I(6), I(7), I(8),
            I(9), I(10), I(11), I(12),
            I(13), I(14), I(15), I(16),
            I(17), I(18), I(19), I(20), cm);
    }
}

public readonly record struct IdntHeader(string CameraName, uint CameraModel, string CameraSerial)
{
    public const int Size = 16 + 32 + 4 + 32;

    public static IdntHeader Parse(ReadOnlySpan<byte> b) => new(
        MlvFormat.ReadAscii(b.Slice(16, 32)),
        BinaryPrimitives.ReadUInt32LittleEndian(b[48..]),
        MlvFormat.ReadAscii(b.Slice(52, 32)));
}

public readonly record struct ExpoHeader(uint IsoMode, uint IsoValue, uint IsoAnalog, uint DigitalGain, ulong ShutterMicroseconds)
{
    public const int Size = 16 + 24;

    public static ExpoHeader Parse(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(b[16..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[20..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[24..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[28..]),
        BinaryPrimitives.ReadUInt64LittleEndian(b[32..]));
}

/// <summary>DISO: whether the dual_iso module was active when the clip was recorded.</summary>
public readonly record struct DisoHeader(uint DualMode, uint IsoValue)
{
    public const int Size = 16 + 8;

    public static DisoHeader Parse(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(b[16..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[20..]));
}

public readonly record struct LensHeader(ushort FocalLength, ushort FocalDistance, ushort ApertureX100, string LensName)
{
    public const int Size = 16 + 16 + 64;

    public static LensHeader Parse(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(b[16..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[18..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[20..]),
        MlvFormat.ReadAscii(b.Slice(32, 32)));
}

public readonly record struct RtciHeader(int Second, int Minute, int Hour, int Day, int Month0, int YearSince1900)
{
    public const int Size = 16 + 28;

    public static RtciHeader Parse(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(b[16..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[18..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[20..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[22..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[24..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[26..]));

    public DateTime? ToDateTime()
    {
        try { return new DateTime(1900 + YearSince1900, Month0 + 1, Day, Hour, Minute, Second); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

public readonly record struct WbalHeader(uint Mode, uint Kelvin, uint GainR, uint GainG, uint GainB)
{
    public const int Size = 16 + 28;

    public static WbalHeader Parse(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(b[16..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[20..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[24..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[28..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[32..]));
}

public readonly record struct WaviHeader(ushort Format, ushort Channels, uint SamplingRate, uint BytesPerSecond, ushort BlockAlign, ushort BitsPerSample)
{
    public const int Size = 16 + 16;

    public static WaviHeader Parse(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(b[16..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[18..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[20..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[24..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[28..]),
        BinaryPrimitives.ReadUInt16LittleEndian(b[30..]));

    public ushort EffectiveBlockAlign =>
        BlockAlign != 0 ? BlockAlign : (ushort)Math.Max(1, Math.Max(1, (int)Channels) * Math.Max(1, BitsPerSample / 8));
}
