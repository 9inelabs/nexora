using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Nexora.Core.Bible;

namespace Nexora.Tests;

public class ReferenceParserTests
{
    [Theory]
    [InlineData("jn 3 16", "John 3:16")]
    [InlineData("john 3:16", "John 3:16")]
    [InlineData("Jn3.16", "John 3:16")]
    [InlineData("JOHN 3 16", "John 3:16")]
    [InlineData("1 cor 13 4-7", "1 Corinthians 13:4-7")]
    [InlineData("1co 13:4-7", "1 Corinthians 13:4-7")]
    [InlineData("1cor13:4-7", "1 Corinthians 13:4-7")]
    [InlineData("first corinthians 13 4", "1 Corinthians 13:4")]
    [InlineData("ps 23", "Psalms 23")]
    [InlineData("psalm 23:1-6", "Psalms 23")]
    [InlineData("rev 22 21", "Revelation 22:21")]
    [InlineData("i john 1 9", "1 John 1:9")]
    [InlineData("ii tim 3 16", "2 Timothy 3:16")]
    [InlineData("2 tim 3:16-17", "2 Timothy 3:16-17")]
    [InlineData("isaiah 53 5", "Isaiah 53:5")]
    [InlineData("isa 40:31", "Isaiah 40:31")]
    [InlineData("song of solomon 2 1", "Song of Solomon 2:1")]
    [InlineData("phil 4 13", "Philippians 4:13")]
    [InlineData("phm 6", "Philemon 6")]
    [InlineData("jude 3", "Jude 3")]
    [InlineData("jude 1:24-25", "Jude 24-25")]
    [InlineData("john 3:16-4:2", "John 3:16-4:2")]
    [InlineData("rom 8:28, 31", "Romans 8:28, 31")]
    [InlineData("rom 8 28,31-32", "Romans 8:28, 31-32")]
    [InlineData("mathew 5 3", "Matthew 5:3")]
    [InlineData("revalation 21 4", "Revelation 21:4")]
    [InlineData("genisis 1 1", "Genesis 1:1")]
    [InlineData("gen 1", "Genesis 1")]
    [InlineData("1 sam 17 45", "1 Samuel 17:45")]
    [InlineData("heb 11:1", "Hebrews 11:1")]
    [InlineData("ps 119:105", "Psalms 119:105")]
    [InlineData("ps 23-24", "Psalms 23-24")]
    [InlineData("eccl 3 1-8", "Ecclesiastes 3:1-8")]
    [InlineData("judg 6 12", "Judges 6:12")]
    [InlineData("ezek 37 4", "Ezekiel 37:4")]
    [InlineData("jhn 14 6", "John 14:6")]
    [InlineData("mk 16 15", "Mark 16:15")]
    [InlineData("acts 2 38", "Acts 2:38")]
    [InlineData("john 3 v 16", "John 3:16")]
    [InlineData("John 3:16–18", "John 3:16-18")]
    [InlineData("ps 23:1-10", "Psalms 23")]
    public void UnderstandsHowPeopleType(string typed, string expected)
    {
        var sw = Stopwatch.StartNew();
        var r = ReferenceParser.Parse(typed);
        sw.Stop();
        Assert.True(r.Ok, $"{typed}: {r.Error}");
        Assert.Equal(expected, r.Reference!.ToString());
        Assert.True(sw.ElapsedMilliseconds < 250, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("jn 22 1", "John has 21 chapters.")]
    [InlineData("john 3 37", "John 3 has 36 verses.")]
    [InlineData("jude 2 1", "Jude has only one chapter.")]
    [InlineData("hezron 3 1", "No book called")]
    [InlineData("", "Type a book")]
    public void ExplainsMistakes(string typed, string start)
    {
        var r = ReferenceParser.Parse(typed);
        Assert.False(r.Ok);
        Assert.StartsWith(start, r.Error);
    }

    [Fact]
    public void AmbiguousBookOffersChoices()
    {
        var r = ReferenceParser.Parse("jo 3 16");
        Assert.False(r.Ok);
        var names = r.Suggestions.Select(b => b.Name).ToList();
        Assert.Contains("John", names);
        Assert.Contains("Job", names);
        Assert.Contains("Joshua", names);
    }

    [Fact]
    public void SuggestsBooksWhileTyping()
    {
        var names = ReferenceParser.Suggest("jo").Select(b => b.Name).ToList();
        Assert.Equal(["Joshua", "Job", "Joel", "Jonah", "John"], names);
        Assert.Equal("Philemon", ReferenceParser.Suggest("phile").Single().Name);
        Assert.Equal("Romans", ReferenceParser.Suggest("rom 8").Single().Name);
    }

    [Fact]
    public void FindsEveryVerseOfARange()
    {
        var r = ReferenceParser.Parse("john 3:35-4:2").Reference!;
        Assert.Equal([(3, 35), (3, 36), (4, 1), (4, 2)], r.Verses().ToList());
    }

    [Fact]
    public void ReadsPastorsNotes()
    {
        var notes = """
            Opening: Psalm 23
            John 3:16-21 Sermon text
            Then read Romans 8:28 and 1 Cor 13 4-7
            Closing prayer
            heb 11 1
            """;
        var list = ReferenceParser.ExtractAll(notes);
        Assert.Equal(["Psalms 23", "John 3:16-21", "Romans 8:28", "1 Corinthians 13:4-7", "Hebrews 11:1"],
            list.Select(x => x.Reference.ToString()).ToList());
        Assert.Equal("Opening", list[0].Note);
        Assert.Equal("Sermon text", list[1].Note);
    }

    [Fact]
    public void EveryBookIsFoundByItsName()
    {
        foreach (var b in BibleBooks.All)
        {
            Assert.Equal(b, ReferenceParser.Parse(b.Name + " 1 1").Reference?.Book);
            Assert.Equal(b, ReferenceParser.Parse(b.ShortName + " 1").Reference?.Book);
        }
        Assert.Equal(BibleBooks.TotalVerses, BibleBooks.All.Sum(b => b.VerseCounts.Sum()));
    }
}

public class BibleLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexora-bible-" + Guid.NewGuid().ToString("N"));

