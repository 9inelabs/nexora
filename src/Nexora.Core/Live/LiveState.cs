using Nexora.Core.Model;

namespace Nexora.Core.Live;

/// <summary>
/// The Live Output routing engine. It decides what each named screen shows from
/// the terminals' content and the pin / take down / bring back routing.
/// Not thread-safe: the server applies commands one at a time, in arrival order.
/// </summary>
public sealed class LiveState
{
    private readonly List<ScreenInfo> _screens = [];
    private readonly Dictionary<string, SourceState> _sources = new();
    private readonly Dictionary<string, FrameState> _frames = new();
    private long _sequence;

    public long Version { get; private set; }

    /// <summary>Service mode, rehearsal mode and the booth message.</summary>
    public LiveModes Modes { get; private set; } = new();

    public void SetModes(LiveModes modes)
    {
        if (modes == Modes) return;
        Modes = modes;
        Touch();
    }

    /// <summary>The pc id used for sources the Live Output operator owns (media, a direct camera).</summary>
    public const string LiveOutputPc = "live-output";

    public IReadOnlyList<ScreenInfo> Screens => _screens;

    public sealed class SourceState
    {
        public required string Key { get; init; }
        public required string PcId { get; init; }
        public required SectionKind Section { get; init; }
        public string PcName { get; set; } = "";
        public string? UserName { get; set; }
        public bool Online { get; set; }
        public SlideContent? Current { get; set; }
        public Dictionary<string, RouteState> Routes { get; } = new();

        public string Label => $"{Sections.Get(Section).Title} · {PcName}";
        public SectionDefinition Definition => Sections.Get(Section);
    }

    private sealed class FrameState
    {
        public MasterMode Master { get; set; }
        public List<LayerItem> Items { get; } = [];
    }

    // ---------- Screens ----------

    public void SetScreens(IEnumerable<ScreenInfo> screens)
    {
        _screens.Clear();
        _screens.AddRange(screens.OrderBy(s => s.Order));
        var ids = _screens.Select(s => s.Id).ToHashSet();
        foreach (var id in _frames.Keys.Where(k => !ids.Contains(k)).ToList()) _frames.Remove(id);
        foreach (var s in _screens) _frames.TryAdd(s.Id, new FrameState());
        foreach (var src in _sources.Values)
            foreach (var id in src.Routes.Keys.Where(k => !ids.Contains(k)).ToList())
                src.Routes.Remove(id);
        Touch();
    }

    public ScreenInfo? Screen(string id) => _screens.FirstOrDefault(s => s.Id == id);

    // ---------- Sources ----------

    public SourceState? Source(string key) => _sources.GetValueOrDefault(key);

    public IEnumerable<SourceState> SourceStates => _sources.Values;

    public SourceState RegisterSource(string pcId, string pcName, SectionKind section, string? userName)
    {
        var key = Sections.SourceKey(pcId, section);
        if (!_sources.TryGetValue(key, out var src))
        {
            src = new SourceState { Key = key, PcId = pcId, Section = section };
            _sources[key] = src;
        }
        src.PcName = pcName;
        src.UserName = userName;
        src.Online = true;
        Touch();
        return src;
    }

    /// <summary>
    /// Marks a source offline. Its content stays on screen and its pins are remembered,
    /// so a pulled cable or a restarted terminal never blanks the audience screen.
    /// </summary>
    public void SetOffline(string key)
    {
        if (_sources.TryGetValue(key, out var src) && src.Online)
        {
            src.Online = false;
            Touch();
        }
    }

    /// <summary>Removes a source the operator no longer wants listed. Its content leaves every screen.</summary>
    public void ForgetSource(string key)
    {
        if (!_sources.Remove(key)) return;
        foreach (var f in _frames.Values) f.Items.RemoveAll(i => i.SourceKey == key);
        Touch();
    }

    /// <summary>A terminal sent new content (Enter, arrow key or click). Pinned screens follow at once.</summary>
    public void SetContent(string key, SlideContent? content)
    {
        if (!_sources.TryGetValue(key, out var src)) return;
        src.Current = content;
        foreach (var (screenId, state) in src.Routes)
        {
            if (state != RouteState.Live) continue;
            if (content is null) RemoveFollowing(screenId, key);
            else Place(screenId, src, content, follows: true);
        }
        Touch();
    }

    // ---------- Routing ----------

