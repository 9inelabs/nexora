using Nexora.Core.Bible;
using Nexora.Core.Speech;

namespace Nexora.Tests;

public class SpokenTextTests
{
    [Theory]
    [InlineData("John three sixteen", "john 3 16")]
    [InlineData("John 3:16", "john 3 : 16")]
    [InlineData("Psalm one hundred and nineteen verse one hundred and five", "psalm 119 verse 105")]
    [InlineData("Psalm twenty-three", "psalm 23")]
    [InlineData("First Corinthians thirteen, four to seven.", "first corinthians 13 4 to 7")]
    [InlineData("Romans 8.28", "romans 8 : 28")]
    [InlineData("verses 4-7", "verses 4 to 7")]
    [InlineData("a hundred and fifty", "150")]
    [InlineData("the sixteenth verse", "the 16 verse")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, SpokenText.Normalize(input));
}

public class SpokenReferenceTests
{
    private static Heard One(string text)
    {
        var all = SpokenReferences.Find(text);
        Assert.True(all.Count >= 1, $"nothing found in \"{text}\"");
        return all[0];
    }

    [Theory]
    [InlineData("Turn with me to John chapter 3 verse 16.", "John 3:16")]
    [InlineData("john three sixteen and let us read", "John 3:16")]
    [InlineData("Let's go to Romans 8:28 this morning", "Romans 8:28")]
    [InlineData("First Corinthians 13 verses 4 to 7 says", "1 Corinthians 13:4-7")]
    [InlineData("1 Corinthians 13:4-7 says love is patient", "1 Corinthians 13:4-7")]
    [InlineData("second Timothy chapter 1 verse 7 tells us", "2 Timothy 1:7")]
    [InlineData("Psalm one hundred and nineteen verse one hundred and five is a lamp", "Psalms 119:105")]
    [InlineData("Jude verse 24 now unto him", "Jude 24")]
    [InlineData("in Philipians 4 13 we read", "Philippians 4:13")]
    [InlineData("Habakuk 2 verse 2 write the vision", "Habakkuk 2:2")]
    [InlineData("Revelations 21 4 he will wipe", "Revelation 21:4")]
    [InlineData("the book of Genesis chapter 1 and verse 1 in the beginning", "Genesis 1:1")]
    [InlineData("Song of Solomon 2 verse 4 his banner over me", "Song of Solomon 2:4")]
    [InlineData("first John 1:9 if we confess", "1 John 1:9")]
    [InlineData("Hebrews 11 1 now faith is", "Hebrews 11:1")]
    [InlineData("Isaiah 53 verses 4 and 5 surely he has borne", "Isaiah 53:4-5")]
    public void FindsExactReferences(string text, string expected)
    {
        var h = One(text);
        Assert.Equal(HeardKind.Reference, h.Kind);
        Assert.True(h.Exact);
        Assert.Equal(expected, h.Reference!.ToString());
    }

    [Theory]
    [InlineData("Open your Bibles to Psalm 23 and", "Psalms 23")]
    [InlineData("turn to the book of John chapter 15 please", "John 15")]
    [InlineData("we are in Romans chapter 12 today", "Romans 12")]
    public void FindsChapters(string text, string expected)
    {
        var h = One(text);
        Assert.Equal(HeardKind.Reference, h.Kind);
        Assert.False(h.Exact);
        Assert.Equal(expected, h.Reference!.ToString());
    }

    [Theory]
    [InlineData("John 3 people came to the meeting")]
    [InlineData("I did a good job 5 times this week")]
    [InlineData("the numbers 10 and 12 are")]
    [InlineData("Mark 2 of them were there")]
    [InlineData("John chapter 3 verse 99")]
    [InlineData("Genesis 51 verse 1")]
    [InlineData("God loves you so much")]
    public void IgnoresThingsThatAreNotReferences(string text)
    {
        Assert.DoesNotContain(SpokenReferences.Find(text), h => h.Kind == HeardKind.Reference);
    }

    [Theory]
    [InlineData("let's look at the next verse", HeardKind.NextVerse)]
    [InlineData("the following verse says", HeardKind.NextVerse)]
    [InlineData("go back a verse", HeardKind.PreviousVerse)]
    [InlineData("in the previous verse he said", HeardKind.PreviousVerse)]
    [InlineData("now the next chapter", HeardKind.NextChapter)]
    public void FindsCommands(string text, HeardKind kind)
    {
        Assert.Contains(SpokenReferences.Find(text), h => h.Kind == kind);
    }

