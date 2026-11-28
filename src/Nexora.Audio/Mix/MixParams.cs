using System.Text.Json;
using System.Text.Json.Nodes;
using Nexora.Audio.Dsp;

namespace Nexora.Audio.Mix;

/// <summary>
/// Changes one setting of the mix by its path, the same way on the screen and in the engine, so both
/// stay in step by passing only the path and the new value. Examples:
/// "ch5.fader", "ch5.comp.threshold", "ch5.eq.2.gain", "ch5.send.mix1.level", "ch5.group.grp1",
/// "ch5.rack.ab12cd34.p.decay", "out.7.source", "out.7.eq.1.gain", "streamset.targetLufs", "solo.mode".
/// </summary>
public static class MixParams
{
    private static JsonSerializerOptions J => MixState.Json;

    /// <summary>The longest delay on a strip or output: 1.3 seconds (about 450 m of sound travel).</summary>
    public const double MaxDelayMs = 1300;

    /// <summary>A bus's trim goes higher than a channel's (the brief: up to +60 dB), for very quiet sources summed into it.</summary>
    public const double MaxBusTrim = 60;

    public static JsonNode? Node(object? v) => v switch
    {
        null => null,
        JsonNode n => n,
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        _ => JsonSerializer.SerializeToNode(v, v.GetType(), J),
    };

