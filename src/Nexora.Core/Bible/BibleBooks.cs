namespace Nexora.Core.Bible;

/// <summary>One book of the 66-book Protestant canon.</summary>
/// <param name="Number">1 = Genesis ... 66 = Revelation.</param>
/// <param name="VerseCounts">Verses in each chapter (King James versification), used for the completeness check and navigation.</param>
public sealed record BibleBook(int Number, string Name, string ShortName, string Osis, string Usfm, string[] Aliases, int[] VerseCounts)
{
    public int Chapters => VerseCounts.Length;
    public bool IsOldTestament => Number <= 39;
    public int VersesIn(int chapter) => chapter >= 1 && chapter <= VerseCounts.Length ? VerseCounts[chapter - 1] : 0;
    public override string ToString() => Name;
}

public static class BibleBooks
{
    public static readonly IReadOnlyList<BibleBook> All =
    [
        new(1, "Genesis", "Gen", "Gen", "GEN", ["ge", "gn", "gen", "gene", "genes", "genisis", "genesis"], [31,25,24,26,32,22,24,22,29,32,32,20,18,24,21,16,27,33,38,18,34,24,20,67,34,35,46,22,35,43,55,32,20,31,29,43,36,30,23,23,57,38,34,34,28,34,31,22,33,26]),
        new(2, "Exodus", "Exod", "Exod", "EXO", ["ex", "exo", "exod", "exd", "exodus", "exodos"], [22,25,22,31,23,30,25,32,35,29,10,51,22,31,27,36,16,27,25,26,36,31,33,18,40,37,21,43,46,38,18,35,23,35,35,38,29,31,43,38]),
        new(3, "Leviticus", "Lev", "Lev", "LEV", ["le", "lv", "lev", "levi", "levit", "leviticus", "levitcus"], [17,16,17,35,19,30,38,36,24,20,47,8,59,57,33,34,16,30,37,27,24,33,44,23,55,46,34]),
        new(4, "Numbers", "Num", "Num", "NUM", ["nu", "nm", "nb", "num", "numb", "numbers"], [54,34,51,49,31,27,89,26,23,36,35,16,33,45,41,50,13,32,22,29,35,41,30,25,18,65,23,31,40,16,54,42,56,29,34,13]),
        new(5, "Deuteronomy", "Deut", "Deut", "DEU", ["de", "dt", "deu", "deut", "duet", "deuteronomy", "deutronomy", "duteronomy"], [46,37,29,49,33,25,26,20,29,22,32,32,18,29,23,22,20,22,21,20,23,30,25,22,19,19,26,68,29,20,30,52,29,12]),
        new(6, "Joshua", "Josh", "Josh", "JOS", ["jos", "josh", "jsh", "joshua", "joshuah"], [18,24,17,24,15,27,26,35,27,43,23,24,33,15,63,10,18,28,51,9,45,34,16,33]),
        new(7, "Judges", "Judg", "Judg", "JDG", ["jg", "jdg", "jdgs", "judg", "judges"], [36,23,31,24,31,40,25,35,57,18,40,15,25,20,20,31,13,31,30,48,25]),
        new(8, "Ruth", "Ruth", "Ruth", "RUT", ["ru", "rth", "rut", "ruth"], [22,23,18,22]),
        new(9, "1 Samuel", "1 Sam", "1Sam", "1SA", ["1sa", "1sm", "1s", "1sam", "1samuel", "1stsamuel", "firstsamuel"], [28,36,21,22,12,21,17,22,27,27,15,25,23,52,35,23,58,30,24,42,15,23,29,22,44,25,12,25,11,31,13]),
        new(10, "2 Samuel", "2 Sam", "2Sam", "2SA", ["2sa", "2sm", "2s", "2sam", "2samuel", "2ndsamuel", "secondsamuel"], [27,32,39,12,25,23,29,18,13,19,27,31,39,33,37,23,29,33,43,26,22,51,39,25]),
        new(11, "1 Kings", "1 Kgs", "1Kgs", "1KI", ["1k", "1ki", "1kg", "1kgs", "1kin", "1king", "1kings", "1stkings", "firstkings"], [53,46,28,34,18,38,51,66,28,29,43,33,34,31,34,34,24,46,21,43,29,53]),
        new(12, "2 Kings", "2 Kgs", "2Kgs", "2KI", ["2k", "2ki", "2kg", "2kgs", "2kin", "2king", "2kings", "2ndkings", "secondkings"], [18,25,27,44,27,33,20,29,37,36,21,21,25,29,38,20,41,37,37,21,26,20,37,20,30]),
        new(13, "1 Chronicles", "1 Chr", "1Chr", "1CH", ["1ch", "1chr", "1chron", "1chro", "1chronicles", "1stchronicles", "firstchronicles"], [54,55,24,43,26,81,40,40,44,14,47,40,14,17,29,43,27,17,19,8,30,19,32,31,31,32,34,21,30]),
        new(14, "2 Chronicles", "2 Chr", "2Chr", "2CH", ["2ch", "2chr", "2chron", "2chro", "2chronicles", "2ndchronicles", "secondchronicles"], [17,18,17,22,14,42,22,18,31,19,23,16,22,15,19,14,19,34,11,37,20,12,21,27,28,23,9,27,36,27,21,33,25,33,27,23]),
        new(15, "Ezra", "Ezra", "Ezra", "EZR", ["ezr", "ezra"], [11,70,13,24,17,22,28,36,15,44]),
        new(16, "Nehemiah", "Neh", "Neh", "NEH", ["ne", "neh", "nehe", "nehemiah", "nehemia"], [11,20,32,23,19,19,73,18,38,39,36,47,31]),
        new(17, "Esther", "Esth", "Esth", "EST", ["es", "est", "esth", "ester", "esther"], [22,23,15,17,14,14,10,17,32,3]),
        new(18, "Job", "Job", "Job", "JOB", ["jb", "job"], [22,13,26,21,27,30,21,22,35,22,20,25,28,22,35,22,16,21,29,29,34,30,17,25,6,14,23,28,25,31,40,22,33,37,16,33,24,41,30,24,34,17]),
        new(19, "Psalms", "Ps", "Ps", "PSA", ["ps", "psa", "psm", "pss", "psalm", "psalms", "pslm", "psalms", "salm", "salms"], [6,12,8,8,12,10,17,9,20,18,7,8,6,7,5,11,15,50,14,9,13,31,6,10,22,12,14,9,11,12,24,11,22,22,28,12,40,22,13,17,13,11,5,26,17,11,9,14,20,23,19,9,6,7,23,13,11,11,17,12,8,12,11,10,13,20,7,35,36,5,24,20,28,23,10,12,20,72,13,19,16,8,18,12,13,17,7,18,52,17,16,15,5,23,11,13,12,9,9,5,8,28,22,35,45,48,43,13,31,7,10,10,9,8,18,19,2,29,176,7,8,9,4,8,5,6,5,6,8,8,3,18,3,3,21,26,9,8,24,13,10,7,12,15,21,10,20,14,9,6]),
        new(20, "Proverbs", "Prov", "Prov", "PRO", ["pr", "pro", "prv", "prov", "proverb", "proverbs"], [33,22,35,27,23,35,27,36,18,32,31,28,25,35,33,33,28,24,29,30,31,29,35,34,28,28,27,28,27,33,31]),
        new(21, "Ecclesiastes", "Eccl", "Eccl", "ECC", ["ec", "ecc", "eccl", "eccles", "qoh", "ecclesiastes", "ecclesiates"], [18,26,22,16,20,12,29,17,18,20,10,14]),
        new(22, "Song of Solomon", "Song", "Song", "SNG", ["so", "sos", "sng", "song", "songs", "songofsolomon", "songofsongs", "canticles", "cant", "ss"], [17,17,11,16,16,13,13,14]),
        new(23, "Isaiah", "Isa", "Isa", "ISA", ["is", "isa", "isaiah", "isiah", "isaih"], [31,22,26,6,30,13,25,22,21,34,16,6,22,32,9,14,14,7,25,6,17,25,18,23,12,21,13,29,24,33,9,20,24,17,10,22,38,22,8,31,29,25,28,28,25,13,15,22,26,11,23,15,12,17,13,12,21,14,21,22,11,12,19,12,25,24]),
        new(24, "Jeremiah", "Jer", "Jer", "JER", ["je", "jr", "jer", "jere", "jeremiah", "jeremia"], [19,37,25,31,31,30,34,22,26,25,23,17,27,22,21,21,27,23,15,18,14,30,40,10,38,24,22,17,32,24,40,44,26,22,19,32,21,28,18,16,18,22,13,30,5,28,7,47,39,46,64,34]),
        new(25, "Lamentations", "Lam", "Lam", "LAM", ["la", "lam", "lament", "lamentations"], [22,22,66,22,22]),
        new(26, "Ezekiel", "Ezek", "Ezek", "EZK", ["ezk", "eze", "ezek", "ezekiel", "ezekial"], [28,10,27,17,17,14,27,18,11,22,25,28,23,23,8,63,24,32,14,49,32,31,49,27,17,21,36,26,21,26,18,32,33,31,15,38,28,23,29,49,26,20,27,31,25,24,23,35]),
        new(27, "Daniel", "Dan", "Dan", "DAN", ["da", "dn", "dan", "daniel"], [21,49,30,37,31,28,28,27,27,21,45,13]),
        new(28, "Hosea", "Hos", "Hos", "HOS", ["ho", "hos", "hosea"], [11,23,5,19,15,11,16,14,17,15,12,14,16,9]),
        new(29, "Joel", "Joel", "Joel", "JOL", ["jl", "joe", "joel"], [20,32,21]),
        new(30, "Amos", "Amos", "Amos", "AMO", ["am", "amo", "amos"], [15,16,15,13,27,14,17,14,15]),
        new(31, "Obadiah", "Obad", "Obad", "OBA", ["ob", "oba", "obad", "obadiah"], [21]),
        new(32, "Jonah", "Jonah", "Jonah", "JON", ["jon", "jnh", "jonah"], [17,10,10,11]),
        new(33, "Micah", "Mic", "Mic", "MIC", ["mi", "mc", "mic", "micah"], [16,13,12,13,15,16,20]),
        new(34, "Nahum", "Nah", "Nah", "NAM", ["na", "nah", "nam", "nahum"], [15,13,19]),
        new(35, "Habakkuk", "Hab", "Hab", "HAB", ["hb", "hab", "habak", "habakkuk", "habakuk"], [17,20,19]),
        new(36, "Zephaniah", "Zeph", "Zeph", "ZEP", ["zp", "zep", "zeph", "zephaniah"], [18,15,20]),
        new(37, "Haggai", "Hag", "Hag", "HAG", ["hg", "hag", "hagg", "haggai"], [15,23]),
        new(38, "Zechariah", "Zech", "Zech", "ZEC", ["zc", "zec", "zech", "zechariah", "zachariah"], [21,13,10,14,11,15,14,23,17,12,17,14,9,21]),
        new(39, "Malachi", "Mal", "Mal", "MAL", ["ml", "mal", "malachi"], [14,17,18,6]),
        new(40, "Matthew", "Matt", "Matt", "MAT", ["mt", "mat", "matt", "mathew", "matthew"], [25,23,17,25,48,34,29,34,38,42,30,50,58,36,39,28,27,35,30,34,46,46,39,51,46,75,66,20]),
        new(41, "Mark", "Mark", "Mark", "MRK", ["mk", "mr", "mrk", "mar", "mark"], [45,28,35,41,43,56,37,38,50,52,33,44,37,72,47,20]),
        new(42, "Luke", "Luke", "Luke", "LUK", ["lk", "lu", "luk", "luke"], [80,52,38,44,39,49,50,56,62,42,54,59,35,35,32,31,37,43,48,47,38,71,56,53]),
        new(43, "John", "John", "John", "JHN", ["jn", "jhn", "joh", "john"], [51,25,36,54,47,71,53,59,41,42,57,50,38,31,27,33,26,40,42,31,25]),
        new(44, "Acts", "Acts", "Acts", "ACT", ["ac", "act", "acts"], [26,47,26,37,42,15,60,40,43,48,30,25,52,28,41,40,34,28,41,38,40,30,35,27,27,32,44,31]),
        new(45, "Romans", "Rom", "Rom", "ROM", ["ro", "rm", "rom", "roman", "romans"], [32,29,31,25,21,23,25,39,33,21,36,21,14,23,33,27]),
        new(46, "1 Corinthians", "1 Cor", "1Cor", "1CO", ["1co", "1cor", "1corinth", "1corinthians", "1stcorinthians", "firstcorinthians"], [31,16,23,21,13,20,40,13,27,33,34,31,13,40,58,24]),
        new(47, "2 Corinthians", "2 Cor", "2Cor", "2CO", ["2co", "2cor", "2corinth", "2corinthians", "2ndcorinthians", "secondcorinthians"], [24,17,18,18,21,18,16,24,15,18,33,21,14]),
        new(48, "Galatians", "Gal", "Gal", "GAL", ["ga", "gal", "galatians"], [24,21,29,31,26,18]),
        new(49, "Ephesians", "Eph", "Eph", "EPH", ["ep", "eph", "ephes", "ephesians"], [23,22,21,32,33,24]),
        new(50, "Philippians", "Phil", "Phil", "PHP", ["php", "phil", "philip", "philippians", "phillipians", "philipians"], [30,30,21,23]),
        new(51, "Colossians", "Col", "Col", "COL", ["co", "col", "colossians", "colosians"], [29,23,25,18]),
        new(52, "1 Thessalonians", "1 Thess", "1Thess", "1TH", ["1th", "1thes", "1thess", "1thessalonians", "1stthessalonians", "firstthessalonians"], [10,20,13,18,28]),
        new(53, "2 Thessalonians", "2 Thess", "2Thess", "2TH", ["2th", "2thes", "2thess", "2thessalonians", "2ndthessalonians", "secondthessalonians"], [12,17,18]),
        new(54, "1 Timothy", "1 Tim", "1Tim", "1TI", ["1ti", "1tim", "1timothy", "1sttimothy", "firsttimothy"], [20,15,16,16,25,21]),
        new(55, "2 Timothy", "2 Tim", "2Tim", "2TI", ["2ti", "2tim", "2timothy", "2ndtimothy", "secondtimothy"], [18,26,17,22]),
        new(56, "Titus", "Titus", "Titus", "TIT", ["ti", "tit", "titus"], [16,15,15]),
        new(57, "Philemon", "Phlm", "Phlm", "PHM", ["phm", "phlm", "philem", "philemon"], [25]),
        new(58, "Hebrews", "Heb", "Heb", "HEB", ["he", "heb", "hebrew", "hebrews"], [14,18,19,16,14,20,28,13,28,39,40,29,25]),
        new(59, "James", "Jas", "Jas", "JAS", ["jas", "jm", "jam", "james"], [27,26,18,17,20]),
        new(60, "1 Peter", "1 Pet", "1Pet", "1PE", ["1p", "1pe", "1pet", "1pt", "1peter", "1stpeter", "firstpeter"], [25,25,22,19,14]),
        new(61, "2 Peter", "2 Pet", "2Pet", "2PE", ["2p", "2pe", "2pet", "2pt", "2peter", "2ndpeter", "secondpeter"], [21,22,18]),
        new(62, "1 John", "1 John", "1John", "1JN", ["1j", "1jn", "1jo", "1joh", "1john", "1stjohn", "firstjohn"], [10,29,24,21,21]),
        new(63, "2 John", "2 John", "2John", "2JN", ["2j", "2jn", "2jo", "2joh", "2john", "2ndjohn", "secondjohn"], [13]),
        new(64, "3 John", "3 John", "3John", "3JN", ["3j", "3jn", "3jo", "3joh", "3john", "3rdjohn", "thirdjohn"], [14]),
        new(65, "Jude", "Jude", "Jude", "JUD", ["jude", "jd"], [25]),
        new(66, "Revelation", "Rev", "Rev", "REV", ["re", "rev", "rv", "revelation", "revelations", "revalation", "revelationofjohn", "apocalypse"], [20,29,22,11,14,17,17,13,21,11,19,17,18,20,8,21,18,24,21,15,27,21]),
    ];

    public const int TotalVerses = 31102;

    public static BibleBook Get(int number) => All[number - 1];

    public static BibleBook? TryGet(int number) => number is >= 1 and <= 66 ? All[number - 1] : null;

    /// <summary>Finds a book from an OSIS or USFM code ("Gen", "GEN", "1Cor", "1CO"), any case.</summary>
    public static BibleBook? FromCode(string code)
    {
        var c = code.Trim();
        return All.FirstOrDefault(b => string.Equals(b.Osis, c, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(b.Usfm, c, StringComparison.OrdinalIgnoreCase));
    }
}