    [Fact]
    public void VerseAndReadTo()
    {
        var go = One("go to verse thirteen");
        Assert.Equal(HeardKind.Verse, go.Kind);
        Assert.Equal(13, go.Verse);

        var range = One("look at verses 4 to 7");
        Assert.Equal(HeardKind.Verse, range.Kind);
        Assert.Equal((4, 7), (range.Verse, range.ToVerse));

        var on = One("let us read on to verse 20");
        Assert.Equal(HeardKind.ReadTo, on.Kind);
        Assert.Equal(20, on.ToVerse);

        var down = One("all the way down to verse 21");
        Assert.Equal(HeardKind.ReadTo, down.Kind);

        var through = One("through verse 18 he says");
        Assert.Equal(HeardKind.ReadTo, through.Kind);
    }

    [Fact]
    public void ChapterWithoutBook()
    {
        var c = One("now in chapter 4 verse 2 we see");
        Assert.Equal(HeardKind.Chapter, c.Kind);
        Assert.Equal((4, 2), (c.Chapter, c.Verse));
    }

    [Theory]
    [InlineData("in the Amplified it says", "AMP")]
    [InlineData("the New King James version puts it", "NKJV")]
    [InlineData("from the King James", "KJV")]
    [InlineData("the New Living Translation says", "NLT")]
    [InlineData("in the English Standard Version", "ESV")]
    public void FindsTranslations(string text, string abbrev)
    {
        var h = SpokenReferences.Find(text).First(x => x.Kind == HeardKind.Translation);
        Assert.Equal(abbrev, h.Translation);
    }

    [Fact]
    public void ReferenceAtTheEndIsNotSettled()
    {
        Assert.True(One("turn to John 3:1").AtEnd);
        Assert.False(One("turn to John 3:16 for God so loved").AtEnd);
    }

    [Fact]
    public void ResolvesCommandsAgainstWhatIsOpen()
    {
        var john316 = ReferenceParser.Parse("John 3:16").Reference!;
        Assert.Equal("John 3:17", SpokenReferences.Resolve(new Heard(HeardKind.NextVerse, "", 1, false), john316)!.ToString());
        Assert.Equal("John 3:15", SpokenReferences.Resolve(new Heard(HeardKind.PreviousVerse, "", 1, false), john316)!.ToString());
        Assert.Equal("John 3:20", SpokenReferences.Resolve(new Heard(HeardKind.Verse, "", 1, false) { Verse = 20 }, john316)!.ToString());
        Assert.Equal("John 3:16-21", SpokenReferences.Resolve(new Heard(HeardKind.ReadTo, "", 1, false) { ToVerse = 21 }, john316)!.ToString());
        Assert.Equal("John 4:1", SpokenReferences.Resolve(new Heard(HeardKind.NextChapter, "", 1, false), john316)!.ToString());
        Assert.Equal("John 5:2", SpokenReferences.Resolve(new Heard(HeardKind.Chapter, "", 1, false) { Chapter = 5, Verse = 2 }, john316)!.ToString());
        // Off the end of the chapter: into the next one.
        var john336 = ReferenceParser.Parse("John 3:36").Reference!;
        Assert.Equal("John 4:1", SpokenReferences.Resolve(new Heard(HeardKind.NextVerse, "", 1, false), john336)!.ToString());
        // No verse 99.
        Assert.Null(SpokenReferences.Resolve(new Heard(HeardKind.Verse, "", 1, false) { Verse = 99 }, john316));
        // Nothing open yet.
        Assert.Null(SpokenReferences.Resolve(new Heard(HeardKind.NextVerse, "", 1, false), null));
    }
}

public class ListenSessionTests
{
    /// <summary>Stands in for Whisper: says what the script says for each piece, by how long the piece is.</summary>
    private sealed class Scripted(Func<float[], string> say) : ITranscriber
    {
        public int Calls;
        public string Name => "script";
        public Task<string> TranscribeAsync(float[] audio, string prompt, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(say(audio));
        }
        public void Dispose() { }
    }

    private static float[] Tone(double seconds, float amp)
    {
        var n = (int)(seconds * ListenSession.Rate);
        var a = new float[n];
        var rnd = new Random(1);
        for (var i = 0; i < n; i++) a[i] = amp * (float)Math.Sin(i * 2 * Math.PI * 220 / ListenSession.Rate) + (float)(rnd.NextDouble() - 0.5) * 0.0004f;
        return a;
    }

