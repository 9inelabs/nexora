using Nexora.Audio.Show;
using Nexora.Core.Model;

namespace NexAudio.App.Services;

/// <summary>
/// Watches what is on the church's main screen (from the Live Output PC) and says when something new goes up:
/// a song, another section, or the screen being cleared. The first picture after connecting only sets the
/// starting point, so a reconnect never fires anything.
/// </summary>
public sealed class LiveWatch
{
    private HashSet<string>? _was;

    public IReadOnlyList<LiveEvent> Next(LiveSnapshot snap)
    {
        var screen = snap.Screens.Where(s => s.Kind == ScreenKind.Audience).OrderBy(s => s.Order).FirstOrDefault()
                     ?? snap.Screens.OrderBy(s => s.Order).FirstOrDefault();
        var frame = screen is null ? null : snap.FrameFor(screen.Id);
        var now = new Dictionary<string, LiveEvent>();
        if (frame is null || frame.Master is MasterMode.Black or MasterMode.Logo || frame.Items.All(i => i.Section == SectionKind.Camera))
            now["clear"] = new LiveEvent(TriggerWhen.ScreenClear, "");
        if (frame is not null && frame.Master == MasterMode.None)
            foreach (var i in frame.Items)
            {
                if (i.Content.Lyrics is { } song) now["song:" + song.SongId] = new LiveEvent(TriggerWhen.SongStarts, song.SongTitle);
                else if (i.Section != SectionKind.Camera) now["sec:" + i.Section] = new LiveEvent(TriggerWhen.SectionLive, i.Section.ToString());
            }
        var was = _was;
        _was = [.. now.Keys];
        if (was is null) return [];
        return now.Where(kv => !was.Contains(kv.Key)).Select(kv => kv.Value).ToList();
    }

    public void Reset() => _was = null;
}
