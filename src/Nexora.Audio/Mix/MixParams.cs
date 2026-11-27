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
            case "solo":
            {
                var u = SetIn(s.Solo, seg[1..], value);
                if (u is null) return false;
                u.Level = Math.Clamp(u.Level, -40, 10);
                s.Solo = u;
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
            send ??= new Send { Bus = bus.Id, Level = Db.Floor, On = true, Tap = bus.Kind == StripKind.Mix ? SendTap.Pre : SendTap.Post };
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
            if (s.Find(seg[2]) is not { Kind: StripKind.Group } g || strip.Kind is not (StripKind.Channel or StripKind.Fx)) return false;
            var on = value?.GetValue<bool>() ?? false;
            strip.Groups.Remove(g.Id);
            if (on) strip.Groups.Add(g.Id);
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
        if (strip.Kind is StripKind.Channel or StripKind.Mono) updated2.Stereo = false;
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
        s.Trim = Math.Clamp(s.Trim, -24, 24);
        s.DelayMs = Math.Clamp(s.DelayMs, 0, 100);
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
        s.Name = (s.Name ?? "").Trim();
        if (s.Name.Length > 24) s.Name = s.Name[..24];
    }

    public static void Clamp(OutputPatch o)
    {
        o.Level = Math.Clamp(o.Level, Db.Floor, 10);
        o.DelayMs = Math.Clamp(o.DelayMs, 0, 500);
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
    public static Strip? AddBus(MixState s, StripKind kind, string? name = null)
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
        var bus = MixState.NewBus(kind, n, name, kind == StripKind.Fx ? Effects.Default("reverb", "hall") : null);
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
        foreach (var o in s.Outputs)
            if (o.Source == id || o.Source.StartsWith(id + ":"))
                o.Source = "off";
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
        b.Hpf = a.Hpf.Clone();
        b.Lpf = a.Lpf.Clone();
        b.Eq = a.Eq.Clone();
        b.Gate = a.Gate.Clone();
        b.Comp = a.Comp.Clone();
        b.Rack = a.Rack.Select(r =>
        {
            var c = r.Clone();
            c.Id = Guid.NewGuid().ToString("n")[..8];
            return c;
        }).ToList();
        return true;
    }
}
