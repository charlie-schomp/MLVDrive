using MlvDrive.Core.Audio;
using MlvDrive.Core.Testing;

namespace MlvDrive.Tests;

/// <summary>SMPTE LTC read from audio, as a Tentacle Sync puts it on the 6D Mark II's mic input.</summary>
public class LtcTests
{
    const int Rate = 48000;

    [Fact]
    public void Ltc_decodes_at_23976_and_2997_drop_frame()
    {
        var tenHours = new Timecode(Timecode.ToFrames(10, 0, 0, 0, 24, false), 24, false, 24000, 1001);
        var signal = LtcSignal.Generate(Rate * 4, 1000, tenHours);
        var at = Ltc.At(signal, Rate, 1000 + 10 * (48000 * 1001 / 24000.0))!.Value;
        Assert.Equal(tenHours.Frames + 10, at.Tc.Frames);
        Assert.Equal((24000, 1001), (at.Tc.RateNum, at.Tc.RateDen));
        Assert.Equal("10:00:00:10", at.Tc.ToString());

        // 29.97 DF across a minute boundary that drops ;00 and ;01
        var df = new Timecode(Timecode.ToFrames(0, 0, 59, 20, 30, true), 30, true, 30000, 1001);
        var dfSignal = LtcSignal.Generate(Rate * 4, 500, df);
        double spf = 48000 * 1001 / 30000.0;
        var hit = Ltc.At(dfSignal, Rate, 500 + 12 * spf)!.Value;
        Assert.True(hit.Tc.DropFrame);
        Assert.Equal(df.Frames + 12, hit.Tc.Frames);
        Assert.Equal("00:01:00;04", hit.Tc.ToString());               // ;21..;29 is 9, then ;02 ;03 ;04
    }

    [Fact]
    public void Silence_or_programme_audio_has_no_ltc()
    {
        var programme = new short[Rate * 3];
        for (int i = 0; i < programme.Length; i++) programme[i] = (short)(i * 31 % 20000 - 10000);
        Assert.Null(Ltc.At(programme, Rate, 1000));
        Assert.Null(Ltc.At(new short[Rate * 3], Rate, 1000));
    }
}
