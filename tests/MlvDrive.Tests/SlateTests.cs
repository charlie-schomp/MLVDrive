using MlvDrive.Core.Mlv;

namespace MlvDrive.Tests;

/// <summary>The 6D Mark II port's clip info (an INFO block) and the Resolve metadata CSV.</summary>
public class SlateTests
{
    [Fact]
    public void Parses_the_slate_written_by_mlv_lite()
    {
        var s = ClipSlate.Parse("camera=B reel=007 scene=12 shot=C take=3\0\0\0")!;
        Assert.Equal("B", s.Camera);
        Assert.Equal(7, s.Reel);
        Assert.Equal(12, s.Scene);
        Assert.Equal("C", s.Shot);
        Assert.Equal(3, s.Take);
        Assert.Equal("B007", s.RollCard);
    }

    [Fact]
    public void Shot_is_optional_and_other_info_blocks_are_not_slates()
    {
        var s = ClipSlate.Parse("camera=A reel=001 scene=4 take=11")!;
        Assert.Null(s.Shot);
        Assert.Equal(11, s.Take);
        Assert.Null(ClipSlate.Parse("scratch_wav_start_us=-412345"));
    }

    [Fact]
    public void Csv_row_uses_resolve_fields_and_quotes_commas()
    {
        var info = new ClipHeaderInfo(
            ClipSlate.Parse("camera=A reel=002 scene=5 shot=B take=1"),
            new ExpoHeader(0, 800, 800, 0, 20833),
            new LensHeader(50, 0, 180, "EF50mm f/1.8 STM, test"),
            new IdntHeader("Canon EOS 6D Mark II", 0, "123456"));
        Assert.Equal("M02-0906,5,B,1,A,A002,800,1/48,\"EF50mm f/1.8 STM, test\",123456", MetadataCsv.Row("M02-0906", info));
        Assert.Equal("M02-0907,,,,,,,,,", MetadataCsv.Row("M02-0907", new ClipHeaderInfo(null, null, null, null)));
    }
}