    /// <summary>Applies a change. Returns false when the path does not exist or the value does not fit.</summary>
    public static bool Apply(MixState s, string path, object? value)
    {
        try
        {
            return ApplyCore(s, path, Node(value));
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    private static bool ApplyCore(MixState s, string path, JsonNode? value)
    {
        var seg = path.Split('.');
        if (seg.Length < 2) return false;
        if (s.Control(seg[0]) is { } cg) return ApplyControl(s, cg, seg, value);
        var strip = s.Find(seg[0]);
        if (strip is null) return ApplyOne(s, path, value);
        var field = seg[1];
        // Only an odd channel starts a stereo pair (1 with 2, 3 with 4, and so on).
        if (field == "linked" && (strip.Kind != StripKind.Channel || s.Channels.IndexOf(strip) % 2 == 1)) return false;
        var oldFader = strip.Fader;
        if (!ApplyOne(s, path, value)) return false;
        var now = s.Find(seg[0])!;
        if (field == "linked" && seg.Length == 2)
        {
            if (now.Linked) LinkPair(s, now);
        }
        else if (ControlRules.Partner(s, now) is { } partner && Mirrors(seg))
            ApplyOne(s, partner.Id + path[seg[0].Length..], Mirror(seg, value));
        // Linked faders follow by the same amount.
        if (field == "fader" && seg.Length == 2 && Math.Abs(now.Fader - oldFader) > 1e-9) MoveLinked(s, now, now.Fader - oldFader);
        // Exclusive solo: this strip (and its stereo partner) alone, apart from solo-safe strips.
        if (field == "solo" && seg.Length == 2 && now.Solo && s.Solo.Exclusive)
        {
            var keep = ControlRules.Partner(s, now)?.Id;
            foreach (var x in s.AllStrips())
                if (x.Id != now.Id && x.Id != keep && !x.SoloSafe) x.Solo = false;
        }
        return true;
    }

    /// <summary>The other strips a change to this path also moves (the stereo partner, linked faders), so the screen can refresh them.</summary>
    public static IEnumerable<string> AlsoChanges(MixState s, string path)
    {
        var seg = path.Split('.');
        if (seg.Length < 2) yield break;
        if (s.Control(seg[0]) is { } g)
        {
            foreach (var m in g.Members) yield return m;
            yield break;
        }
        if (s.Find(seg[0]) is not { } st) yield break;
        var seen = new HashSet<string> { st.Id };
        if (ControlRules.Partner(s, st) is { } p && seen.Add(p.Id)) yield return p.Id;
        if (seg[1] != "fader") yield break;
        foreach (var lg in s.Controls.Where(x => x.Mode == ControlMode.Linked && x.Acts(st.Id)))
            foreach (var m in lg.Members)
            {
                if (seen.Add(m)) yield return m;
                if (s.Find(m) is { } ms && ControlRules.Partner(s, ms) is { } mp && seen.Add(mp.Id)) yield return mp.Id;
            }
    }

    // ---- Stereo link ------------------------------------------------------------------------------

    /// <summary>Settings that belong to each side of a stereo pair on its own: the mic, its name, where it comes from.</summary>
    private static readonly HashSet<string> OwnSide = ["name", "input", "altInput", "useAlt", "preamp", "phantom", "linked", "directTap", "rack"];

    private static bool Mirrors(string[] seg) => !OwnSide.Contains(seg[1]);

    /// <summary>The partner gets the same change, with left and right swapped on a pan.</summary>
    private static JsonNode? Mirror(string[] seg, JsonNode? value) =>
        seg[^1] == "pan" && value is JsonValue v && v.TryGetValue<double>(out var d) ? JsonValue.Create(-d) : value?.DeepClone();

    /// <summary>A new pair: the right side takes everything from the left, and they pan hard left and right.</summary>
    private static void LinkPair(MixState s, Strip left)
    {
        if (ControlRules.Partner(s, left) is not { } right) return;
        right.TakeProcessing(left, newIds: true);
        right.Fader = left.Fader;
        right.Mute = left.Mute;
        right.Solo = left.Solo;
        right.ToMain = left.ToMain;
        right.Groups = [.. left.Groups];
        right.Sends = left.Sends.Select(x => { var c = x.Clone(); if (c.Pan is { } p) c.Pan = -p; return c; }).ToList();
        right.StreamLevel = left.StreamLevel;
        right.StreamOn = left.StreamOn;
        right.Colour = left.Colour;
        right.RecallSafe = left.RecallSafe;
        right.Safe = left.Safe;
        right.SoloSafe = left.SoloSafe;
        right.HardMute = left.HardMute;
        foreach (var g in s.Controls)
        {
            g.Members.Remove(right.Id);
            g.Safe.Remove(right.Id);
            if (g.Members.Contains(left.Id))
            {
                g.Members.Add(right.Id);
                if (g.Safe.Contains(left.Id)) g.Safe.Add(right.Id);
            }
        }
        left.Pan = -1;
        right.Pan = 1;
    }

    // ---- Control groups ---------------------------------------------------------------------------

    private static void MoveLinked(MixState s, Strip moved, double delta)
    {
        var done = new HashSet<string> { moved.Id };
        if (ControlRules.Partner(s, moved) is { } mp) done.Add(mp.Id);
        foreach (var g in s.Controls.Where(x => x.Mode == ControlMode.Linked && x.Acts(moved.Id)).ToList())
            foreach (var id in g.Members.Where(g.Acts))
                if (done.Add(id) && s.Find(id) is { } m)
                {
                    Nudge(m, delta);
                    if (ControlRules.Partner(s, m) is { } p && done.Add(p.Id)) p.Fader = m.Fader;
                }
    }

    /// <summary>Moves a fader by an amount, leaving one at the bottom where it is.</summary>
    private static void Nudge(Strip m, double delta)
    {
        if (m.Fader <= Db.Floor + 0.5 && delta < 0) return;
        m.Fader = Math.Clamp(m.Fader <= Db.Floor + 0.5 ? Db.Floor : m.Fader + delta, Db.Floor, 10);
    }

    private static bool ApplyControl(MixState s, ControlGroup g, string[] seg, JsonNode? value)
    {
        if (seg[1] is "member" or "safe" && seg.Length == 3)
        {
            if (s.Find(seg[2]) is not { Kind: not StripKind.Stream } st) return false;
            var on = value?.GetValue<bool>() ?? false;
            var ids = new List<string> { st.Id };
            if (ControlRules.Partner(s, st) is { } p) ids.Add(p.Id);
            foreach (var id in ids)
            {
                if (seg[1] == "member")
                {
                    g.Members.Remove(id);
                    g.Safe.Remove(id);
                    if (on) g.Members.Add(id);
                }
                else
                {
                    g.Safe.Remove(id);
                    if (on && g.Members.Contains(id)) g.Safe.Add(id);
                }
            }
            return true;
        }
        var u = SetIn(g, seg[1..], value);
        if (u is null) return false;
        u.Id = g.Id;
        u.Members = g.Members;
        u.Safe = g.Safe;
        u.Name ??= "";
        u.Colour ??= "";
        u.Fader = Math.Clamp(double.IsFinite(u.Fader) ? u.Fader : 0, Db.Floor, 10);
        if (u.Mode != g.Mode)
        {
            // Changing what the group does never changes the sound: a VCA master or a group mute is handed to the members first.
            Release(s, g);
            u.Fader = 0;
            u.Mute = false;
        }
        if (u.Mode == ControlMode.Linked)
        {
            u.Mute = false;
            // The linked group's master moves every member's own fader.
            var delta = u.Fader - g.Fader;
            if (Math.Abs(delta) > 1e-9 && seg[1] == "fader")
                foreach (var id in u.Members.Where(u.Acts))
                    if (s.Find(id) is { } m) Nudge(m, delta);
        }
        s.Controls[s.Controls.IndexOf(g)] = u;
        return true;
    }

    /// <summary>Hands a group's master level and mute to its members, so removing the group or changing its mode sounds the same.</summary>
    private static void Release(MixState s, ControlGroup g)
    {
        if (g.Mode == ControlMode.Linked) return;
        foreach (var id in g.Members.Where(g.Acts))
        {
            if (s.Find(id) is not { } m) continue;
            if (g.Mute) m.Mute = true;
            if (g.Mode == ControlMode.Vca)
            {
                if (g.Fader <= Db.Floor + 0.5) m.Fader = Db.Floor;
                else Nudge(m, g.Fader);
            }
        }
    }

    /// <summary>Adds a control group with the next free number (cg1 to cg24). Null when all 24 are in use.</summary>
    public static ControlGroup? AddControl(MixState s, ControlMode mode, string? name = null, string? colour = null, IEnumerable<string>? members = null)
    {
        var used = s.Controls.Select(g => int.Parse(g.Id[2..])).ToHashSet();
        if (used.Count >= MixState.MaxControls) return null;
        var n = Enumerable.Range(1, MixState.MaxControls).First(i => !used.Contains(i));
        var g = new ControlGroup { Id = $"cg{n}", Mode = mode, Name = name?.Trim() ?? "", Colour = colour ?? "" };
        foreach (var id in members ?? [])
            if (s.Find(id) is { Kind: not StripKind.Stream } st && !g.Members.Contains(st.Id))
            {
                g.Members.Add(st.Id);
                if (ControlRules.Partner(s, st) is { } p && !g.Members.Contains(p.Id)) g.Members.Add(p.Id);
            }
        s.Controls.Add(g);
        s.Controls.Sort((a, b) => int.Parse(a.Id[2..]).CompareTo(int.Parse(b.Id[2..])));
        return g;
    }

    /// <summary>Removes a control group. Its members keep sounding the way they did.</summary>
    public static bool RemoveControl(MixState s, string id)
    {
        if (s.Control(id) is not { } g) return false;
        Release(s, g);
        s.Controls.Remove(g);
        return true;
    }

    /// <summary>Clears every solo except solo-safe strips.</summary>
    public static void ClearSolos(MixState s)
    {
        foreach (var st in s.AllStrips())
            if (!st.SoloSafe) st.Solo = false;
    }

    /// <summary>One change, with no knock-on effects.</summary>
    private static bool ApplyOne(MixState s, string path, JsonNode? value)
    {
        var seg = path.Split('.');
        if (seg.Length < 2) return false;
        switch (seg[0])
        {
            case "out":
            {
                if (!int.TryParse(seg[1], out var n) || n < 1 || n > s.Outputs.Count) return false;
                var o = s.Outputs[n - 1];
                var updated = SetIn(o, seg[2..], value);
                if (updated is null) return false;
                updated.Number = n;
                Clamp(updated);
                s.Outputs[n - 1] = updated;
                return true;
            }
            case "streamset":
            {
                var u = SetIn(s.StreamSettings, seg[1..], value);
                if (u is null) return false;
                u.TargetLufs = Math.Clamp(u.TargetLufs, -30, -6);
                u.Ceiling = Math.Clamp(u.Ceiling, -6, 0);
                u.DelayMs = Math.Clamp(u.DelayMs, 0, 1000);
                s.StreamSettings = u;
                return true;
            }
            case "speech":
            {
                var u = SetIn(s.Speech, seg[1..], value);
                if (u is null) return false;
                u.Channel = (u.Channel ?? "").Trim().ToLowerInvariant();
                if (u.Channel.Length > 0 && s.Channels.All(c => c.Id != u.Channel)) return false;
                s.Speech = u;
                return true;
            }
            case "tone":
            {
                var u = SetIn(s.Tone, seg[1..], value);
                if (u is null) return false;
                u.Freq = Math.Clamp(u.Freq, 20, 20000);
                u.Level = Math.Clamp(u.Level, -60, ToneSettings.MaxLevel);
                u.Bus ??= "";
                if (u.Bus.Length > 0 && s.Find(u.Bus) is not { Kind: not (StripKind.Channel or StripKind.Stream) }) return false;
                if (u.Bus.Length == 0) u.On = false;
                s.Tone = u;
                return true;
            }
            case "mainset":
            {
                // Left and right, or left-centre-right. The centre keeps its own settings while it is switched off.
                var u = SetIn(s.MainSettings, seg[1..], value);
                if (u is null || !Enum.IsDefined(u.Layout)) return false;
                s.MainSettings = u;
                if (!s.IsLcr)
                {
                    s.Centre.Solo = false;
                    foreach (var o in s.Outputs)
                        if (o.Source.Equals("centre", StringComparison.OrdinalIgnoreCase)) o.Source = "off";
                }
                return true;
            }
            case "solo":
            {
                var u = SetIn(s.Solo, seg[1..], value);
                if (u is null) return false;
                u.Level = Math.Clamp(u.Level, -40, 10);
                u.Level2 = Math.Clamp(u.Level2, -40, 10);
                s.Solo = u;
                return true;
            }
            case "cue":
            {
                // The engineer's headphones.
                var u = SetIn(s.Cue, seg[1..], value);
                if (u is null) return false;
                u.Source = string.IsNullOrWhiteSpace(u.Source) ? "solo" : u.Source.Trim().ToLowerInvariant();
                if (u.Source is not ("solo" or "solo2" or "main") && s.Find(u.Source) is null) return false;
                u.Level = Math.Clamp(u.Level, -40, 10);
                s.Cue = u;
                return true;
            }
        }

        var strip = s.Find(seg[0]);
        if (strip is null) return false;
        var field = seg[1];

        if (field == "send" && seg.Length >= 4)
        {
            var bus = s.Find(seg[2]);
            if (bus is null || !Routing.CanSend(strip, bus)) return false;
            var send = strip.SendTo(bus.Id);
            var created = send is null;
            // A new send must not let the sound come back round (feedback).
            if (created && Routing.WouldLoop(s, strip.Id, bus.Id)) return false;
            send ??= new Send { Bus = bus.Id, Level = Db.Floor, On = true, Tap = MixState.DefaultTap(bus) };
            var u = SetIn(send, seg[3..], value);
            if (u is null) return false;
            u.Bus = bus.Id;
            u.Level = Math.Clamp(u.Level, Db.Floor, 10);
            if (u.Pan is { } pan) u.Pan = Math.Clamp(pan, -1, 1);
            if (created) strip.Sends.Add(u);
            else strip.Sends[strip.Sends.IndexOf(send)] = u;
            return true;
        }
        if (field == "group" && seg.Length == 3)
        {
            if (s.Find(seg[2]) is not { } g || !Routing.CanGroup(strip, g)) return false;
            var on = value?.GetValue<bool>() ?? false;
            if (on && !strip.Groups.Contains(g.Id) && Routing.WouldLoop(s, strip.Id, g.Id)) return false;
            strip.Groups.Remove(g.Id);
            if (on)
            {
                strip.Groups.Add(g.Id);
                // A group going into another group becomes its sub-group: it stops going to the main mix by itself, so it is not heard twice.
                if (strip.Kind == StripKind.Group) strip.ToMain = false;
            }
            return true;
        }
        if (field == "sendsFrom" && seg.Length == 2)
        {
            // A bus's tap point: every send to it moves there, and new sends start there.
            if (strip.Kind is not (StripKind.Mix or StripKind.Fx or StripKind.Matrix)) return false;
            if (!Enum.TryParse<SendTap>(value?.GetValue<string>(), true, out var tap)) return false;
            strip.SendsFrom = tap;
            foreach (var x in s.AllStrips())
                foreach (var send in x.Sends)
                    if (send.Bus == strip.Id) send.Tap = tap;
            return true;
        }
        if (field == "rack" && seg.Length >= 4)
        {
            var item = strip.Rack.FirstOrDefault(r => r.Id == seg[2]);
            if (item is null) return false;
            switch (seg[3])
            {
                case "bypass":
                    item.Bypass = value?.GetValue<bool>() ?? false;
                    return true;
                case "pre":
                    item.Pre = value?.GetValue<bool>() ?? false;
                    return true;
                case "p" when seg.Length == 5:
                    var v = value!.GetValue<double>();
                    if (Effects.Get(item.Type)?.Params.FirstOrDefault(p => p.Key == seg[4]) is { } info) v = Math.Clamp(v, info.Min, info.Max);
                    if (double.IsNaN(v) || double.IsInfinity(v)) return false;
                    item.Params[seg[4]] = v;
                    return true;
                case "t" when seg.Length == 5:
                    item.Text[seg[4]] = value?.GetValue<string>() ?? "";
                    return true;
                case "plugin":
                    var pr = value is null ? null : value.Deserialize<PluginRef>(J);
                    if (pr is not null) item.Plugin = pr;
                    return pr is not null;
            }
            return false;
        }

        // Anything else on the strip itself, for example "fader", "comp.ratio", "eq.3.freq".
        var updated2 = SetIn(strip, seg[1..], value);
        if (updated2 is null) return false;
        // These never change through a setting.
        updated2.Id = strip.Id;
        updated2.Kind = strip.Kind;
        if (strip.Kind is StripKind.Channel or StripKind.Mono or StripKind.Centre) updated2.Stereo = false;
        if (strip.Kind is StripKind.Centre) updated2.Pan = 0;
        if (strip.Kind is StripKind.Main or StripKind.Stream) updated2.Stereo = true;
        updated2.Rack = strip.Rack;
        updated2.Sends = strip.Sends;
        updated2.Groups = strip.Groups;
        Clamp(updated2);
        Replace(s, strip, updated2);
        return true;
    }

    private static void Replace(MixState s, Strip old, Strip now)
    {
        var i = s.Channels.IndexOf(old);
        if (i >= 0) { s.Channels[i] = now; return; }
        i = s.Buses.IndexOf(old);
        if (i >= 0) { s.Buses[i] = now; return; }
        if (ReferenceEquals(s.Main, old)) s.Main = now;
        else if (ReferenceEquals(s.Mono, old)) s.Mono = now;
        else if (ReferenceEquals(s.Stream, old)) s.Stream = now;
        else if (ReferenceEquals(s.Centre, old)) s.Centre = now;
    }

    /// <summary>Sets a value inside an object by path (lists by index, "eq.2" meaning eq.bands[2]). Returns a new copy.</summary>
    private static T? SetIn<T>(T obj, string[] path, JsonNode? value) where T : class
    {
        if (path.Length == 0) return null;
        var root = JsonSerializer.SerializeToNode(obj, J)!;
        JsonNode node = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var key = Camel(path[i]);
            JsonNode? next;
            if (node is JsonArray arr)
            {
                if (!int.TryParse(path[i], out var idx) || idx < 0 || idx >= arr.Count) return null;
                next = arr[idx];
            }
            else if (node is JsonObject o)
            {
                if (!o.TryGetPropertyValue(key, out next)) return null;
                // "eq.2.gain": the eq's bands list.
                if (next is JsonObject eo && eo.ContainsKey("bands") && i + 1 < path.Length && int.TryParse(path[i + 1], out _)) next = eo["bands"];
            }
            else return null;
            if (next is null) return null;
            node = next;
        }
        var last = path[^1];
        if (node is JsonArray la)
        {
            if (!int.TryParse(last, out var li) || li < 0 || li >= la.Count) return null;
            la[li] = value?.DeepClone();
        }
        else if (node is JsonObject lo)
        {
            var key = Camel(last);
            if (!lo.ContainsKey(key)) return null;
            lo[key] = value?.DeepClone();
        }
        else return null;
        return root.Deserialize<T>(J);
    }

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>Keeps every number in a safe range, whatever was typed or sent.</summary>
    public static void Clamp(Strip s)
    {
        s.Trim = Math.Clamp(s.Trim, -24, s.Kind == StripKind.Channel ? 24 : MaxBusTrim);
        s.DelayMs = Math.Clamp(s.DelayMs, 0, MaxDelayMs);
        if (s.Kind == StripKind.Channel)
        {
            s.Input = Math.Clamp(s.Input, 0, MixState.ChannelCount);
            s.AltInput = Math.Clamp(s.AltInput, 0, MixState.ChannelCount);
            if (s.AltInput == 0) s.UseAlt = false;
        }
        else
        {
            s.Input = s.AltInput = 0;
            s.UseAlt = false;
        }
        s.Pan = Math.Clamp(s.Pan, -1, 1);
        s.Fader = Math.Clamp(s.Fader, Db.Floor, 10);
        s.StreamLevel = Math.Clamp(s.StreamLevel, Db.Floor, 10);
        if (s.Preamp is { } p) s.Preamp = Math.Clamp(p, -12, 60);
        s.Hpf.Freq = Math.Clamp(s.Hpf.Freq, 20, 1000);
        s.Lpf.Freq = Math.Clamp(s.Lpf.Freq, 1000, 20000);
        s.Hpf.Slope = s.Hpf.Slope >= 24 ? 24 : 12;
        s.Lpf.Slope = s.Lpf.Slope >= 24 ? 24 : 12;
        ClampEq(s.Eq);
        s.Gate.Threshold = Math.Clamp(s.Gate.Threshold, -80, 0);
        s.Gate.Range = Math.Clamp(s.Gate.Range, 0, 80);
        s.Gate.Attack = Math.Clamp(s.Gate.Attack, 0.02, 100);
        s.Gate.Hold = Math.Clamp(s.Gate.Hold, 0, 2000);
        s.Gate.Release = Math.Clamp(s.Gate.Release, 1, 4000);
        s.Gate.KeyFilter = Math.Clamp(s.Gate.KeyFilter, 0, 2000);
        s.Comp.Threshold = Math.Clamp(s.Comp.Threshold, -60, 0);
        s.Comp.Ratio = Math.Clamp(s.Comp.Ratio, 1, 40);
        s.Comp.Attack = Math.Clamp(s.Comp.Attack, 0.02, 300);
        s.Comp.Release = Math.Clamp(s.Comp.Release, 5, 4000);
        s.Comp.Knee = Math.Clamp(s.Comp.Knee, 0, 24);
        s.Comp.Makeup = Math.Clamp(s.Comp.Makeup, -12, 24);
        s.Comp.Mix = Math.Clamp(s.Comp.Mix, 0, 100);
        s.Comp.KeyFilter = Math.Clamp(s.Comp.KeyFilter, 0, 2000);
        s.Drive.Drive = Math.Clamp(s.Drive.Drive, 0, 24);
        s.Drive.Mix = Math.Clamp(s.Drive.Mix, 0, 100);
        s.Drive.Output = Math.Clamp(s.Drive.Output, -12, 12);
        foreach (var b in s.DynEq.Bands)
        {
            b.Freq = Math.Clamp(b.Freq, 20, 20000);
            b.Q = Math.Clamp(b.Q, 0.3, 10);
            b.Threshold = Math.Clamp(b.Threshold, -60, 0);
            b.Range = Math.Clamp(b.Range, -18, 12);
            b.Ratio = Math.Clamp(b.Ratio, 1, 20);
            b.Attack = Math.Clamp(b.Attack, 0.1, 200);
            b.Release = Math.Clamp(b.Release, 10, 2000);
        }
        var t = s.Trigger;
        t.Threshold = Math.Clamp(t.Threshold, -60, 0);
        t.Hold = Math.Clamp(t.Hold, 5, 500);
        t.FocusLow = Math.Clamp(t.FocusLow, 20, 2000);
        t.FocusHigh = Math.Clamp(t.FocusHigh, 500, 16000);
        if (t.FocusHigh < t.FocusLow * 1.5) t.FocusHigh = Math.Min(16000, t.FocusLow * 1.5);
        t.Dynamics = Math.Clamp(t.Dynamics, 0, 100);
        t.Blend = Math.Clamp(t.Blend, 0, 100);
        t.Level = Math.Clamp(t.Level, -24, 12);
        t.Pitch = Math.Clamp(t.Pitch, -12, 12);
        t.GateTime = Math.Clamp(t.GateTime, 20, 1000);
        t.Layers ??= [];
        t.Layers.RemoveAll(l => l is null);
        if (t.Layers.Count == 0) t.Layers.Add(new TriggerLayer());
        if (t.Layers.Count > TriggerSettings.MaxLayers) t.Layers.RemoveRange(TriggerSettings.MaxLayers, t.Layers.Count - TriggerSettings.MaxLayers);
        foreach (var l in t.Layers) l.Samples = (l.Samples ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Take(TriggerSettings.MaxTakes).ToList();
        t.Key ??= "";
        if (t.Key == s.Id) t.Key = "";
        if (s.Kind != StripKind.Channel) t.On = false;
        s.Gate.Key ??= "";
        s.Comp.Key ??= "";
        if (s.Gate.Key == s.Id) s.Gate.Key = "";
        if (s.Comp.Key == s.Id) s.Comp.Key = "";
        s.Name = (s.Name ?? "").Trim();
        if (s.Name.Length > 24) s.Name = s.Name[..24];
    }

    public static void Clamp(OutputPatch o)
    {
        o.Level = Math.Clamp(o.Level, Db.Floor, 10);
        o.DelayMs = Math.Clamp(o.DelayMs, 0, MaxDelayMs);
        o.Ceiling = Math.Clamp(o.Ceiling, -24, 0);
        o.Hpf.Freq = Math.Clamp(o.Hpf.Freq, 20, 1000);
        o.Lpf.Freq = Math.Clamp(o.Lpf.Freq, 1000, 20000);
        ClampEq(o.Eq);
    }

    private static void ClampEq(EqSettings eq)
    {
        foreach (var b in eq.Bands)
        {
            b.Freq = Math.Clamp(b.Freq, 20, 20000);
            b.Gain = Math.Clamp(b.Gain, -18, 18);
            b.Q = Math.Clamp(b.Q, 0.1, 16);
        }
    }

    // ---- Changes to the shape of the mix (sent as commands, answered with the whole mix) ----------

    /// <summary>Adds a bus of this kind with the next free number. Returns null when there is no room left.</summary>
    /// <summary>Adds a bus with the choices from the new-bus card. Null when that kind is full.</summary>
    public static Strip? AddBus(MixState s, StripKind kind, string? name, NewBusOptions o)
    {
        var effect = kind == StripKind.Fx ? EffectFor(o.Effect) : null;
        var bus = AddBus(s, kind, name, effect);
        if (bus is null) return null;
        if (o.Stereo is { } st) bus.Stereo = st;
        if (o.ToMain is { } tm && kind is StripKind.Fx or StripKind.Group) bus.ToMain = tm;
        if (o.Tap is { } tap && kind is StripKind.Mix or StripKind.Fx or StripKind.Matrix) bus.SendsFrom = tap;
        if (!string.IsNullOrWhiteSpace(o.Colour)) bus.Colour = o.Colour;
        return bus;
    }

    private static RackItem EffectFor(string? choice) => choice switch
    {
        "room" or "plate" or "hall" => Effects.Default("reverb", choice),
        "delay" => Effects.Default("delay"),
        _ => Effects.Default("reverb", "hall"),
    };

    public static Strip? AddBus(MixState s, StripKind kind, string? name = null, RackItem? effect = null)
    {
        var max = kind switch
        {
            StripKind.Mix => MixState.MaxMix,
            StripKind.Group => MixState.MaxGroups,
            StripKind.Matrix => MixState.MaxMatrix,
            StripKind.Fx => MixState.MaxFx,
            _ => 0,
        };
        var used = s.BusesOf(kind).Select(b => b.Number).ToHashSet();
        if (used.Count >= max) return null;
        var n = Enumerable.Range(1, max).First(i => !used.Contains(i));
        var bus = MixState.NewBus(kind, n, name, kind == StripKind.Fx ? effect ?? Effects.Default("reverb", "hall") : null);
        s.Buses.Add(bus);
        return bus;
    }

    public static bool RemoveBus(MixState s, string id)
    {
        var bus = s.Buses.FirstOrDefault(b => b.Id == id);
        if (bus is null) return false;
        s.Buses.Remove(bus);
        foreach (var st in s.AllStrips())
        {
            st.Sends.RemoveAll(x => x.Bus == id);
            st.Groups.Remove(id);
        }
        foreach (var g in s.Controls)
        {
            g.Members.Remove(id);
            g.Safe.Remove(id);
        }
        foreach (var o in s.Outputs)
            if (o.Source == id || o.Source.StartsWith(id + ":"))
                o.Source = "off";
        if (s.Tone.Bus == id)
        {
            s.Tone.Bus = "";
            s.Tone.On = false;
        }
        return true;
    }

    public static bool AddRackItem(MixState s, string stripId, RackItem item, int index = -1)
    {
        if (s.Find(stripId) is not { } st || st.Rack.Count >= 8) return false;
        if (st.Rack.Any(r => r.Id == item.Id)) item.Id = Guid.NewGuid().ToString("n")[..8];
        if (index < 0 || index > st.Rack.Count) st.Rack.Add(item);
        else st.Rack.Insert(index, item);
        return true;
    }

    public static bool RemoveRackItem(MixState s, string stripId, string itemId) =>
        s.Find(stripId) is { } st && st.Rack.RemoveAll(r => r.Id == itemId) > 0;

    public static bool MoveRackItem(MixState s, string stripId, string itemId, int index)
    {
        if (s.Find(stripId) is not { } st || st.Rack.FirstOrDefault(r => r.Id == itemId) is not { } item) return false;
        st.Rack.Remove(item);
        st.Rack.Insert(Math.Clamp(index, 0, st.Rack.Count), item);
        return true;
    }

    /// <summary>Copies one strip's sound (filters, EQ, dynamics, effects) onto another.</summary>
    public static bool CopyProcessing(MixState s, string fromId, string toId)
    {
        if (s.Find(fromId) is not { } a || s.Find(toId) is not { } b || a == b) return false;
        b.TakeProcessing(a, newIds: true, input: false);
        return true;
    }

    /// <summary>
    /// Puts copied settings onto a strip. <paramref name="parts"/> is any of "input", "eq", "dyn", "trigger", "fx" (comma-separated), or "all".
    /// Faders, mutes, sends and routing are never pasted.
    /// </summary>
    public static bool Paste(MixState s, string toId, Strip from, string parts)
    {
        if (s.Find(toId) is not { } b) return false;
        var all = parts is "" or "all";
        bool Has(string p) => all || parts.Split(',').Contains(p);
        // The trim, polarity and delay belong to the mic and stand, so they are never pasted.
        if (Has("input"))
        {
            b.Hpf = from.Hpf.Clone();
            b.Lpf = from.Lpf.Clone();
            b.Drive = from.Drive.Clone();
        }
        if (Has("eq"))
        {
            b.Eq = from.Eq.Clone();
            b.DynEq = from.DynEq.Clone();
        }
        if (Has("dyn"))
        {
            // Every strip but the stream has a gate (on a bus, usually a ducker).
            if (b.Kind != StripKind.Stream)
            {
                b.Gate = from.Gate.Clone();
                if (b.Gate.Key == b.Id) b.Gate.Key = "";
            }
            b.Comp = from.Comp.Clone();
            if (b.Comp.Key == b.Id) b.Comp.Key = "";
            b.Order = from.Order;
        }
        if (Has("trigger") && b.Kind == StripKind.Channel)
        {
            b.Trigger = from.Trigger.Clone();
            if (b.Trigger.Key == b.Id) b.Trigger.Key = "";
        }
        if (Has("fx"))
            b.Rack = from.Rack.Where(r => r.Type != "plugin" || r.Plugin is not null).Select(r =>
            {
                var c = r.Clone();
                c.Id = Guid.NewGuid().ToString("n")[..8];
                return c;
            }).Take(8).ToList();
        Clamp(b);
        return true;
    }
}

/// <summary>What the new-bus card chose. Anything left null keeps the usual setting for that kind.</summary>
public sealed record NewBusOptions(SendTap? Tap = null, bool? Stereo = null, bool? ToMain = null, string? Effect = null, string? Colour = null);
