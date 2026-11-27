using System.Text.Json;
using Nexora.Audio.Mix;
using Nexora.Audio.Record;
using Nexora.Audio.Show;

namespace Nexora.Audio.Engine;

/// <summary>Show control: the cue list, macros, show files, backups, recall previews and the recorder.</summary>
public sealed partial class Desk
{
    private readonly object _showGate = new();

    private ShowState ShowCopy
    {
        get { lock (_showGate) return _show.Clone(); }
    }

    private ShowState LoadShow()
    {
        try
        {
            if (File.Exists(_showFile) && JsonSerializer.Deserialize<ShowState>(File.ReadAllText(_showFile), ShowState.Json) is { } s)
            {
                s.Repair();
                return s;
            }
        }
        catch (Exception ex) { _log("Could not read the cue list: " + ex.Message); }
        return new ShowState();
    }

    private void ChangeShow(Action<ShowState> change)
    {
        string json;
        lock (_showGate)
        {
            change(_show);
            _show.Repair();
            json = JsonSerializer.Serialize(_show, ShowState.Json);
        }
        try
        {
            var tmp = _showFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _showFile, true);
        }
        catch (Exception ex) { _log("Could not save the cue list: " + ex.Message); }
        _backupDirty = true;
        Changed?.Invoke();
    }

    // ---- Cue list ----------------------------------------------------------------------------------

    public void SaveCue(Cue cue, int index = -1)
    {
        ChangeShow(s =>
        {
            var i = s.Cues.FindIndex(c => c.Id == cue.Id);
            if (i >= 0) s.Cues[i] = cue;
            else if (index >= 0 && index <= s.Cues.Count) s.Cues.Insert(index, cue);
            else s.Cues.Add(cue);
        });
    }

    public void DeleteCue(string id) => ChangeShow(s => s.Cues.RemoveAll(c => c.Id == id));

    public void MoveCue(string id, int direction) => ChangeShow(s =>
    {
        var i = s.Cues.FindIndex(c => c.Id == id);
        var j = i + Math.Sign(direction);
        if (i < 0 || j < 0 || j >= s.Cues.Count) return;
        (s.Cues[i], s.Cues[j]) = (s.Cues[j], s.Cues[i]);
    });

    /// <summary>Fires the next cue (or the one named). Returns false at the end of the list.</summary>
    public bool GoCue(string? id = null)
    {
        Cue? cue;
        lock (_showGate) cue = id is null ? _show.Next : _show.Cues.FirstOrDefault(c => c.Id == id);
        if (cue is null) return false;
        ChangeShow(s => s.CurrentCueId = cue.Id);
        Fire(cue);
        return true;
    }

    /// <summary>Goes back one cue and fires it.</summary>
    public bool BackCue()
    {
        Cue? cue;
        lock (_showGate) cue = _show.CurrentIndex > 0 ? _show.Cues[_show.CurrentIndex - 1] : null;
        return cue is not null && GoCue(cue.Id);
    }

    /// <summary>Starts the cue list again from the top, without firing anything.</summary>
    public void ResetCues() => ChangeShow(s => s.CurrentCueId = null);

    private void Fire(Cue cue)
    {
        var ok = true;
        if (cue.SnapshotId is { Length: > 0 } snap) ok = Recall(snap, true, cue.Scope, cue.Fade);
        else if (cue.SceneId is { Length: > 0 } scene) ok = Recall(scene, false, cue.Scope, cue.Fade);
        if (!ok) Notice?.Invoke($"The cue \"{cue.Name}\" points to a snapshot or scene that has been deleted.", true);
        if (cue.MacroId is { Length: > 0 } mid) _ = RunMacroAsync(mid);
        _log($"Cue \"{cue.Name}\".");
    }

    // ---- Triggers from NEXORA ----------------------------------------------------------------------

    public void SaveTrigger(ShowTrigger t) => ChangeShow(s =>
    {
        var i = s.Triggers.FindIndex(x => x.Id == t.Id);
        if (i >= 0) s.Triggers[i] = t;
        else s.Triggers.Add(t);
    });

    public void DeleteTrigger(string id) => ChangeShow(s => s.Triggers.RemoveAll(t => t.Id == id));

    public void FollowNexora(bool on) => ChangeShow(s => s.FollowNexora = on);

    private readonly Dictionary<string, DateTime> _triggerFired = [];

    /// <summary>
    /// Something went live on the church's screens. Fires the triggers that match, each at most once every
    /// 3 seconds (a song going up twice in a row, or a reconnect, does not fire it again). Returns what was done.
    /// </summary>
    public IReadOnlyList<string> OnLive(LiveEvent e, DateTime? now = null)
    {
        if (Settings.Offline) return [];
        var t = now ?? DateTime.UtcNow;
        IReadOnlyList<ShowTrigger> hits;
        ShowState show;
        lock (_showGate)
        {
            hits = ShowTriggers.Match(_show, e);
            show = _show;
        }
        var done = new List<string>();
        foreach (var h in hits)
        {
            lock (_triggerFired)
            {
                if (_triggerFired.TryGetValue(h.Id, out var last) && t - last < TimeSpan.FromSeconds(3)) continue;
                _triggerFired[h.Id] = t;
            }
            var ok = h.Action switch
            {
                TriggerAction.Snapshot => Recall(h.Target!, true),
                TriggerAction.Cue => GoCue(h.Target),
                TriggerAction.NextCue => GoCue(),
                TriggerAction.Macro => RunMacro(h.Target!),
                _ => false,
            };
            var what = h.Action switch
            {
                TriggerAction.Snapshot => $"recalled the snapshot {SnapshotName(h.Target)}",
                TriggerAction.Cue => $"went to the cue {show.Cues.FirstOrDefault(c => c.Id == h.Target)?.Name ?? "?"}",
                TriggerAction.NextCue => "went to the next cue",
                _ => $"ran the macro {show.Macros.FirstOrDefault(m => m.Id == h.Target)?.Name ?? "?"}",
            };
            var line = (e.When switch
            {
                TriggerWhen.SongStarts => $"\"{e.Name}\" went up on the screen",
                TriggerWhen.SectionLive => $"{e.Name} went up on the screen",
                _ => "The screen was cleared",
            }) + (ok ? $": NEX-Audio {what}." : $", but NEX-Audio could not do it (the {h.Action.ToString().ToLowerInvariant()} it points to is gone).");
            _log(line);
            Notice?.Invoke(line, !ok);
            done.Add(line);
        }
        return done;
    }

    private string SnapshotName(string? id) => _store.Snapshots.FirstOrDefault(x => x.Id == id)?.Name ?? "?";

    private bool RunMacro(string id)
    {
        bool known;
        lock (_showGate) known = _show.Macros.Any(m => m.Id == id);
        if (known) _ = RunMacroAsync(id, "NEXORA");
        return known;
    }

    // ---- Macros ------------------------------------------------------------------------------------

    public void SaveMacro(Macro m) => ChangeShow(s =>
    {
        var i = s.Macros.FindIndex(x => x.Id == m.Id);
        if (i >= 0) s.Macros[i] = m;
        else s.Macros.Add(m);
        // A key moves to the macro that was just given it.
        if (m.Key > 0)
            foreach (var other in s.Macros.Where(x => x.Id != m.Id && x.Key == m.Key)) other.Key = 0;
    });

    public void DeleteMacro(string id) => ChangeShow(s =>
    {
        s.Macros.RemoveAll(m => m.Id == id);
        foreach (var c in s.Cues.Where(c => c.MacroId == id)) c.MacroId = null;
    });

    public void MoveMacro(string id, int direction) => ChangeShow(s =>
    {
        var i = s.Macros.FindIndex(m => m.Id == id);
        var j = i + Math.Sign(direction);
        if (i < 0 || j < 0 || j >= s.Macros.Count) return;
        (s.Macros[i], s.Macros[j]) = (s.Macros[j], s.Macros[i]);
    });

    /// <summary>Runs a macro's steps in order. A step that cannot be done is reported and the rest carry on.</summary>
    public async Task<bool> RunMacroAsync(string id, string by = "")
    {
        Macro? m;
        lock (_showGate) m = _show.Macros.FirstOrDefault(x => x.Id == id);
        if (m is null) return false;
        _runningMacro = m.Name;
        Changed?.Invoke();
        var problems = new List<string>();
        try
        {
            foreach (var step in m.Steps)
            {
                try
                {
                    await RunStep(step, by);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    problems.Add(ex.Message);
                }
            }
        }
        finally
        {
            _runningMacro = null;
            Changed?.Invoke();
        }
        if (problems.Count > 0) Notice?.Invoke($"The macro \"{m.Name}\" could not do everything: {string.Join(" ", problems.Distinct())}", true);
        _log($"Macro \"{m.Name}\".");
        return true;
    }

    private static readonly char[] Comma = [','];

    private async Task RunStep(MacroStep step, string by)
    {
        var strips = (step.Strip ?? "").Split(Comma, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        void SetAll(string field, object value)
        {
            foreach (var id in strips)
                if (!Set($"{id}.{field}", JsonSerializer.SerializeToElement(value)))
                    throw new InvalidOperationException($"\"{id}\" is no longer on the desk.");
        }
        switch (step.Action)
        {
            case MacroAction.Mute: SetAll("mute", true); break;
            case MacroAction.Unmute: SetAll("mute", false); break;
            case MacroAction.Fader: SetAll("fader", step.Value); break;
            case MacroAction.SendOn when step.Bus is { Length: > 0 } b: SetAll($"send.{b}.on", true); break;
            case MacroAction.SendOff when step.Bus is { Length: > 0 } b: SetAll($"send.{b}.on", false); break;
            case MacroAction.Snapshot:
                if (!Recall(step.Target ?? "", true)) throw new InvalidOperationException("Its snapshot has been deleted.");
                break;
            case MacroAction.Scene:
                if (!Recall(step.Target ?? "", false)) throw new InvalidOperationException("Its scene has been deleted.");
                break;
            case MacroAction.NextCue: GoCue(); break;
            case MacroAction.StartRecording: StartRecording(null, by); break;
            case MacroAction.StopRecording: _tape.StopRecording(); break;
            case MacroAction.PlaybackOn: _tape.SetPlayback(true); break;
            case MacroAction.LiveInputs: _tape.SetPlayback(false); break;
            case MacroAction.ClearSolo:
                Change(s =>
                {
                    foreach (var st in s.AllStrips()) st.Solo = false;
                    return true;
                });
                break;
            case MacroAction.Wait:
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(step.Value, 0, 60)));
                break;
        }
    }

    // ---- Recall preview ----------------------------------------------------------------------------

    /// <summary>What recalling a scene or snapshot would change now, without changing anything.</summary>
    public RecallPreview? Preview(string id, bool snapshot, RecallScope? scope = null, double? fade = null, string? cueId = null)
    {
        var saved = _store.Load(id, snapshot);
        if (saved is null) return null;
        var sc = scope ?? saved.Scope;
        var now = Mix;
        var after = SceneRecall.Apply(now, saved.Mix, sc);
        var (changes, safe, strips) = RecallDiff.Compare(now, after, saved.Mix);
        return new RecallPreview(saved.Id, saved.Name, snapshot, sc, fade ?? saved.FadeSeconds, changes, safe, strips, cueId);
    }

    /// <summary>What a cue would change (the next cue when id is null).</summary>
    public RecallPreview? PreviewCue(string? id)
    {
        Cue? cue;
        lock (_showGate) cue = id is null ? _show.Next : _show.Cues.FirstOrDefault(c => c.Id == id);
        if (cue is null) return null;
        if (cue.SnapshotId is { Length: > 0 } snap) return Preview(snap, true, cue.Scope, cue.Fade, cue.Id);
        if (cue.SceneId is { Length: > 0 } scene) return Preview(scene, false, cue.Scope, cue.Fade, cue.Id);
        return new RecallPreview("", cue.Name, true, RecallScope.None, 0, [], [], 0, cue.Id);
    }

    // ---- Show files and backups ----------------------------------------------------------------------

    /// <summary>Writes everything to disk now (the mix is otherwise saved a second after it stops changing).</summary>
    private void Flush()
    {
        if (_mixDirty) SaveMix();
    }

    public void SaveShow(string file, string name, string by)
    {
        if (string.IsNullOrWhiteSpace(file)) throw new InvalidOperationException("Pick where to save the show.");
        if (!file.EndsWith(ShowFiles.Extension, StringComparison.OrdinalIgnoreCase)) file += ShowFiles.Extension;
        Flush();
        var title = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file) : name.Trim();
        ChangeShow(s => s.Name = title);
        ShowFiles.Export(_dir, file, title, by);
        SaveSettings(Settings with { ShowFile = file });
        Notice?.Invoke($"Saved the show to {file}.", false);
        Changed?.Invoke();
    }

    public ShowContents InspectShow(string file) => ShowFiles.Inspect(file);

    /// <summary>
    /// Opens a show file: its scenes, snapshots, presets, cue list and macros replace this desk's, and its mix
    /// replaces the live mix when takeMix is set. A backup of the desk as it was is saved first.
    /// </summary>
    public ShowContents OpenShow(string file, bool takeMix, string by, bool remember = true)
    {
        if (_tape.Recording && takeMix) throw new InvalidOperationException("Stop recording before opening a show with its mix.");
        var contents = ShowFiles.Inspect(file);
        Flush();
        BackupNow("before opening", by);
        ShowFiles.Import(_dir, file, takeMix);
        lock (_showGate) _show = LoadShow();
        if (takeMix && contents.HasMix && MixState.FromJson(File.ReadAllText(_mixFile)) is { } mix)
        {
            Change(s =>
            {
                s.Channels = mix.Channels; s.Buses = mix.Buses; s.Main = mix.Main; s.Mono = mix.Mono; s.Stream = mix.Stream;
                s.StreamSettings = mix.StreamSettings; s.Solo = mix.Solo; s.Outputs = mix.Outputs;
                return true;
            }, $"open the show \"{contents.Manifest.Name}\"");
        }
        if (remember) SaveSettings(Settings with { ShowFile = file, CurrentSceneId = null });
        _log($"Opened the show \"{contents.Manifest.Name}\"{(takeMix ? " with its mix" : "")}.");
        Changed?.Invoke();
        return contents;
    }

    public ShowContents RestoreBackup(string name, bool takeMix, string by)
    {
        var f = ShowFiles.BackupPath(_dir, name) ?? throw new InvalidOperationException("That backup could not be found.");
        // Copy it out first: opening saves a new backup, which may push this one out of the list.
        var tmp = Path.Combine(Path.GetTempPath(), "nexaudio-restore-" + Guid.NewGuid().ToString("n")[..8] + ShowFiles.Extension);
        File.Copy(f, tmp, true);
        try { return OpenShow(tmp, takeMix, by, remember: false); }
        finally
        {
            try { File.Delete(tmp); } catch (Exception) { }
        }
    }

    /// <summary>Starts a new, empty cue list and set of macros (the mix, scenes and snapshots stay).</summary>
    public void NewShow(string name)
    {
        BackupNow("before new show");
        ChangeShow(s =>
        {
            s.Name = name.Trim();
            s.Cues.Clear();
            s.Macros.Clear();
            s.CurrentCueId = null;
        });
        SaveSettings(Settings with { ShowFile = null });
    }

    public string? BackupNow(string reason, string by = "")
    {
        if (_backingUp) return null;
        _backingUp = true;
        try
        {
            Flush();
            var f = ShowFiles.Backup(_dir, reason, by);
            _backupDirty = false;
            _lastBackup = DateTime.UtcNow;
            Changed?.Invoke();
            return f;
        }
        catch (Exception ex)
        {
            _log("Backup failed: " + ex.Message);
            return null;
        }
        finally
        {
            _backingUp = false;
        }
    }

    // ---- Recorder ------------------------------------------------------------------------------------

    /// <summary>Starts recording. Track names come from the screen (which knows the X32's scribble names), or the desk.</summary>
    public Take StartRecording(string? name, string by, IReadOnlyList<string>? screenNames = null)
    {
        MixState m;
        lock (_gate) m = _mix;
        var names = m.Channels.Select((c, i) => c.Name.Length > 0 ? c.Name : screenNames?.ElementAtOrDefault(i) ?? "").ToList();
        return _tape.StartRecording(name ?? "", by, n => n - 1 < names.Count && names[n - 1].Length > 0 ? names[n - 1] : $"Ch {n}", m.Channels.Count);
    }

    /// <summary>Arms or disarms one channel (0 = every channel) for recording.</summary>
    public void Arm(int channel, bool on)
    {
        int count;
        lock (_gate) count = _mix.Channels.Count;
        _tape.Configure(s =>
        {
            var set = (s.Armed ?? Enumerable.Range(1, count).ToList()).ToHashSet();
            if (channel == 0)
            {
                set.Clear();
                if (on) set.UnionWith(Enumerable.Range(1, count));
            }
            else if (on) set.Add(channel);
            else set.Remove(channel);
            return s with { Armed = set.Count == count ? null : set.Order().ToList() };
        });
    }
}
