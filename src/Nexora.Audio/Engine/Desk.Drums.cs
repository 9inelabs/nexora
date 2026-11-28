using System.Text.Json;
using Nexora.Audio.Dsp;
using Nexora.Audio.Drums;
using Nexora.Audio.Mix;

namespace Nexora.Audio.Engine;

/// <summary>What a drum trigger on the open channel is doing, for its page (sent with the meters).</summary>
/// <param name="Learn">Hits heard so far while learning (0 to 8), or -1 when not learning.</param>
public sealed record TriggerLive(string StripId, TriggerHit[] Hits, uint Seq, int Learn, bool HasSamples, double Rate);

public sealed partial class Desk
{
    private DrumLibrary? _drums;
    private IReadOnlyList<DrumSampleInfo> _drumList = [];
    private readonly HashSet<string> _triggerProblems = [];
    private string? _learning;

    /// <summary>The drum library (trigger samples), with NEX-Audio's starter kit in it.</summary>
    public DrumLibrary Drums
    {
        get
        {
            if (_drums is not null) return _drums;
            _drums = new DrumLibrary(_dir);
            try { _drums.EnsureStarter(); }
            catch (Exception ex) { Note("Could not make the starter drum kit: " + ex.Message, EventLevel.Warning); }
            _drumList = _drums.List();
            return _drums;
        }
    }

    /// <summary>Every sample in the drum library.</summary>
    public IReadOnlyList<DrumSampleInfo> DrumSamples
    {
        get
        {
            _ = Drums;
            return _drumList;
        }
    }

    private void RefreshDrums()
    {
        _drumList = Drums.List();
        // Triggers pick up added or replaced files.
        lock (_gate) _engine?.Load(_mix);
        Changed?.Invoke();
    }

    /// <summary>Copies WAV files into the drum library. Returns their library paths.</summary>
    public IReadOnlyList<string> ImportDrums(IEnumerable<string> files, string kit)
    {
        var added = new List<string>();
        foreach (var f in files) added.Add(Drums.Import(f, kit));
        if (added.Count > 0) _log($"Added {added.Count} drum sample{(added.Count == 1 ? "" : "s")} to the kit \"{added[0].Split('/')[0]}\".");
        RefreshDrums();
        return added;
    }

    public bool DeleteDrum(string path)
    {
        if (path.StartsWith(DrumLibrary.StarterKit + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The starter kit comes with NEX-Audio and stays.");
        var ok = Drums.Delete(path);
        if (ok) _log($"Removed the drum sample {path}.");
        RefreshDrums();
        return ok;
    }

    /// <summary>Starts Learn on a channel's trigger: the next eight hits set its threshold and hold.</summary>
    public bool LearnTrigger(string stripId)
    {
        if (_engine?.TriggerOf(stripId) is not { } t) return false;
        t.StartLearn();
        _learning = stripId;
        return true;
    }

    /// <summary>The open channel's trigger right now (hits, Learn), or null.</summary>
    public TriggerLive? TriggerLive(string? stripId)
    {
        if (stripId is null || _engine?.TriggerOf(stripId) is not { } t) return null;
        var buf = new TriggerHit[64];
        var n = t.ReadHistory(buf, out var seq);
        return new TriggerLive(stripId, buf[..n], seq, t.Learning ? t.LearnCount : -1, t.HasSamples, t.SampleRate);
    }

    /// <summary>Once a tick: finishes Learn, and tells the event log about samples that went missing.</summary>
    private void DrumTick()
    {
        if (_engine is not { } e) return;
        if (_learning is { } id && e.TriggerOf(id) is { } t && !t.Learning)
        {
            _learning = null;
            if (t.LearnResult() is { } r)
            {
                Set($"{id}.trigger.threshold", JsonSerializer.SerializeToElement(r.Threshold));
                Set($"{id}.trigger.hold", JsonSerializer.SerializeToElement(r.Hold));
                _log($"Learned the drum trigger on {NameOfStrip(id)}: fires from {r.Threshold:0} dB, holds {r.Hold:0} ms.");
            }
        }
        if (_tick % 10 != 0) return;
        var now = e.TriggerProblems();
        var keys = now.Select(p => p.Id).ToHashSet();
        foreach (var (sid, missing) in now)
            if (_triggerProblems.Add(sid))
                Note($"{NameOfStrip(sid)}: {missing} drum trigger sample{(missing == 1 ? " is" : "s are")} missing, so the natural mic is playing instead. Add the sample again on the Trigger page.", EventLevel.Problem);
        foreach (var gone in _triggerProblems.Where(k => !keys.Contains(k)).ToList())
        {
            _triggerProblems.Remove(gone);
            Note($"{NameOfStrip(gone)}: the drum trigger has all its samples again.", EventLevel.Info);
        }
    }

    private string NameOfStrip(string id)
    {
        lock (_gate) return _mix.Find(id) is { } st ? MixState.Label(st) : id;
    }
}
