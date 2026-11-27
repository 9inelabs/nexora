using Nexora.Audio.Dsp;
using Nexora.Audio.Engine;
using Nexora.Audio.Mix;

namespace Nexora.Tests;

/// <summary>Phase 2 of the complete desk: control groups, mute groups, hard mute, solo buses and stereo link.</summary>
public class GroupTests
{
    private static float G(double db) => (float)Math.Pow(10, db / 20);

    /// <summary>Channel 1 alone on output 1 through the main left, every other channel off the main mix.</summary>
    private static MixState OneChannel()
    {
        var s = MixRig.Plain();
        foreach (var c in s.Channels) c.ToMain = false;
        s.Channels[0].ToMain = true;
        s.Outputs[0].Source = "main:l";
        return s;
    }

    private const float Centre = 0.7071f;

    // ---- VCA-style control groups -------------------------------------------------------------------

    [Fact]
    public void A_control_group_master_moves_its_members_without_mixing_them()
    {
        var s = OneChannel();
        var g = MixParams.AddControl(s, ControlMode.Vca, "Band", members: ["ch1", "ch2"])!;
        Assert.Equal("cg1", g.Id);
        Assert.True(MixParams.Apply(s, "cg1.fader", MixRig.J(-6.0)));
        // The channel's own fader is unchanged: the master is added on top.
        Assert.Equal(0, s.Channels[0].Fader);
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Assert.InRange(o[0][^1], 0.5f * Centre * G(-6) * 0.98f, 0.5f * Centre * G(-6) * 1.02f);

        // A master at the bottom silences its members.
        Assert.True(MixParams.Apply(s, "cg1.fader", MixRig.J(Db.Floor)));
        e.Load(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Assert.Equal(0f, o[0][^1], 4);
    }

    [Fact]
    public void Two_masters_add_up_and_never_push_a_member_past_plus_10()
    {
        var s = OneChannel();
        s.Channels[0].Fader = 6;
        MixParams.AddControl(s, ControlMode.Vca, members: ["ch1"]);
        MixParams.AddControl(s, ControlMode.Vca, members: ["ch1"]);
        MixParams.Apply(s, "cg1.fader", MixRig.J(4.0));
        MixParams.Apply(s, "cg2.fader", MixRig.J(5.0));
        var fx = ControlRules.Effects(s)["ch1"];
        Assert.Equal(9, fx.Offset, 3);
        Assert.Equal(10, ControlRules.Effective(s.Channels[0], fx), 3);
    }

    [Fact]
    public void A_safe_member_ignores_the_master_until_it_is_taken_out_of_safe()
    {
        var s = OneChannel();
        MixParams.AddControl(s, ControlMode.Vca, members: ["ch1"]);
        MixParams.Apply(s, "cg1.mute", MixRig.J(true));
        Assert.True(MixParams.Apply(s, "cg1.safe.ch1", MixRig.J(true)));
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Assert.InRange(o[0][^1], 0.5f * Centre * 0.98f, 0.5f * Centre * 1.02f);

        MixParams.Apply(s, "cg1.safe.ch1", MixRig.J(false));
        e.Load(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Assert.Equal(0f, o[0][^1], 4);
    }

    [Fact]
    public void Removing_a_control_group_or_changing_its_mode_keeps_the_sound_the_same()
    {
        var s = OneChannel();
        MixParams.AddControl(s, ControlMode.Vca, members: ["ch1", "ch2"]);
        MixParams.Apply(s, "cg1.fader", MixRig.J(-10.0));
        Assert.True(MixParams.Apply(s, "cg1.mode", MixRig.J("Linked")));
        Assert.Equal(-10, s.Channels[0].Fader, 3);
        Assert.Equal(-10, s.Channels[1].Fader, 3);
        Assert.Equal(0, s.Control("cg1")!.Fader);

        MixParams.AddControl(s, ControlMode.Mute, members: ["ch3"]);
        MixParams.Apply(s, "cg2.mute", MixRig.J(true));
        Assert.True(MixParams.RemoveControl(s, "cg2"));
        Assert.True(s.Channels[2].Mute);
    }

    [Fact]
    public void There_are_at_most_24_control_groups()
    {
        var s = MixState.CreateDefault();
        for (var n = 0; n < MixState.MaxControls; n++) Assert.NotNull(MixParams.AddControl(s, ControlMode.Mute));
        Assert.Null(MixParams.AddControl(s, ControlMode.Vca));
        MixParams.RemoveControl(s, "cg7");
        Assert.Equal("cg7", MixParams.AddControl(s, ControlMode.Vca)!.Id);
    }

    // ---- Linked faders (moving-fader groups) ---------------------------------------------------------

    [Fact]
    public void Moving_one_linked_fader_moves_the_others_by_the_same_amount()
    {
        var s = MixState.CreateDefault();
        s.Channels[1].Fader = -5;
        s.Channels[2].Fader = Db.Floor;
        MixParams.AddControl(s, ControlMode.Linked, members: ["ch1", "ch2", "ch3", "ch4"]);
        MixParams.Apply(s, "cg1.safe.ch4", MixRig.J(true));
        Assert.True(MixParams.Apply(s, "ch1.fader", MixRig.J(-3.0)));
        Assert.Equal(-8, s.Channels[1].Fader, 3);
        Assert.Equal(Db.Floor, s.Channels[2].Fader); // one at the bottom stays there
        Assert.Equal(0, s.Channels[3].Fader);         // safe
        Assert.Contains("ch2", MixParams.AlsoChanges(s, "ch1.fader"));

        // The linked group's own fader moves every member.
        Assert.True(MixParams.Apply(s, "cg1.fader", MixRig.J(2.0)));
        Assert.Equal(-1, s.Channels[0].Fader, 3);
        Assert.Equal(-6, s.Channels[1].Fader, 3);
    }

    // ---- Mute groups ---------------------------------------------------------------------------------

    [Fact]
    public void A_mute_group_mutes_every_member_and_lets_them_go_again()
    {
        var s = OneChannel();
        s.Channels[1].ToMain = true;
        MixParams.AddControl(s, ControlMode.Mute, "Band mics", members: ["ch1", "ch2"]);
        var (e, i, o) = MixRig.Make(s);
        Assert.True(MixParams.Apply(s, "cg1.mute", MixRig.J(true)));
        e.Load(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f, [1] = 0.3f });
        Assert.Equal(0f, o[0][^1], 4);
        // The channels' own mute buttons are untouched.
        Assert.False(s.Channels[0].Mute);

        MixParams.Apply(s, "cg1.mute", MixRig.J(false));
        e.Load(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f, [1] = 0.3f });
        Assert.InRange(o[0][^1], 0.8f * Centre * 0.98f, 0.8f * Centre * 1.02f);
    }

