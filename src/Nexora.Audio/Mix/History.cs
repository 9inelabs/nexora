namespace Nexora.Audio.Mix;

/// <summary>
/// Undo and redo for the desk. Each step keeps the whole mix as it was (as text), so any change can be
/// taken back: a knob turned, an effect added, a scene recalled. Turning one knob for a while counts as one step.
/// Faders, mutes and solos are not undone, so pressing Undo never changes the live level by surprise.
/// </summary>
public sealed class MixHistory
{
    public const int Depth = 60;

    private sealed record Step(string Label, string Mix);

    private readonly LinkedList<Step> _undo = new();
    private readonly Stack<Step> _redo = new();
    private string? _lastKey;
    private DateTime _lastAt;

    /// <summary>What Undo would take back, or null when there is nothing.</summary>
    public string? UndoLabel => _undo.Last?.Value.Label;
    public string? RedoLabel => _redo.TryPeek(out var r) ? r.Label : null;

    /// <summary>True when a change to this setting now would start a new step (so the caller copies the mix only then).</summary>
    public bool Wants(string key) => key != _lastKey || DateTime.UtcNow - _lastAt >= TimeSpan.FromSeconds(1.5);

    /// <summary>A change to the setting carried on the step already there.</summary>
    public void Touch(string key)
    {
        if (key == _lastKey) _lastAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Remembers the mix before a change. Changes to the same setting within a moment of each other join the
    /// step already there (one knob turn, one step). Returns true when a new step was added.
    /// </summary>
    public bool Before(MixState mix, string label, string? key = null)
    {
        var now = DateTime.UtcNow;
        if (key is not null && key == _lastKey && now - _lastAt < TimeSpan.FromSeconds(1.5))
        {
            _lastAt = now;
            return false;
        }
        _lastKey = key;
        _lastAt = now;
        _undo.AddLast(new Step(label, mix.ToJson()));
        while (_undo.Count > Depth) _undo.RemoveFirst();
        _redo.Clear();
        return true;
    }

    /// <summary>Takes back the last step. Returns the mix to put back, or null when there is nothing to undo.</summary>
    public MixState? Undo(MixState current)
    {
        if (_undo.Last is not { } last) return null;
        _undo.RemoveLast();
        _redo.Push(new Step(last.Value.Label, current.ToJson()));
        _lastKey = null;
        return Restore(MixState.FromJson(last.Value.Mix), current);
    }

    public MixState? Redo(MixState current)
    {
        if (!_redo.TryPop(out var step)) return null;
        _undo.AddLast(new Step(step.Label, current.ToJson()));
        _lastKey = null;
        return Restore(MixState.FromJson(step.Mix), current);
    }

    /// <summary>The older mix, but with today's faders, mutes and solos.</summary>
    private static MixState? Restore(MixState? older, MixState current)
    {
        if (older is null) return null;
        foreach (var s in older.AllStrips())
            if (current.Find(s.Id) is { } now)
            {
                s.Fader = now.Fader;
                s.Mute = now.Mute;
                s.HardMute = now.HardMute;
                s.Solo = now.Solo;
            }
        // Control group masters and mutes are live levels too.
        foreach (var g in older.Controls)
            if (current.Control(g.Id) is { } now)
            {
                g.Fader = now.Fader;
                g.Mute = now.Mute;
            }
        return older;
    }

    /// <summary>A short name for a setting path, for "Undo EQ band 3 gain on Lead vox".</summary>
    public static string Describe(string path)
    {
        var seg = path.Split('.');
        if (seg.Length < 2) return path;
        var what = seg[1] switch
        {
            "eq" => seg.Length > 3 ? $"EQ band {(int.TryParse(seg[2], out var b) ? b + 1 : 0)} {seg[3]}" : "EQ",
            "dynEq" => "dynamic EQ",
            "comp" => "compressor " + (seg.Length > 2 ? seg[2] : ""),
            "gate" => "gate " + (seg.Length > 2 ? seg[2] : ""),
            "drive" => "drive",
            "hpf" => "low cut",
            "lpf" => "high cut",
            "send" => "send",
            "rack" => "effect",
            _ => seg[1],
        };
        return $"{what.Trim()} on {seg[0]}";
    }

    /// <summary>Settings Undo does not cover (the live levels).</summary>
    public static bool IsLevel(string path) =>
        path.EndsWith(".fader", StringComparison.Ordinal) || path.EndsWith(".mute", StringComparison.Ordinal) ||
        path.EndsWith(".solo", StringComparison.Ordinal) || path.EndsWith(".hardMute", StringComparison.Ordinal);
}