    public static string BundledDir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Nexora.sln"))) d = d.Parent;
            return Path.Combine(d!.FullName, "src", "Nexora.App", "Bibles");
        }
    }

    [Fact]
    public void BundledTranslationsAreCompleteGenesisToRevelation()
    {
        var files = Directory.GetFiles(BundledDir, "*.json.gz");
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var result = BibleImport.Load(f);
            Assert.True(result.Report.Complete, $"{Path.GetFileName(f)}: {result.Report.Summary}");
            Assert.Equal(BibleBooks.TotalVerses, result.Report.Present);
            Assert.True(result.Report.Omitted.Count <= BibleCheck.OftenOmitted.Count);
            Assert.NotNull(result.Bible.Get(1, 1, 1));
            Assert.NotNull(result.Bible.Get(66, 22, 21));
        }
    }

    [Fact]
    public void PreparesOpensAndSearches()
    {
        using var lib = new BibleLibrary(BundledDir, _dir);
        var sw = Stopwatch.StartNew();
        lib.Load();
        Assert.Null(lib.LoadError);
        Assert.Contains(lib.All, t => t.Abbrev == "KJV");
        var kjv = lib.Get("kjv")!;
        Assert.True(kjv.Check().Complete);
        Assert.StartsWith("For God so loved the world", kjv.Verse(43, 3, 16));
        Assert.Equal(36, kjv.VersesIn(43, 3));

        sw.Restart();
        var hits = kjv.Search("faith hope charity");
        Assert.True(sw.ElapsedMilliseconds < 250, $"search took {sw.ElapsedMilliseconds} ms");
        Assert.Contains(hits, h => h is { Book: 46, Chapter: 13, Verse: 13 });
        Assert.Contains(kjv.Search("\"in the beginning was the word\""), h => h is { Book: 43, Chapter: 1, Verse: 1 });
        Assert.Empty(kjv.Search("   "));
        Assert.Empty(kjv.Search("\"unbalanced"));

        // Second start reuses the prepared files.
        sw.Restart();
        using var again = new BibleLibrary(BundledDir, _dir);
        again.Load();
        Assert.True(sw.ElapsedMilliseconds < 3000, $"reopen took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ImportsCommonFormats()
    {
        Directory.CreateDirectory(_dir);
        var samples = new Dictionary<string, string>
        {
            ["osis.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <osis xmlns="http://www.bibletechnologies.net/2003/OSIS/namespace"><osisText osisIDWork="TST" xml:lang="en">
                <header><work osisWork="TST"><title>Test Bible</title></work></header>
                <div type="book" osisID="John"><chapter osisID="John.3">
                <verse osisID="John.3.16">For God so loved<note>a note</note> the world.</verse>
                <verse sID="John.3.17" osisID="John.3.17"/>Milestone <w lemma="x">verse</w>.<verse eID="John.3.17"/>
                </chapter></div></osisText></osis>
                """,
            ["zef.xml"] = """
                <XMLBIBLE biblename="Zef"><INFORMATION><title>Zef Bible</title><identifier>ZEF</identifier></INFORMATION>
                <BIBLEBOOK bnumber="43" bname="John"><CHAPTER cnumber="3"><VERS vnumber="16">For God so loved the world.</VERS><VERS vnumber="17">Seventeen.</VERS></CHAPTER></BIBLEBOOK></XMLBIBLE>
                """,
            ["opensong.xml"] = """
                <bible><b n="John"><c n="3"><v n="16">For God so loved the world.</v><v n="17">Seventeen.</v></c></b></bible>
                """,
            ["holy.xml"] = """
                <bible translation="Holy"><testament name="New"><book number="43"><chapter number="3"><verse number="16">For God so loved the world.</verse><verse number="17">Seventeen.</verse></chapter></book></testament></bible>
                """,
            ["book.usfm"] = "\\id JHN\n\\h John\n\\c 3\n\\p\n\\v 16 For God so \\w loved|strong=\"G25\"\\w* the world.\\f + \\fr 3:16 \\ft note\\f*\n\\v 17 Seventeen.\n",
            ["rows.csv"] = "book,chapter,verse,text\nJohn,3,16,\"For God so loved the world.\"\n43,3,17,Seventeen.\n",
            ["rows.tsv"] = "John\t3\t16\tFor God so loved the world.\nJohn\t3\t17\tSeventeen.\n",
            ["rows.json"] = """[{"book":"John","chapter":3,"verse":16,"text":"For God so loved the world."},{"book_id":43,"chapter":3,"verse":17,"text":"Seventeen."}]""",
        };
        foreach (var (name, content) in samples)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            var r = BibleImport.Load(path);
            Assert.True(r.Bible.Get(43, 3, 16)?.StartsWith("For God so loved the world") == true, $"{name}: '{r.Bible.Get(43, 3, 16)}'");
            Assert.NotNull(r.Bible.Get(43, 3, 17));
            Assert.False(r.Report.Complete);
            Assert.DoesNotContain("note", r.Bible.Get(43, 3, 16));
        }
        Assert.Equal("Milestone verse.", BibleImport.Load(Path.Combine(_dir, "osis.xml")).Bible.Get(43, 3, 17));
        Assert.Equal("TST", BibleImport.Load(Path.Combine(_dir, "osis.xml")).Bible.Abbrev);
        Assert.Equal("ZEF", BibleImport.Load(Path.Combine(_dir, "zef.xml")).Bible.Abbrev);

        // A zip of USFM books.
        var zipPath = Path.Combine(_dir, "usfm.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("44JHNtest.usfm");
            using var w = new StreamWriter(e.Open());
            w.Write(samples["book.usfm"]);
        }
        Assert.NotNull(BibleImport.Load(zipPath).Bible.Get(43, 3, 17));
    }

    [Fact]
    public void ImportedTranslationRoundTripsAndReplacesNothingBundled()
    {
        using var lib = new BibleLibrary(Path.Combine(_dir, "none"), _dir);
        lib.Load();
        Assert.Empty(lib.All);
        var t = new BibleText { Abbrev = "TST", Name = "Test" };
        t.Add(43, 3, 16, "Text");
        lib.SaveImported(t, "test.csv", "abc");
        Assert.Equal("Text", lib.Get("TST")!.Verse(43, 3, 16));
        var file = lib.ImportedFiles().Single();
        Assert.Equal("abc", file.Info.Hash);

        // Re-import the .nxbible itself.
        var r = BibleImport.Load(file.Path);
        Assert.Equal("TST", r.Bible.Abbrev);
        Assert.True(lib.RemoveImported("TST"));
        Assert.Empty(lib.All);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
