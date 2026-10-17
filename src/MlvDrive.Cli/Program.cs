using System.Globalization;
using MlvDrive.Core.Audio;
using MlvDrive.Core.Dng;
using MlvDrive.Core.Mlv;
using MlvDrive.Core.Testing;

// mlvdrive: command-line companion to MLV Drive.
//   info CLIP.MLV                       what the clip contains and how it will be shown
//   export CLIP.MLV OUTDIR [--fps N]    write the clip as a CinemaDNG folder (+ WAV) to disk
//   testclip OUTDIR [--bpp 12]          write a synthetic colour-bar clip for checking an editor

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0) return Usage();
    try
    {
        return args[0] switch
        {
            "info" when args.Length >= 2 => Info(args[1]),
            "export" when args.Length >= 3 => Export(args[1], args[2], Option(args, "--fps")),
            "testclip" when args.Length >= 2 => TestClip(args[1], (int)(Option(args, "--bpp") ?? 12)),
            _ => Usage(),
        };
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        return 1;
    }
}

static int Usage()
{
    Console.Error.WriteLine("usage: mlvdrive info CLIP.MLV | export CLIP.MLV OUTDIR [--fps N] | testclip OUTDIR [--bpp 10|12|14]");
    return 2;
}

static double? Option(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? double.Parse(args[i + 1], CultureInfo.InvariantCulture) : null;
}

static int Info(string path)
{
    using var clip = MlvClip.Open(path);
    var r = clip.Rawi;
    var (num, den) = clip.FrameRate();
    Console.WriteLine($"{clip.BaseName}: {clip.FilePaths.Count} file(s), {clip.Frames.Count} frames at {(double)num / den:0.###} fps");
    Console.WriteLine($"  camera   {clip.Idnt?.CameraName ?? "?"}");
    if (r is { } ri)
        Console.WriteLine($"  frame    {ri.XRes}x{ri.YRes} {ri.BitsPerPixel}-bit, black {ri.BlackLevel}, white {ri.WhiteLevel}");
    if (clip.Expo is { } e) Console.WriteLine($"  exposure ISO {e.IsoValue}, 1/{(e.ShutterMicroseconds > 0 ? 1_000_000.0 / e.ShutterMicroseconds : 0):0}");
    if (clip.Lens is { } l) Console.WriteLine($"  lens     {l.LensName} {l.FocalLength}mm f/{l.ApertureX100 / 100.0:0.0}");
    Console.WriteLine($"  audio    {(EmbeddedWav.Create(clip) is { } w ? $"embedded, {w.Size / 1e6:0.0} MB" : PassThroughFile.FindScratchWav(path) is { } s ? "scratch WAV " + s : "none")}");
    if (clip.Unsupported is { } why) Console.WriteLine($"  NOT SHOWN: {why}");
    else
    {
        var seq = new DngSequence(clip);
        Console.WriteLine($"  colour   {seq.Profile.Name} ({seq.ProfileSource})");
        Console.WriteLine($"  DNG      {seq.FileSize / 1e6:0.0} MB per frame");
    }
    foreach (var warning in clip.Warnings) Console.WriteLine($"  warning  {warning}");
    return clip.Unsupported is null ? 0 : 1;
}

static int Export(string path, string outDir, double? fps)
{
    using var clip = MlvClip.Open(path);
    if (clip.Unsupported is { } why) throw new NotSupportedException(why);
    var seq = new DngSequence(clip, new DngOptions(fps));
    var folder = Path.Combine(outDir, clip.BaseName);
    Directory.CreateDirectory(folder);
    var buffer = new byte[seq.FileSize];
    for (int i = 0; i < seq.FrameCount; i++)
    {
        seq.Read(i, 0, buffer);
        File.WriteAllBytes(Path.Combine(folder, $"{clip.BaseName}_{i:000000}.dng"), buffer);
        if (i % 24 == 0) Console.Write($"\r{i + 1}/{seq.FrameCount}");
    }
    IVirtualFileContent? audio = EmbeddedWav.Create(clip, fps);
    if (audio is null && PassThroughFile.FindScratchWav(path) is { } wav) audio = new PassThroughFile(wav);
    if (audio is not null)
    {
        var bytes = new byte[audio.Size];
        audio.Read(0, bytes);
        File.WriteAllBytes(Path.Combine(folder, clip.BaseName + ".wav"), bytes);
    }
    Console.WriteLine($"\r{seq.FrameCount} frames -> {folder}{(audio is null ? "" : " (+ wav)")}");
    return 0;
}

static int TestClip(string outDir, int bpp)
{
    var synthetic = new SyntheticClip
    {
        Width = 1920, Height = 804, Bpp = bpp,
        Black = 2047 >> (14 - bpp), White = 16200 >> (14 - bpp),
        WithAudio = true,
    };
    synthetic.UseColourBars();
    var path = synthetic.Write(outDir, $"T{bpp:00}-BARS", new[] { 24, 24 });
    Console.WriteLine($"wrote {path} (+ .M00): 48 frames 1920x804 {bpp}-bit, colour bars, embedded audio");
    return 0;
}