    [Fact]
    public void HearsEachSentenceOnceAndCommandsEachTime()
    {
        var sentence = 0;
        var t = new Scripted(audio => Interlocked.Increment(ref sentence) switch
        {
            1 => "Turn with me to John 3:16 for God so loved the world.",
            2 => "Next verse.",
            3 => "Next verse.",
            _ => "",
        });
        using var s = new ListenSession(t, new ListenOptions { PartialEvery = TimeSpan.Zero });
        var heard = new List<Heard>();
        var lines = new List<TranscriptLine>();
        s.Heard += (h, _) => { lock (heard) heard.Add(h); };
        s.Transcript += l => { lock (lines) lines.Add(l); };

        s.Push(Tone(1.0, 0));
        foreach (var len in new[] { 2.5, 1.0, 1.0 })
        {
            s.Push(Tone(len, 0.2f));
            s.Push(Tone(1.0, 0));
        }
        s.Flush(TimeSpan.FromSeconds(5));

        lock (heard)
        {
            Assert.Equal(3, heard.Count);
            Assert.Equal("John 3:16", heard[0].Reference!.ToString());
            Assert.Equal(HeardKind.NextVerse, heard[1].Kind);
            Assert.Equal(HeardKind.NextVerse, heard[2].Kind);
        }
        Assert.Equal(3, lines.Count(l => l.Final));
    }

    [Fact]
    public void WaitsForTheNumberToFinishBeforeActing()
    {
        // While the sentence is still going, the partial ends on "John 3:1": not acted on until the words go on.
        var calls = 0;
        var t = new Scripted(audio => Interlocked.Increment(ref calls) == 1 ? "Turn with me to John 3:1" : "Turn with me to John 3:16 for God so loved");
        using var s = new ListenSession(t, new ListenOptions { PartialEvery = TimeSpan.FromSeconds(0.6) });
        var heard = new List<Heard>();
        s.Heard += (h, _) => { lock (heard) heard.Add(h); };
        s.Push(Tone(0.5, 0));
        for (var i = 0; i < 30; i++)
        {
            s.Push(Tone(0.1, 0.2f));
            Thread.Sleep(10);
        }
        s.Push(Tone(1.0, 0));
        s.Flush(TimeSpan.FromSeconds(5));
        lock (heard)
        {
            Assert.Single(heard);
            Assert.Equal("John 3:16", heard[0].Reference!.ToString());
        }
    }

    [Fact]
    public void PausedThrowsTheSoundAway()
    {
        var t = new Scripted(_ => "John 3:16 for God so loved");
        using var s = new ListenSession(t, new ListenOptions { PartialEvery = TimeSpan.Zero }) { Paused = true };
        s.Push(Tone(0.5, 0));
        s.Push(Tone(2, 0.2f));
        s.Push(Tone(1, 0));
        s.Flush(TimeSpan.FromSeconds(2));
        Assert.Equal(0, t.Calls);
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData(" Thank you.", "")]
    [InlineData("(music) Praise the Lord", "Praise the Lord")]
    [InlineData("John 3:16", "John 3:16")]
    public void CleansWhisperNoise(string input, string expected) => Assert.Equal(expected, ListenSession.Clean(input));
}

public class MeaningSearchTests
{
    private static MeaningSearch Make()
    {
        // A small Bible: a few verses in old and modern wording.
        var verses = new List<(int, int, int, string)>
        {
            (43, 6, 11, "And Jesus took the loaves; and when he had given thanks, he distributed to the disciples"),
            (43, 6, 11, "Then Jesus took the loaves, gave thanks to God, and distributed them to the people"),
            (43, 6, 13, "Therefore they gathered them together, and filled twelve baskets with the fragments of the five barley loaves"),
            (19, 23, 1, "The LORD is my shepherd; I shall not want."),
            (1, 1, 1, "In the beginning God created the heaven and the earth."),
            (40, 14, 20, "And they did all eat, and were filled: and they took up of the fragments that remained twelve baskets full."),
            (45, 8, 28, "And we know that all things work together for good to them that love God"),
            (42, 15, 20, "And he arose, and came to his father. But when he was yet a great way off, his father saw him, and had compassion, and ran"),
        };
        for (var i = 0; i < 400; i++) verses.Add((1, 2 + i / 25, 1 + i % 25, "filler words about the land and the people and the house number " + i));
        var index = VerseIndex.Build(verses);
        return new MeaningSearch(index, _ => "text");
    }

