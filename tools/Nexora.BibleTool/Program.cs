using System.Text.Json;
using System.Text.RegularExpressions;
using Nexora.Core.Bible;

// NEXORA Bible tool.
//   check <file>...                     import each file and print the verse check
//   prepare <sources.json> <sourceDir> <outDir>
//                                       import the church's translation files, apply the listed
//                                       repairs, check every verse and write the bundled .json.gz files

if (args.Length >= 2 && args[0] == "check")
{
    var ok = true;
    foreach (var path in args.Skip(1)) ok &= Report(path, BibleImport.Load(path).Bible);
    return ok ? 0 : 1;
}

if (args.Length == 4 && args[0] == "prepare")
{
    var sources = JsonSerializer.Deserialize<List<Source>>(File.ReadAllText(args[1]), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Directory.CreateDirectory(args[3]);
    var ok = true;
    foreach (var s in sources)
    {
        var path = Path.Combine(args[2], s.File);
        var bible = BibleImport.Load(path).Bible;
        bible.Abbrev = s.Abbrev;
        bible.Name = s.Name;
        bible.Language = s.Language ?? "English";
        // Some files run the next verse into the one before ("... to the Lord. 1:17 The LORD sent ...").
        foreach (var moved in SplitEmbedded(bible)) Console.WriteLine($"       split: {moved}");
        foreach (var j in s.Joins ?? [])
        {
            var r = Ref(j);
            if (!string.IsNullOrEmpty(bible.Get(r.b, r.c, r.v))) throw new InvalidDataException($"{s.Abbrev} {j} has text; it cannot be joined.");
            bible.Join(r.b, r.c, r.v);
        }
        foreach (var (key, text) in s.Fixes ?? [])
        {
            var r = Ref(key);
            bible.Set(r.b, r.c, r.v, text);
        }
        ok &= Report(s.File, bible);
        BibleBundle.Write(bible, Path.Combine(args[3], s.Abbrev + ".json.gz"));
    }
    return ok ? 0 : 1;
}

Console.Error.WriteLine("usage: check <file>... | prepare <sources.json> <sourceDir> <outDir>");
return 2;

static (int b, int c, int v) Ref(string text)
{
    var p = ReferenceParser.Parse(text);
    if (!p.Ok || !p.Reference!.First.IsSingleVerse) throw new InvalidDataException($"Not a single verse: {text} ({p.Error})");
    return (p.Reference.Book.Number, p.Reference.First.StartChapter, p.Reference.First.StartVerse);
}

static List<string> SplitEmbedded(BibleText bible)
{
    var log = new List<string>();
    foreach (var v in bible.All().ToList())
    {
        var m = Regex.Match(v.Text, @"(?<=[^\d\s:])\s?(\d{1,3}):(\d{1,3})\s+(?=\S)");
        if (!m.Success) continue;
        var c = int.Parse(m.Groups[1].Value);
        var n = int.Parse(m.Groups[2].Value);
        // Only the very next verse, which must be missing ("119:113" inside 119:112).
        var isNext = (c == v.Chapter && n == v.Verse + 1) || (c == v.Chapter + 1 && n == 1);
        if (!isNext) continue;
        if (!string.IsNullOrEmpty(bible.Get(v.Book, c, n))) continue;
        var tail = v.Text[(m.Index + m.Length)..];
        bible.Set(v.Book, v.Chapter, v.Verse, v.Text[..m.Index]);
        bible.Set(v.Book, c, n, tail);
        log.Add($"{BibleBooks.Get(v.Book).Name} {c}:{n} was inside {v.Chapter}:{v.Verse}");
    }
    return log;
}

static bool Report(string name, BibleText bible)
{
    var report = BibleCheck.Run(bible);
    var damaged = bible.All().Where(v => Regex.IsMatch(v.Text, @"vnumber|</?[A-Za-z]+[\s>]|[<>]"))
        .Select(v => $"{BibleBooks.Get(v.Book).Name} {v.Chapter}:{v.Verse}").ToList();
    Console.WriteLine($"{bible.Abbrev,-6} {name}: {bible.Count:N0} verses. {report.Summary}");
    if (report.Missing.Count > 0) Console.WriteLine("       missing: " + string.Join(", ", report.Missing.Take(30)));
    if (damaged.Count > 0) Console.WriteLine("       damaged: " + string.Join(", ", damaged.Take(30)));
    return report.Complete && damaged.Count == 0;
}

record Source(string File, string Abbrev, string Name, string? Language, List<string>? Joins, Dictionary<string, string>? Fixes);