    /// <summary>Pin a terminal live on a screen (also used for Bring back).</summary>
    public void Pin(string key, string screenId)
    {
        if (!_sources.TryGetValue(key, out var src) || Screen(screenId) is not { } screen) return;

        if (screen.OneTextSource && src.Definition.IsText)
        {
            foreach (var other in _sources.Values)
            {
                if (other.Key == key || !other.Definition.IsText) continue;
                if (other.Routes.GetValueOrDefault(screenId) == RouteState.Live)
                    other.Routes[screenId] = RouteState.Standby;
            }
            _frames[screenId].Items.RemoveAll(i =>
                i.SourceKey != key && Sections.Get(i.Section).IsText);
        }

        src.Routes[screenId] = RouteState.Live;
        if (src.Current is { } content) Place(screenId, src, content, follows: true);
        Touch();
    }

    /// <summary>Take a terminal down: its content leaves the screen, the pin is remembered.</summary>
    public void TakeDown(string key, string screenId)
    {
        if (!_sources.TryGetValue(key, out var src) || !_frames.ContainsKey(screenId)) return;
        src.Routes[screenId] = RouteState.Standby;
        RemoveFollowing(screenId, key);
        Touch();
    }

    /// <summary>Stop routing a terminal to a screen at all.</summary>
    public void Unroute(string key, string screenId)
    {
        if (!_sources.TryGetValue(key, out var src) || !_frames.ContainsKey(screenId)) return;
        src.Routes.Remove(screenId);
        RemoveFollowing(screenId, key);
        Touch();
    }

    /// <summary>Cue mode: put a terminal's current item on a screen once, without following it.</summary>
    public void Take(string key, string screenId)
    {
        if (!_sources.TryGetValue(key, out var src) || src.Current is null || Screen(screenId) is not { } screen) return;
        if (screen.OneTextSource && src.Definition.IsText)
            _frames[screenId].Items.RemoveAll(i => Sections.Get(i.Section).IsText);
        Place(screenId, src, src.Current, follows: false);
        Touch();
    }

    /// <summary>Show the logo or black over everything. Pressing the same mode again returns to normal.</summary>
    public void ToggleMaster(string screenId, MasterMode mode)
    {
        if (!_frames.TryGetValue(screenId, out var f)) return;
        f.Master = f.Master == mode ? MasterMode.None : mode;
        Touch();
    }

    public void SetMaster(string screenId, MasterMode mode)
    {
        if (!_frames.TryGetValue(screenId, out var f)) return;
        f.Master = mode;
        Touch();
    }

    /// <summary>Clear Scripture and Lyrics from a screen. Pins stay; the next change shows again.</summary>
    public void ClearText(string screenId)
    {
        if (!_frames.TryGetValue(screenId, out var f)) return;
        f.Items.RemoveAll(i => Sections.Get(i.Section).IsText);
        Touch();
    }

    public void ClearAll(string screenId)
    {
        if (!_frames.TryGetValue(screenId, out var f)) return;
        f.Items.Clear();
        Touch();
    }

    // ---------- Output ----------

    public ScreenFrame Frame(string screenId)
    {
        var f = _frames.GetValueOrDefault(screenId) ?? new FrameState();
        return new ScreenFrame(screenId, f.Master,
            f.Items.OrderBy(i => i.Layer).ThenBy(i => i.Sequence).ToList(), Modes.Booth, Modes.Rehearsal);
    }

    public LiveSnapshot Snapshot(IReadOnlyList<ClientInfo> clients) => new(
        _screens.ToList(),
        _sources.Values
            .OrderBy(s => s.Section).ThenBy(s => s.PcName)
            .Select(s => new SourceInfo(s.Key, s.PcId, s.PcName, s.Section, s.UserName, s.Online, s.Current,
                new Dictionary<string, RouteState>(s.Routes)))
            .ToList(),
        _screens.Select(s => Frame(s.Id)).ToList(),
        clients,
        Version,
        Modes);

    // ---------- Persistence ----------

    public sealed record SavedSource(string Key, string PcId, string PcName, SectionKind Section, string? UserName,
        SlideContent? Current, Dictionary<string, RouteState> Routes);

    public sealed record SavedFrame(string ScreenId, MasterMode Master, List<LayerItem> Items);

    public sealed record Saved(List<SavedSource> Sources, List<SavedFrame> Frames, long Sequence, LiveModes? Modes = null);

