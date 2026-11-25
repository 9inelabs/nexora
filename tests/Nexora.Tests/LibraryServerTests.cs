using System.Diagnostics;
using Nexora.Core.Bible;
using Nexora.Core.Model;
using Nexora.Core.Net;
using Nexora.Core.Protocol;

namespace Nexora.Tests;

public class LibraryServerTests
{
    static LibraryServerTests()
    {
        NexoraClient.RetryDelay = TimeSpan.FromMilliseconds(300);
    }

    private static async Task WaitFor(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("Condition not met in time");
            await Task.Delay(10);
        }
    }

    private static async Task<NexoraClient> SignedIn(ServerFixture f, string pc, UserInfo user, string pin, params SectionKind[] sections)
    {
        var c = f.Client(pc);
        await WaitFor(() => c.Status == ConnectionStatus.Connected);
        var r = await c.LoginAsync(user.Id, pin);
        Assert.True(r.Ok, r.Error);
        if (sections.Length > 0)
        {
            await c.OpenSectionsAsync(sections);
            await WaitFor(() => c.Snapshot.Sources.Count(s => s.PcId == pc && s.Online) == sections.Length);
        }
        return c;
    }

    [Fact]
    public async Task Themes_are_shared_with_every_pc()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var daniel = await SignedIn(f, "pc1", f.Daniel, "2222");
        await using var other = await SignedIn(f, "pc2", f.Admin, "1111");
        await WaitFor(() => other.Library is not null);
        Assert.Equal(ScriptureTheme.BuiltIn.Count, other.Library!.Themes.Count);

        var mine = ScriptureTheme.Classic with { Id = "t-new", Name = "Easter", TextColor = "#FFEECC" };
        await daniel.SendAsync(new SaveScriptureTheme(mine));
        await WaitFor(() => other.Library!.Themes.Any(t => t.Name == "Easter"));
        Assert.Equal("#FFEECC", other.Library!.Themes.Single(t => t.Id == "t-new").TextColor);

        // A trainee cannot change themes.
        await using var tom = await SignedIn(f, "pc3", f.Trainee, "3333");
        await tom.SendAsync(new SaveScriptureTheme(mine with { Id = "t-tom", Name = "Tom" }));
        await Task.Delay(300);
        Assert.DoesNotContain(other.Library!.Themes, t => t.Id == "t-tom");

        // Only the admin sets the church default.
        await daniel.SendAsync(new SaveScriptureSettings(new ScriptureSettings("ESV", "t-new")));
        await Task.Delay(300);
        Assert.Equal("KJV", other.Library!.Scripture.DefaultTranslation);
        await other.SendAsync(new SaveScriptureSettings(new ScriptureSettings("ESV", "t-new")));
        await WaitFor(() => daniel.Library!.Scripture.DefaultTranslation == "ESV");
    }

    [Fact]
    public async Task Imported_bibles_and_backgrounds_reach_every_pc()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var admin = await SignedIn(f, "pc1", f.Admin, "1111");
        await using var daniel = await SignedIn(f, "pc2", f.Daniel, "2222");

        var t = new BibleText { Abbrev = "TST", Name = "Test Bible" };
        t.Add(43, 3, 16, "For God so loved the world.");
        var file = Path.Combine(f.Dir, "upload.nxbible");
        BibleFile.Write(t, file, "test", "h1");

        // Daniel is not an admin: refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() => daniel.UploadAsync("bibles", "TST.nxbible", file));

        var stored = await admin.UploadAsync("bibles", "TST.nxbible", file);
        Assert.Equal("TST.nxbible", stored);
        await WaitFor(() => daniel.Library?.Bibles.Any(b => b.Name == "TST.nxbible") == true);
        var target = Path.Combine(f.Dir, "pc2-copy.nxbible");
        Assert.True(await daniel.DownloadAsync("bibles", "TST.nxbible", target));
        Assert.Equal("For God so loved the world.", BibleImport.Load(target).Bible.Get(43, 3, 16));
        Assert.Equal(daniel.Library!.Bibles.Single().Hash, BibleImport.Hash(await File.ReadAllBytesAsync(target)));

        // A background picture from the Scripture operator.
        var png = Path.Combine(f.Dir, "bg.png");
        await File.WriteAllBytesAsync(png, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4]);
        var media = await daniel.UploadAsync("media", "bg.png", png);
        Assert.EndsWith(".png", media);
        await WaitFor(() => admin.Library?.Media.Any(m => m.Name == media) == true);
        Assert.True(await admin.DownloadAsync("media", media, Path.Combine(f.Dir, "bg-copy.png")));

        // Names that try to leave the library folder are refused.
        Assert.False(await admin.DownloadAsync("media", "..%2Flib.db", Path.Combine(f.Dir, "x")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.UploadAsync("media", "evil.exe", png));

        await admin.SendAsync(new RemoveBible("TST"));
        await WaitFor(() => daniel.Library!.Bibles.Count == 0);
    }

    [Fact]
    public async Task Scripture_slides_carry_verses_and_theme_to_the_screen()
    {
        await using var f = new ServerFixture();
        await f.Server.StartAsync();
        await using var daniel = await SignedIn(f, "pc1", f.Daniel, "2222", SectionKind.Scripture);
        await using var admin = await SignedIn(f, "pc2", f.Admin, "1111");
        var key = Sections.SourceKey("pc1", SectionKind.Scripture);
        await admin.RouteAsync(key, "main", RouteAction.Pin);
        await WaitFor(() => daniel.Snapshot.Sources.Single(s => s.Key == key).RouteOn("main") == RouteState.Live);

        var slide = new ScriptureSlide("John 3:16", [new ScriptureColumn("KJV", [new VerseText(3, 16, "For God so loved the world")])],
            ScriptureTheme.Classic with { TextColor = "#ABCDEF" });
        await daniel.SendContentAsync(SectionKind.Scripture, new SlideContent("For God so loved the world", "John 3:16 · KJV", "John 3:16", slide));
        await WaitFor(() => admin.Snapshot.FrameFor("main")?.Items.Any(i => i.Content.Scripture is not null) == true);
        var shown = admin.Snapshot.FrameFor("main")!.Items.Single().Content.Scripture!;
        Assert.Equal("#ABCDEF", shown.Theme.TextColor);
        Assert.Equal(16, shown.Columns[0].Verses[0].Verse);
    }
}
