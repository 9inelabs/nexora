using Nexora.Audio.Dsp;
using Nexora.Audio.Mix;

namespace Nexora.Tests;

/// <summary>Tap points: where a send is taken from (input, before or after the EQ, before or after the fader), and the new-bus choices.</summary>
public class TapPointTests
{
    /// <summary>Channel 1 with its low shelf lifted 12 dB and its fader at -20 dB, sent at 0 dB to a mix played on output 10.</summary>
    private static float SendLevel(SendTap tap, bool mute = false)
    {
        var s = MixRig.Plain();
        var mix1 = s.BusesOf(StripKind.Mix).First();
        mix1.Fader = 0;
        var ch = s.Channels[0];
        ch.Eq.Bands[0].Gain = 12;
        ch.Fader = -20;
        ch.Mute = mute;
        Assert.True(MixParams.Apply(s, $"ch1.send.{mix1.Id}.level", MixRig.J(0.0)));
        Assert.True(MixParams.Apply(s, $"ch1.send.{mix1.Id}.tap", MixRig.J(tap.ToString())));
        s.Outputs[9].Source = mix1.Id;
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.25f }, 400);
        return o[9][^1];
    }

    [Fact]
    public void Each_tap_point_takes_the_sound_from_its_own_place_in_the_strip()
    {
        var input = SendLevel(SendTap.Input);
        var preEq = SendLevel(SendTap.PreEq);
        var postEq = SendLevel(SendTap.PostEq);
        var pre = SendLevel(SendTap.Pre);
        var post = SendLevel(SendTap.Post);
        Assert.InRange(input, 0.24f, 0.26f);          // no EQ, no fader
        Assert.InRange(preEq, 0.24f, 0.26f);          // before the EQ
        Assert.True(postEq > input * 3, $"after the EQ {postEq}");
        Assert.InRange(pre, postEq * 0.97f, postEq * 1.03f);
        Assert.InRange(post, pre * 0.09f, pre * 0.11f); // the -20 dB fader
    }

    [Fact]
    public void A_muted_channel_feeds_no_tap_point()
    {
        Assert.Equal(0f, SendLevel(SendTap.Input, mute: true), 4);
        Assert.Equal(0f, SendLevel(SendTap.PostEq, mute: true), 4);
    }

    [Fact]
    public void A_new_bus_takes_the_card_choices_and_its_tap_point_moves_every_send()
    {
        var s = MixState.CreateDefault();
        var bus = MixParams.AddBus(s, StripKind.Mix, "Drummer", new NewBusOptions(SendTap.PostEq, Stereo: true));
        Assert.NotNull(bus);
        Assert.True(bus!.Stereo);
        Assert.Equal(SendTap.PostEq, bus.SendsFrom);
        Assert.True(MixParams.Apply(s, $"ch2.send.{bus.Id}.level", MixRig.J(-5.0)));
        Assert.Equal(SendTap.PostEq, s.Channels[1].SendTo(bus.Id)!.Tap);

        Assert.True(MixParams.Apply(s, $"{bus.Id}.sendsFrom", MixRig.J("Input")));
        Assert.Equal(SendTap.Input, s.Channels[1].SendTo(bus.Id)!.Tap);
        Assert.False(MixParams.Apply(s, "ch2.sendsFrom", MixRig.J("Pre")));

        var grp = MixParams.AddBus(s, StripKind.Group, "Choir", new NewBusOptions(Tap: SendTap.Input, ToMain: false));
        Assert.NotNull(grp);
        Assert.False(grp!.ToMain);
        Assert.Null(grp.SendsFrom);                    // subgroups always take channels after the fader

        var fx = MixParams.AddBus(s, StripKind.Fx, null, new NewBusOptions(Effect: "delay"));
        Assert.Equal("delay", fx!.Rack[0].Type);

        var back = MixState.FromJson(s.ToJson())!;
        Assert.Equal(SendTap.Input, back.Find(bus.Id)!.SendsFrom);
    }
}