    // ---- Mute and hard mute --------------------------------------------------------------------------

    [Fact]
    public void A_mute_cuts_the_wedges_but_keeps_a_pre_fader_direct_out_going()
    {
        var s = OneChannel();
        var ch = s.Channels[0];
        ch.Sends.Add(new Send { Bus = "mix1", Level = 0, Tap = SendTap.Pre });
        ch.DirectTap = SendTap.Pre;
        ch.Mute = true;
        s.Outputs[1].Source = "mix1";
        s.Outputs[2].Source = "ch:1";
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Assert.Equal(0f, o[0][^1], 4);           // main
        Assert.Equal(0f, o[1][^1], 4);           // wedge
        Assert.InRange(o[2][^1], 0.49f, 0.51f);  // recorder keeps the mic

        ch.Mute = false;
        ch.HardMute = true;
        e.Load(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Assert.Equal(0f, o[0][^1], 4);
        Assert.Equal(0f, o[1][^1], 4);
        Assert.Equal(0f, o[2][^1], 4);           // hard mute: nothing at all
    }

    [Fact]
    public void A_scene_or_undo_never_lifts_a_hard_mute()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexaudio-hard-" + Guid.NewGuid().ToString("n")[..6]);
        using var desk = new Desk(dir, network: false);
        var saved = desk.Mix.Clone();
        desk.Change(m => { m.Channels[4].HardMute = true; return true; });
        var after = SceneRecall.Apply(desk.Mix, saved, RecallScope.All);
        Assert.True(after.Channels[4].HardMute);
        desk.Change(m => { m.Channels[4].Eq.Bands[0].Gain = 3; return true; }, "EQ");
        Assert.True(desk.Undo());
        Assert.True(desk.Mix.Channels[4].HardMute);
    }

