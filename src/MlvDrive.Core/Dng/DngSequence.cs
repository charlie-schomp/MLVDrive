using System.Buffers;
using System.Reflection;
using MlvDrive.Core.Audio;
using MlvDrive.Core.Mlv;

namespace MlvDrive.Core.Dng;

/// <summary>Options that change what the DNGs say (not the pixels).</summary>
/// <param name="ScratchLatencyMs">Calibration for the 6D Mark II's scratch WAV: how much later its
/// first sample comes than the moment the camera stamped (see <see cref="ScratchWav"/>).</param>
public sealed record DngOptions(double? FrameRateOverride = null, double ScratchLatencyMs = 0);

/// <summary>
/// A clip presented as a CinemaDNG sequence: one virtual DNG per frame, each a fixed
/// 64 kB header (as MLVFS uses, which Resolve accepts) followed by the frame unpacked to
/// 16 bits per pixel. Any byte range of any frame can be read without building the rest.
/// </summary>
public sealed class DngSequence
{
    public const int HeaderSize = 65536;

    static readonly string Software =
        "MLV Drive " + (typeof(DngSequence).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev").Split('+')[0];

    readonly MlvClip _clip;
    readonly RawiHeader _rawi;
    readonly DngOptions _options;
    readonly object _cacheLock = new();
    readonly (int Frame, byte[] Header)[] _headerCache = new (int, byte[])[4];
    int _headerCacheNext;

    public CameraProfile Profile { get; }
    public ProfileSource ProfileSource { get; }
    public int FrameCount => _clip.Frames.Count;
    public long ImageSize => (long)_rawi.XRes * _rawi.YRes * 2;
    public long FileSize => HeaderSize + ImageSize;
    public MlvClip Clip => _clip;

    public DngSequence(MlvClip clip, DngOptions? options = null)
    {
        if (clip.Unsupported is { } why) throw new NotSupportedException(why);
        _clip = clip;
        _rawi = clip.Rawi!.Value;
        _options = options ?? new DngOptions();
        (Profile, ProfileSource) = CameraProfiles.Resolve(clip.Idnt?.CameraName, _rawi.ColorMatrix1, _rawi.CalibrationIlluminant1);
        for (int i = 0; i < _headerCache.Length; i++) _headerCache[i] = (-1, Array.Empty<byte>());
    }

    /// <summary>Read part of frame <paramref name="frame"/>'s DNG. Returns the bytes written.</summary>
    public int Read(int frame, long offset, Span<byte> destination)
    {
        if ((uint)frame >= (uint)FrameCount || offset < 0 || offset >= FileSize) return 0;
        int length = (int)Math.Min(destination.Length, FileSize - offset);
        int done = 0;

        if (offset < HeaderSize)
        {
            var header = GetHeader(frame);
            int n = (int)Math.Min(length, HeaderSize - offset);
            header.AsSpan((int)offset, n).CopyTo(destination);
            done = n;
        }
        if (done < length)
            ReadPixels(frame, offset + done - HeaderSize, destination[done..length]);
        return length;
    }

    void ReadPixels(int frame, long imageOffset, Span<byte> destination)
    {
        int bpp = _rawi.BitsPerPixel;
        var f = _clip.Frames[frame];
        long firstPixel = imageOffset / 2;
        long lastPixelExcl = (imageOffset + destination.Length + 1) / 2;
        const int ChunkPixels = 1 << 16;

        var words = ArrayPool<byte>.Shared.Rent((int)PixelUnpacker.WordSpan(0, ChunkPixels, bpp) * 2 + 8);
        var pixels = ArrayPool<ushort>.Shared.Rent(ChunkPixels);
        try
        {
            int written = 0;
            int skip = (int)(imageOffset & 1);                       // odd offset: start mid-pixel
            for (long p = firstPixel; p < lastPixelExcl; p += ChunkPixels)
            {
                int count = (int)Math.Min(ChunkPixels, lastPixelExcl - p);
                long wordStart = PixelUnpacker.FirstWord(p, bpp);
                int byteCount = (int)PixelUnpacker.WordSpan(p, count, bpp) * 2;
                var packed = words.AsSpan(0, byteCount);
                long available = Math.Max(0, Math.Min(byteCount, f.PayloadSize - wordStart * 2));
                int got = available > 0 ? _clip.ReadFile(f.FileIndex, f.PayloadOffset + wordStart * 2, packed[..(int)available]) : 0;
                packed[got..].Clear();

                PixelUnpacker.Unpack(packed, bpp, p, count, pixels);
                var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan(0, count));
                int take = Math.Min(bytes.Length - skip, destination.Length - written);
                bytes.Slice(skip, take).CopyTo(destination[written..]);
                written += take;
                skip = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(words);
            ArrayPool<ushort>.Shared.Return(pixels);
        }
    }

    byte[] GetHeader(int frame)
    {
        lock (_cacheLock)
        {
            foreach (var (f, h) in _headerCache)
                if (f == frame) return h;
        }
        var header = BuildHeader(frame);
        lock (_cacheLock)
        {
            _headerCache[_headerCacheNext] = (frame, header);
            _headerCacheNext = (_headerCacheNext + 1) % _headerCache.Length;
        }
        return header;
    }

    /// <summary>The DNG header (IFD0 + EXIF) for one frame.</summary>
    public byte[] BuildHeader(int frame)
    {
        var r = _rawi;
        var clip = _clip;
        string model = clip.Idnt?.CameraName is { Length: > 0 } n ? n : "Magic Lantern camera";
        string make = model.Split(' ')[0];
        var (fpsNum, fpsDen) = clip.FrameRate(_options.FrameRateOverride);

        // Active area / crop: Magic Lantern's raw_info describes the camera's whole buffer;
        // a recorded frame is usually a crop of it, so fall back to the full frame unless
        // the active area actually lies inside what was recorded.
        int top = 0, left = 0, bottom = r.YRes, right = r.XRes;
        if (r.ActiveX2 > r.ActiveX1 && r.ActiveY2 > r.ActiveY1 && r.ActiveX1 >= 0 && r.ActiveY1 >= 0 &&
            r.ActiveX2 <= r.XRes && r.ActiveY2 <= r.YRes)
        {
            (top, left, bottom, right) = (r.ActiveY1, r.ActiveX1, r.ActiveY2, r.ActiveX2);
        }

        // MLVFS's line-skipping heuristic: a very wide, short buffer outside crop mode is
        // 5x3 squeezed footage from older bodies, shown at its true aspect.
        (uint, uint) scaleH = (1, 1), scaleV = (1, 1);
        double rawW = r.ActiveX2 - r.ActiveX1, rawH = r.ActiveY2 - r.ActiveY1;
        if (rawH > 0 && rawW / rawH > 2.0 && rawH <= 720) scaleV = (5, 3);

        // CFA layout: Magic Lantern stores it as four bytes (0 red, 1 green, 2 blue).
        var cfa = BitConverter.GetBytes(r.CfaPattern);
        if (cfa.Any(b => b > 2) || r.CfaPattern == 0) cfa = new byte[] { 0, 1, 1, 2 };

        var ifd0 = new TiffIfd()
            .Long(254, 0)                                            // NewSubFileType: main image
            .Long(256, (uint)r.XRes)
            .Long(257, (uint)r.YRes)
            .Short(258, 16)                                          // BitsPerSample
            .Short(259, 1)                                           // Compression: none
            .Short(262, 32803)                                       // PhotometricInterpretation: CFA
            .Ascii(271, make)
            .Ascii(272, model)
            .Long(273, HeaderSize)                                   // StripOffsets
            .Short(274, 1)                                           // Orientation
            .Short(277, 1)                                           // SamplesPerPixel
            .Long(278, (uint)r.YRes)                                 // RowsPerStrip
            .Long(279, (uint)ImageSize)                              // StripByteCounts
            .Short(284, 1)                                           // PlanarConfiguration
            .Ascii(305, Software)
            .Short(33421, 2, 2)                                      // CFARepeatPatternDim
            .Bytes(33422, cfa)                                       // CFAPattern
            .Bytes(50706, 1, 4, 0, 0)                                // DNGVersion 1.4
            .Bytes(50707, 1, 1, 0, 0)                                // DNGBackwardVersion 1.1
            .Ascii(50708, model)                                     // UniqueCameraModel
            .Long(50714, (uint)Math.Max(0, r.BlackLevel))
            .Long(50717, (uint)Math.Max(1, r.WhiteLevel))
            .Rational(50718, scaleH, scaleV)                         // DefaultScale
            .Long(50719, 0, 0)                                       // DefaultCropOrigin (in active area)
            .Long(50720, (uint)(right - left), (uint)(bottom - top)) // DefaultCropSize
            .Matrix(50721, Profile.ColorMatrix1)
            .Short(50778, (ushort)Profile.Illuminant1)
            .Long(50829, (uint)top, (uint)left, (uint)bottom, (uint)right) // ActiveArea
            .Bytes(51043, Timecode(frame, fpsNum, fpsDen))           // CinemaDNG TimeCodes
            .SRational(51044, (fpsNum, fpsDen))                      // CinemaDNG FrameRate
            .Ascii(51081, clip.BaseName)                             // CinemaDNG ReelName
            .SRational(51109, (0, 1));                               // CinemaDNG BaselineExposureOffset

        if (Profile.ColorMatrix2 is { } cm2)
            ifd0.Matrix(50722, cm2).Short(50779, (ushort)Profile.Illuminant2);
        if (Profile.ForwardMatrix1 is { } fm1) ifd0.Matrix(50964, fm1);
        if (Profile.ForwardMatrix2 is { } fm2) ifd0.Matrix(50965, fm2);

        try
        {
            var neutral = WhiteBalance.AsShotNeutral(clip.Wbal, Profile);
            ifd0.Rational(50728, neutral.Select(v => ((uint)v.Num, (uint)v.Den)).ToArray());
        }
        catch (ArgumentException)
        {
            // A matrix that cannot give a white balance: leave AsShotNeutral out, Resolve uses its own.
        }

        if (r.ExposureBiasDen != 0) ifd0.SRational(50730, (r.ExposureBiasNum, r.ExposureBiasDen));
        else ifd0.SRational(50730, (0, 1));
        if (clip.Idnt?.CameraSerial is { Length: > 0 } serial) ifd0.Ascii(50735, serial);
        if (FrameDateTime(frame) is { } dt) ifd0.Ascii(306, dt.ToString("yyyy:MM:dd HH:mm:ss"));

        var exif = new TiffIfd().Undefined(36864, "0230"u8.ToArray()); // ExifVersion
        if (clip.Expo is { } expo)
        {
            if (expo.ShutterMicroseconds > 0) exif.Rational(33434, ((uint)Math.Min(expo.ShutterMicroseconds, uint.MaxValue), 1_000_000));
            if (expo.IsoValue is > 0 and <= ushort.MaxValue) exif.Short(34855, (ushort)expo.IsoValue).Short(34864, 3);
        }
        if (clip.Lens is { } lens)
        {
            if (lens.ApertureX100 > 0) exif.Rational(33437, (lens.ApertureX100, 100));
            if (lens.FocalLength > 0) exif.Rational(37386, (lens.FocalLength, 1));
            if (lens.FocalDistance is > 0 and < 65535) exif.Rational(37382, (lens.FocalDistance, 1000));
            if (lens.LensName.Length > 0) exif.Ascii(42036, lens.LensName);
        }

        var header = new byte[HeaderSize];
        TiffWriter.Write(header, ifd0, exif);
        return header;
    }

    DateTime? FrameDateTime(int frame)
    {
        if (_clip.Rtci?.ToDateTime() is not { } start || _clip.Frames.Count == 0) return null;
        ulong t0 = _clip.Frames[0].Timestamp, t = _clip.Frames[frame].Timestamp;
        return t >= t0 ? start.AddTicks((long)(t - t0) * 10) : start;
    }

    /// <summary>SMPTE timecode in the CinemaDNG TimeCodes layout (BCD frames, seconds, minutes, hours).</summary>
    static byte[] Timecode(int frame, int fpsNum, int fpsDen)
    {
        double fps = fpsDen == 0 ? 0 : (double)fpsNum / fpsDen;
        int timebase = fps > 1 ? (int)Math.Round(fps) : 1;            // non-integer rates count on the next integer
        long totalSeconds = frame / timebase;
        int frames = frame % timebase;
        int seconds = (int)(totalSeconds % 60), minutes = (int)(totalSeconds / 60 % 60), hours = (int)(totalSeconds / 3600 % 24);
        static byte Bcd(int v) => (byte)((v / 10) << 4 | v % 10);
        return new[] { (byte)(Bcd(frames) & 0x3F), (byte)(Bcd(seconds) & 0x7F), (byte)(Bcd(minutes) & 0x7F), (byte)(Bcd(hours) & 0x3F), (byte)0, (byte)0, (byte)0, (byte)0 };
    }
}