    public Saved Export() => new(
        _sources.Values.Select(s => new SavedSource(s.Key, s.PcId, s.PcName, s.Section, s.UserName, s.Current,
            new Dictionary<string, RouteState>(s.Routes))).ToList(),
        _frames.Select(kv => new SavedFrame(kv.Key, kv.Value.Master, kv.Value.Items.ToList())).ToList(),
        _sequence,
        Modes);

    /// <summary>Restores routing and on-screen content after a restart. Every source starts offline.</summary>
    public void Import(Saved saved)
    {
        _sources.Clear();
        foreach (var s in saved.Sources)
        {
            var src = new SourceState { Key = s.Key, PcId = s.PcId, Section = s.Section, PcName = s.PcName, UserName = s.UserName, Current = s.Current, Online = false };
            foreach (var (screen, state) in s.Routes)
                if (_frames.ContainsKey(screen)) src.Routes[screen] = state;
            _sources[s.Key] = src;
        }
        foreach (var f in saved.Frames)
        {
            if (!_frames.TryGetValue(f.ScreenId, out var frame)) continue;
            frame.Master = f.Master;
            frame.Items.Clear();
            frame.Items.AddRange(f.Items);
        }
        _sequence = saved.Sequence;
        // Rehearsal mode never survives a restart: a fresh start always reaches the screens.
        Modes = (saved.Modes ?? new LiveModes()) with { Rehearsal = false };
        Touch();
    }

    // ---------- Time ----------

    /// <summary>Something the clock asks the server to do (a countdown reached zero).</summary>
    public sealed record ClockAction(string SourceKey, ZeroAction Action);

    /// <summary>
    /// Runs every second on the Live Output PC: lower thirds hide when their time is up, finished videos
    /// leave the screen (so it returns to what was there before), and countdowns that reach zero report
    /// their action once.
    /// </summary>
    public IReadOnlyList<ClockAction> Tick(DateTime utcNow)
    {
        var actions = new List<ClockAction>();
        foreach (var src in _sources.Values.ToList())
        {
            var c = src.Current;
            if (c is null) continue;
            if (c.LowerThird is { } lt && lt.Expired(utcNow)) SetContent(src.Key, null);
            else if (c.Media is { } m && m.Finished(utcNow)) SetContent(src.Key, null);
            else if (c.Countdown is { Running: true, AtZero: not ZeroAction.None } cd && cd.Remaining(utcNow) <= 0 && !_firedZero.Contains(src.Key + cd.EndsUtc))
            {
                _firedZero.Add(src.Key + cd.EndsUtc);
                actions.Add(new ClockAction(src.Key, cd.AtZero));
            }
        }
        // Items taken as snapshots (not following a source) expire the same way.
        foreach (var f in _frames.Values)
        {
            var removed = f.Items.RemoveAll(i => !i.Follows && (i.Content.LowerThird?.Expired(utcNow) == true || i.Content.Media?.Finished(utcNow) == true));
            if (removed > 0) Touch();
        }
        return actions;
    }

    private readonly HashSet<string> _firedZero = [];

    /// <summary>Removes one source's content from every screen (pins stay), for example the announcement loop at a countdown's zero.</summary>
    public void ClearSourceEverywhere(SectionKind section)
    {
        foreach (var src in _sources.Values.Where(s => s.Section == section)) SetContent(src.Key, null);
    }

    // ---------- Internals ----------

    private void Place(string screenId, SourceState src, SlideContent content, bool follows)
    {
        if (!_frames.TryGetValue(screenId, out var f)) return;
        var layer = src.Definition.Layer;
        // Scripture, Lyrics and Camera hold one item each: the most recent wins.
        // The overlay layer holds one item per source (a lower third and a countdown can sit together).
        if (layer == Layer.Overlays) f.Items.RemoveAll(i => i.Layer == layer && i.SourceKey == src.Key);
        else f.Items.RemoveAll(i => i.Layer == layer);
        f.Items.Add(new LayerItem(layer, src.Key, src.Label, src.Section, content, follows, ++_sequence));
    }

    private void RemoveFollowing(string screenId, string key)
    {
        if (_frames.TryGetValue(screenId, out var f))
            f.Items.RemoveAll(i => i.SourceKey == key && i.Follows);
    }

    private void Touch() => Version++;
}