    // ---- Solo ----------------------------------------------------------------------------------------

    [Fact]
    public void Solo_goes_to_bus_1_bus_2_or_both_each_with_its_own_mode()
    {
        var s = OneChannel();
        s.Channels[0].Fader = -20;
        s.Channels[0].Solo = true;
        s.Outputs[1].Source = "mon:l";
        s.Outputs[2].Source = "mon2:l";
        s.Solo.Mode = SoloMode.Pfl;
        s.Solo.Mode2 = SoloMode.Afl;
        s.Solo.Route = SoloRoute.Both;
        var (e, i, o) = MixRig.Make(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Assert.InRange(o[1][^1], 0.49f, 0.51f);                                    // before the fader
        Assert.InRange(o[2][^1], 0.5f * 0.1f * 0.95f, 0.5f * 0.1f * 1.05f);   // after the fader

        // Only bus 2: bus 1 goes back to the main mix.
        s.Solo.Route = SoloRoute.Bus2;
        e.Load(s);
        MixRig.Settle(e, i, o, new() { [0] = 0.5f });
        Assert.InRange(o[1][^1], o[0][^1] * 0.98f, o[0][^1] * 1.02f);
    }

    [Fact]
    public void Exclusive_solo_and_clear_leave_solo_safe_strips_soloed()
    {
        var s = MixState.CreateDefault();
        s.Solo.Exclusive = true;
        s.Channels[30].SoloSafe = true;
        s.Channels[30].Solo = true;
        MixParams.Apply(s, "ch1.solo", MixRig.J(true));
        MixParams.Apply(s, "ch2.solo", MixRig.J(true));
        Assert.False(s.Channels[0].Solo);
        Assert.True(s.Channels[1].Solo);
        Assert.True(s.Channels[30].Solo);
        MixParams.ClearSolos(s);
        Assert.False(s.Channels[1].Solo);
        Assert.True(s.Channels[30].Solo);
    }

    // ---- Stereo link ---------------------------------------------------------------------------------

    [Fact]
    public void Linking_a_pair_copies_the_left_side_and_pans_them_apart()
    {
        var s = MixState.CreateDefault();
        var keysL = s.Channels[14];
        keysL.Eq.Bands[1].Gain = 4;
        keysL.Fader = -7;
        keysL.Sends.Add(new Send { Bus = "mix2", Level = -3 });
        s.Channels[15].Input = 30;
        Assert.False(MixParams.Apply(s, "ch16.linked", MixRig.J(true))); // only the odd channel starts a pair
        Assert.True(MixParams.Apply(s, "ch15.linked", MixRig.J(true)));
        var keysR = s.Channels[15];
        Assert.Equal(4, keysR.Eq.Bands[1].Gain);
        Assert.Equal(-7, keysR.Fader);
        Assert.Equal(-3, keysR.SendTo("mix2")!.Level);
        Assert.Equal(30, keysR.Input); // each side keeps its own input
        Assert.Equal(-1, s.Channels[14].Pan);
        Assert.Equal(1, keysR.Pan);
    }

    [Fact]
    public void A_change_to_one_side_of_a_pair_goes_to_both_with_pan_mirrored()
    {
        var s = MixState.CreateDefault();
        MixParams.Apply(s, "ch15.linked", MixRig.J(true));
        MixParams.Apply(s, "ch16.comp.threshold", MixRig.J(-25.0));
        MixParams.Apply(s, "ch15.fader", MixRig.J(-4.0));
        MixParams.Apply(s, "ch15.pan", MixRig.J(-0.6));
        MixParams.Apply(s, "ch15.name", MixRig.J("Keys L"));
        Assert.Equal(-25, s.Channels[14].Comp.Threshold);
        Assert.Equal(-4, s.Channels[15].Fader);
        Assert.Equal(0.6, s.Channels[15].Pan, 3);
        Assert.Equal("", s.Channels[15].Name);
        Assert.Equal(["ch16"], MixParams.AlsoChanges(s, "ch15.eq.1.gain"));
        // Joining a group brings the partner in too.
        MixParams.AddControl(s, ControlMode.Vca);
        MixParams.Apply(s, "cg1.member.ch16", MixRig.J(true));
        Assert.Equal(["ch16", "ch15"], s.Control("cg1")!.Members);
    }

    [Fact]
    public void The_X32_shadow_mix_follows_masters_and_mute_groups_without_feeding_them_back()
    {
        var s = MixState.CreateDefault();
        MixParams.AddControl(s, ControlMode.Vca, members: ["ch1"]);
        MixParams.Apply(s, "cg1.fader", MixRig.J(-10.0));
        MixParams.AddControl(s, ControlMode.Mute, members: ["ch2"]);
        MixParams.Apply(s, "cg2.mute", MixRig.J(true));
        var d = Nexora.Audio.X32.ShadowMix.Desired(s);
        Assert.Equal(Nexora.Audio.X32.X32Client.DbToFader(-10), d["/ch/01/mix/fader"], 3);
        Assert.Equal(0, d["/ch/02/mix/on"]);
        // The X32 showing -10 means the channel's own fader is still at 0, and its mute light is the group's, not the channel's.
        var back = Nexora.Audio.X32.ShadowMix.FromDesk(s, "/ch/01/mix/fader", Nexora.Audio.X32.X32Client.DbToFader(-10))!.Value;
        Assert.Equal(0, (double)back.Value, 0);
        Assert.Null(Nexora.Audio.X32.ShadowMix.FromDesk(s, "/ch/02/mix/on", 0));
    }

    // ---- Files and scenes ----------------------------------------------------------------------------

    [Fact]
    public void Control_groups_survive_a_save_and_bad_entries_are_cleaned_out()
    {
        var s = MixState.CreateDefault();
        MixParams.AddControl(s, ControlMode.Vca, "Vocals", members: ["ch2", "ch3"]);
        s.Controls.Add(new ControlGroup { Id = "cg99" });
        s.Controls.Add(new ControlGroup { Id = "cg1" });
        s.Controls[0].Members.Add("mix40");
        s.Controls[0].Safe.Add("ch9");
        s.Channels[1].Linked = true; // an even channel cannot start a pair
        var back = MixState.FromJson(s.ToJson())!;
        var g = Assert.Single(back.Controls);
        Assert.Equal("Vocals", g.Name);
        Assert.Equal(["ch2", "ch3"], g.Members);
        Assert.Empty(g.Safe);
        Assert.False(back.Channels[1].Linked);
    }

    [Fact]
    public void A_scene_brings_back_masters_with_the_levels_and_members_with_the_routing()
    {
        var s = MixState.CreateDefault();
        MixParams.AddControl(s, ControlMode.Vca, members: ["ch1"]);
        s.Controls[0].Fader = -4;
        var saved = s.Clone();
        s.Controls[0].Fader = 0;
        s.Controls[0].Members.Add("ch2");

        var levels = SceneRecall.Apply(s, saved, RecallScope.Levels);
        Assert.Equal(-4, levels.Controls[0].Fader);
        Assert.Equal(["ch1", "ch2"], levels.Controls[0].Members);

        var routing = SceneRecall.Apply(s, saved, RecallScope.Routing);
        Assert.Equal(0, routing.Controls[0].Fader);
        Assert.Equal(["ch1"], routing.Controls[0].Members);
    }

    [Fact]
    public void Undo_never_moves_a_control_group_master()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexaudio-cg-" + Guid.NewGuid().ToString("n")[..6]);
        using var desk = new Desk(dir, network: false);
        desk.Change(m => MixParams.AddControl(m, ControlMode.Vca, members: ["ch1"]) is not null, "add a control group");
        desk.Change(m => { m.Channels[0].Eq.Bands[0].Gain = 2; return true; }, "EQ");
        desk.Set("cg1.fader", MixRig.J(-12.0));
        Assert.True(desk.Undo());
        Assert.Equal(-12, desk.Mix.Control("cg1")!.Fader);
    }
}
