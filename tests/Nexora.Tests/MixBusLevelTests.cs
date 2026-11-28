using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace Nexora.Tests;

/// <summary>Mix buses (sidefills, wedges): their level against the main mix, and the meters on sends on faders.</summary>
public class MixBusLevelTests
{
    /// <summary>A 60 Hz tone (a bass note) on channel 1, played through: the RMS on output 1 (main left) and output 10 (the bus).</summary>
    private static (float Main, float Bus, StripMeters Ch1) Run(Action<MixState> setup)
    {
        var s = MixState.CreateDefault();
        s.Outputs[0].Source = "main:l";
        setup(s);
        var (e, i, o) = MixRig.Make(s);
        double main = 0, bus = 0;
        var count = 0;
        StripMeters ch1 = default;
        for (var b = 0; b < 1500; b++)
        {
            for (var k = 0; k < MixRig.Frames; k++) i[0][k] = (float)(0.3 * Math.Sin(2 * Math.PI * 60 * (b * MixRig.Frames + k) / MixRig.Rate));
            e.Process(i, o, MixRig.Frames);
            if (b == 1400) e.TakeMeters();
            if (b > 1000)
            {
                main += o[0].Sum(x => (double)x * x);
                bus += o[9].Sum(x => (double)x * x);
                count += MixRig.Frames;
            }
        }
        ch1 = e.TakeMeters()[0];
        return ((float)Math.Sqrt(main / count), (float)Math.Sqrt(bus / count), ch1);
    }

    [Fact]
    public void A_send_at_0_dB_reaches_a_mono_mix_as_loud_as_the_channel_is_in_the_main_mix_or_louder()
    {
        var (main, bus, _) = Run(s =>
        {
            Assert.True(MixParams.Apply(s, "ch1.send.mix1.level", MixRig.J(0.0)));
            s.Outputs[9].Source = "mix1";
        });
        // The main mix pans the channel to the centre (-3 dB each side); a mono mix takes it whole.
        Assert.InRange(20 * Math.Log10(bus / main), 2.5, 3.5);
    }

    [Fact]
    public void A_send_from_the_faders_is_taken_before_the_fader_for_a_mix()
    {
        var s = MixState.CreateDefault();
        Assert.True(MixParams.Apply(s, "ch1.send.mix1.level", MixRig.J(-5.0)));
        Assert.True(MixParams.Apply(s, "ch1.send.mix1.on", MixRig.J(true)));
        Assert.Equal(SendTap.Pre, s.Channels[0].SendTo("mix1")!.Tap);
    }

    [Fact]
    public void The_meter_before_the_fader_follows_the_channel_with_its_fader_down()
    {
        var (_, _, ch1) = Run(s => s.Channels[0].Fader = -30);
        Assert.InRange(ch1.Pre, 0.28f, 0.31f);   // the 0.3 peak, before the fader
        Assert.InRange(ch1.L, 0.005f, 0.015f);   // -30 dB after it
    }

    [Fact]
    public void Sends_on_faders_meters_show_what_goes_into_the_bus()
    {
        var s = MixState.CreateDefault();
        var mix1 = s.Find("mix1")!;
        var ch = s.Channels[0];
        // No send: nothing goes in, however loud the channel is in the main mix.
        Assert.Equal(0f, Routing.SendMeterLevel(ch, mix1, 0.5f, 0.5f, 0.5f));
        // A send before the fader at -6 dB: the level before the fader, halved.
        Assert.True(MixParams.Apply(s, "ch1.send.mix1.level", MixRig.J(-6.0)));
        Assert.InRange(Routing.SendMeterLevel(ch, mix1, 0.01f, 0.01f, 0.5f), 0.24f, 0.26f);
        // After the fader: the level after it.
        Assert.True(MixParams.Apply(s, "ch1.send.mix1.tap", MixRig.J("Post")));
        Assert.InRange(Routing.SendMeterLevel(ch, mix1, 0.1f, 0.1f, 0.5f), 0.049f, 0.051f);
        // Switched off, or the channel muted: nothing.
        Assert.True(MixParams.Apply(s, "ch1.send.mix1.on", MixRig.J(false)));
        Assert.Equal(0f, Routing.SendMeterLevel(ch, mix1, 0.1f, 0.1f, 0.5f));
        Assert.True(MixParams.Apply(s, "ch1.send.mix1.on", MixRig.J(true)));
        ch.Mute = true;
        Assert.Equal(0f, Routing.SendMeterLevel(ch, mix1, 0.1f, 0.1f, 0.5f));
    }
}