    [Fact]
    public void FindsStoriesByName()
    {
        var m = Make();
        var r = m.Search("remember the story of the prodigal son who came back to his father");
        Assert.NotEmpty(r);
        Assert.Equal("Luke 15:11-32", r[0].Reference.ToString());

        r = m.Search("like little David facing the giant Goliath with just a sling and a stone");
        Assert.Equal("1 Samuel 17", r[0].Reference.ToString());

        r = m.Search("when Jesus met the samaritan woman at the well and offered her living water");
        Assert.Equal("John 4:1-42", r[0].Reference.ToString());
    }

    [Fact]
    public void QuietWhenNothingFits()
    {
        var m = Make();
        Assert.Empty(m.Search("good morning everyone please be seated, we thank God for this day"));
        Assert.Empty(m.Search("amen"));
    }

    [Fact]
    public void EveryStoryReferenceIsReal()
    {
        Assert.True(BibleStories.All.Count > 200);
        foreach (var s in BibleStories.All) Assert.NotNull(s.Reference);
    }

    [Fact]
    public void FindsVersesByTheirWords()
    {
        var m = Make();
        var r = m.Search("they gathered the fragments and filled twelve baskets with barley loaves", minScore: 0.3);
        Assert.Contains(r, x => x.Reference.ToString() is "John 6:13" or "John 6:1-14" or "Matthew 14:20");
    }
}

public class SpeechFeedTests
{
    private static byte[] Packet(uint seq, string label, float value)
    {
        var buf = new byte[Nexora.Audio.SpeechPacket.HeaderSize + Nexora.Audio.SpeechPacket.Frames * 2];
        var samples = Enumerable.Repeat(value, Nexora.Audio.SpeechPacket.Frames).ToArray();
        var n = Nexora.Audio.SpeechPacket.Write(buf, seq, 0, label, samples);
        return buf[..n];
    }

    [Fact]
    public void PacketsCarryTheLabelAndTheSound()
    {
        var p = Packet(7, "SOUND-PC · Pastor", 0.5f);
        Assert.True(Nexora.Audio.SpeechPacket.TryRead(p, out var seq, out var label, out var pcm));
        Assert.Equal(7u, seq);
        Assert.Equal("SOUND-PC · Pastor", label);
        Assert.Equal(Nexora.Audio.SpeechPacket.Frames * 2, pcm.Length);
        Assert.False(Nexora.Audio.SpeechPacket.TryRead(new byte[60], out _, out _, out _));
    }

    [Fact]
    public void ReceiverFillsLostPacketsWithSilenceAndIgnoresOtherDesks()
    {
        using var rx = new Nexora.Audio.SpeechReceiver(0) { Only = "A" };
        var got = new List<float[]>();
        rx.Samples += got.Add;
        rx.Take(Packet(1, "A", 0.5f));
        rx.Take(Packet(4, "A", 0.5f)); // 2 and 3 were lost
        rx.Take(Packet(4, "A", 0.5f)); // a repeat
        rx.Take(Packet(5, "B", 0.5f)); // another desk
        Assert.Equal(2, got.Count);
        Assert.Equal(3 * Nexora.Audio.SpeechPacket.Frames, got[1].Length);
        Assert.Equal(0f, got[1][0]);
        Assert.InRange(got[1][^1], 0.49f, 0.51f);
        Assert.Equal(2, rx.PacketsLost);
        Assert.Equal("A", rx.Following);
        Assert.Contains("B", rx.Senders);
    }

    [Fact]
    public void ResamplerTurns48kInto16k()
    {
        var tap = new Nexora.Audio.SpeechTap();
        var rs = new Nexora.Audio.SpeechResampler(48000, 480);
        var l = new float[480];
        for (var i = 0; i < l.Length; i++) l[i] = (float)Math.Sin(i * 2 * Math.PI * 440 / 48000);
        for (var k = 0; k < 10; k++) rs.Write(tap, l, null, 480);
        Assert.InRange(tap.Written, 1590, 1610); // 100 ms
        var back = new float[160];
        tap.Read(tap.Written - 160, back);
        Assert.InRange(back.Max(), 0.8f, 1.1f); // 440 Hz passes the voice filter
    }
}
