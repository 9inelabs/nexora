using Nexora.Core.Bible;

namespace Nexora.Core.Speech;

/// <summary>Turns a sentence into a vector of numbers whose direction carries its meaning (a small local model).</summary>
public interface IEmbedder
{
    int Dimensions { get; }
    /// <summary>The unit-length meaning vector of <paramref name="text"/>.</summary>
    float[] Embed(string text);
}

/// <summary>A passage the pastor may be talking about, with how sure NEXORA is (0 to 1).</summary>
public sealed record MeaningMatch(BibleReference Reference, string Title, double Score, string Why);

/// <summary>Words that carry no meaning on their own, left out of the search.</summary>
public static class StopWords
{
    public static readonly HashSet<string> All =
    [
        "a", "an", "the", "and", "or", "but", "if", "then", "so", "of", "to", "in", "on", "at", "by", "for", "with", "from", "into",
        "is", "are", "was", "were", "be", "been", "being", "am", "do", "does", "did", "done", "have", "has", "had", "having",
        "i", "me", "my", "we", "us", "our", "you", "your", "ye", "thou", "thee", "thy", "he", "him", "his", "she", "her", "it", "its",
        "they", "them", "their", "this", "that", "these", "those", "there", "here", "what", "which", "who", "whom", "when", "where",
        "why", "how", "all", "any", "some", "no", "not", "nor", "only", "own", "same", "such", "too", "very", "can", "will", "would",
        "shall", "should", "may", "might", "must", "could", "let", "lets", "go", "going", "get", "got", "say", "said", "says", "saying",
        "tell", "told", "know", "think", "now", "just", "also", "even", "like", "unto", "upon", "out", "up", "down", "about", "over",
        "again", "one", "two", "come", "came", "make", "made", "see", "look", "yes", "oh", "amen", "hallelujah", "praise", "church",
        "people", "today", "morning", "somebody", "everybody", "everyone", "brethren", "brothers", "sisters", "pastor", "bible",
        "verse", "verses", "chapter", "book", "read", "reading", "turn", "open", "please", "thing", "things", "way", "day", "time",
        "really", "because", "therefore", "hath", "doth", "shalt", "art", "wilt", "thereof", "lord", "god", "jesus", "christ",
        "well", "want", "need", "remember", "story", "when", "talk", "talking", "spoke", "speak", "man", "men", "him", "himself",
    ];

    /// <summary>A light stem so "believeth", "believed", "believing" and "believes" meet.</summary>
    public static string Stem(string w)
    {
        if (w.Length <= 4) return w;
        foreach (var end in new[] { "eth", "est", "ing", "edst", "ed", "es", "s", "ly" })
        {
            if (w.Length - end.Length >= 4 && w.EndsWith(end, StringComparison.Ordinal))
                return w[..^end.Length];
        }
        return w;
    }

    public static IEnumerable<string> Terms(string text)
    {
        foreach (var t in SpokenText.Tokens(text))
        {
            if (t.Length < 3 || SpokenText.IsNumber(t) || All.Contains(t)) continue;
            yield return Stem(t);
        }
    }
}

/// <summary>
/// Searches every verse by its words, across several translations at once (old and modern wording, so
/// "fed the five thousand" meets "they all ate and were satisfied"). Ranked with BM25, the ranking web
/// search engines use. Built once in memory (about a second).
/// </summary>
public sealed class VerseIndex
{
    private readonly Dictionary<string, (int[] Docs, byte[] Counts)> _postings;
    private readonly long[] _ids;
    private readonly int[] _lengths;
    private readonly double _avgLength;

    private VerseIndex(Dictionary<string, (int[], byte[])> postings, long[] ids, int[] lengths)
    {
        _postings = postings;
        _ids = ids;
        _lengths = lengths;
        _avgLength = lengths.Length == 0 ? 1 : lengths.Average();
    }

    public int Count => _ids.Length;

    /// <summary>Builds the index from verse texts: (book, chapter, verse) → the texts of that verse in each translation.</summary>
    public static VerseIndex Build(IEnumerable<(int Book, int Chapter, int Verse, string Text)> verses)
    {
        var byVerse = new Dictionary<long, Dictionary<string, int>>();
        foreach (var (b, c, v, text) in verses)
        {
            var id = b * 1_000_000L + c * 1000L + v;
            if (!byVerse.TryGetValue(id, out var counts)) byVerse[id] = counts = [];
            foreach (var term in StopWords.Terms(text)) counts[term] = counts.GetValueOrDefault(term) + 1;
        }
        var ids = byVerse.Keys.OrderBy(x => x).ToArray();
        var lengths = new int[ids.Length];
        var tmp = new Dictionary<string, List<(int, byte)>>();
        for (var d = 0; d < ids.Length; d++)
        {
            var counts = byVerse[ids[d]];
            lengths[d] = counts.Values.Sum();
            foreach (var (term, n) in counts)
            {
                if (!tmp.TryGetValue(term, out var l)) tmp[term] = l = [];
                l.Add((d, (byte)Math.Min(n, 255)));
            }
        }
        var postings = tmp.ToDictionary(kv => kv.Key, kv => (kv.Value.Select(x => x.Item1).ToArray(), kv.Value.Select(x => x.Item2).ToArray()));
        return new VerseIndex(postings, ids, lengths);
    }

    /// <summary>The best verses for the words in <paramref name="text"/>, with their BM25 score and how many words met.</summary>
    public IReadOnlyList<(long Id, double Score, int Matched)> Search(string text, int limit = 20)
    {
        const double k1 = 1.2, b = 0.75;
        var terms = StopWords.Terms(text).Distinct().ToList();
        var scores = new Dictionary<int, (double S, int M)>();
        var n = _ids.Length;
        foreach (var term in terms)
        {
            if (!_postings.TryGetValue(term, out var p)) continue;
            // Words in more than 1 verse in 20 say little about which passage it is.
            if (p.Docs.Length > n / 20) continue;
            var idf = Math.Log(1 + (n - p.Docs.Length + 0.5) / (p.Docs.Length + 0.5));
            for (var i = 0; i < p.Docs.Length; i++)
            {
                var d = p.Docs[i];
                var tf = p.Counts[i];
                var s = idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * _lengths[d] / _avgLength));
                var cur = scores.GetValueOrDefault(d);
                scores[d] = (cur.S + s, cur.M + 1);
            }
        }
        return scores.OrderByDescending(kv => kv.Value.S).Take(limit).Select(kv => (_ids[kv.Key], kv.Value.S, kv.Value.M)).ToList();
    }

    /// <summary>How rare a word is across the verses (higher is rarer), or 0 when no verse has it.</summary>
    public double Rarity(string term)
    {
        var t = StopWords.Stem(term);
        if (!_postings.TryGetValue(t, out var p)) return 0;
        return Math.Log(1 + (_ids.Length - p.Docs.Length + 0.5) / (p.Docs.Length + 0.5));
    }
}

/// <summary>
/// "What is the pastor talking about?" Listens to the last few sentences and suggests passages: first the
/// well-known stories ("the prodigal son"), then single verses by their words, and, when the small meaning model
/// is on this PC, by meaning too. Suggestions only: nothing found here is ever sent to the screen by itself.
/// </summary>
public sealed class MeaningSearch
{
    private readonly VerseIndex _index;
    private readonly Func<long, string?> _text;
    private readonly IEmbedder? _embedder;
    private readonly VerseVectors? _vectors;
    private readonly List<(BibleStory Story, HashSet<string> Terms, float[]? Vector)> _stories;
    private readonly Dictionary<string, double> _storyRarity;

    private readonly QuoteIndex? _quotes;

    public MeaningSearch(VerseIndex index, Func<long, string?> verseText, IEmbedder? embedder = null, VerseVectors? vectors = null, QuoteIndex? quotes = null)
    {
        _index = index;
        _quotes = quotes;
        _text = verseText;
        _embedder = embedder;
        _vectors = vectors is not null && embedder is not null && vectors.Dimensions == embedder.Dimensions ? vectors : null;
        _stories = BibleStories.All.Select(s =>
        {
            var terms = StopWords.Terms(s.Title + " " + s.Words).ToHashSet();
            float[]? v = null;
            if (_embedder is not null)
            {
                try { v = _embedder.Embed(s.Title + ". " + s.Words); } catch (Exception) { v = null; }
            }
            return (s, terms, v);
        }).ToList();
        var df = new Dictionary<string, int>();
        foreach (var (_, terms, _) in _stories)
            foreach (var t in terms) df[t] = df.GetValueOrDefault(t) + 1;
        _storyRarity = df.ToDictionary(kv => kv.Key, kv => Math.Log(1 + (_stories.Count - kv.Value + 0.5) / (kv.Value + 0.5)));
    }

    public bool UsesMeaningModel => _vectors is not null;

    /// <summary>The best few passages for what was just said, most likely first, at or above <paramref name="minScore"/>.</summary>
    public IReadOnlyList<MeaningMatch> Search(string spoken, int limit = 3, double minScore = 0.5)
    {
        var heard = StopWords.Terms(spoken).ToHashSet();
        if (heard.Count < 2 && _quotes is null) return [];
        float[]? q = null;
        if (_embedder is not null)
        {
            try { q = _embedder.Embed(spoken); } catch (Exception) { q = null; }
        }

        var results = new List<MeaningMatch>();

        // 0. A verse quoted word for word ("for God so loved the world that he gave…").
        if (_quotes is not null)
        {
            foreach (var (id, share) in _quotes.Find(spoken, 2))
            {
                var book = BibleBooks.Get((int)(id / 1_000_000));
                int c = (int)(id / 1000 % 1000), v = (int)(id % 1000);
                var text = _text(id) ?? "";
                results.Add(new MeaningMatch(new BibleReference(book, [new VerseRange(c, v, c, v)]), text.Length > 70 ? text[..70] + "…" : text,
                    Math.Min(1, 0.7 + share * 0.3), "Quoted"));
            }
        }

        // 1. Stories by name and by the words people use for them.
        foreach (var (story, terms, vec) in _stories)
        {
            var met = terms.Where(heard.Contains).ToList();
            var weight = met.Sum(t => _storyRarity.GetValueOrDefault(t));
            var rare = met.Count(t => _storyRarity.GetValueOrDefault(t) > 3.5 && _index.Rarity(t) is 0 or > 6);
            var title = StopWords.Terms(story.Title).ToList();
            var titleMet = title.Count > 0 && title.All(heard.Contains);
            // Two words that belong to the story, one of them a name or rare word (or the whole title).
            if (!(titleMet && title.Count >= 2) && (met.Count < 2 || rare == 0) && !(met.Count >= 3 && weight > 9)) continue;
            var score = Math.Min(1, 0.25 + weight / 14 + (titleMet ? 0.25 : 0));
            if (q is not null && vec is not null) score = 0.75 * score + 0.25 * Math.Max(0, Dot(q, vec)) * 1.4;
            results.Add(new MeaningMatch(story.Reference, story.Title, Math.Min(1, score), "Story: " + string.Join(", ", met.Take(4))));
        }

        // 2. Single verses by their words (and meaning).
        var hits = _index.Search(spoken, 25);
        var best = hits.Count > 0 ? hits[0].Score : 0;
        var candidates = new Dictionary<long, (double Words, double Meaning, int Matched)>();
        foreach (var (id, s, m) in hits)
            if (m >= 3) candidates[id] = (s, 0, m);
        if (q is not null && _vectors is not null)
        {
            foreach (var (id, sim) in _vectors.Nearest(q, 15))
            {
                var cur = candidates.GetValueOrDefault(id);
                candidates[id] = (cur.Words, sim, cur.Matched);
            }
            foreach (var id in candidates.Keys.ToList())
            {
                var cur = candidates[id];
                if (cur.Meaning == 0) candidates[id] = cur with { Meaning = _vectors.Similarity(q, id) };
            }
        }
        foreach (var (id, (words, meaning, matched)) in candidates)
        {
            // Words: how many met and how rare they were; meaning: how close the sense is.
            var w = Math.Min(1, words / 30.0) * Math.Min(1, matched / 5.0);
            var score = _vectors is null ? w : 0.55 * w + 0.45 * Math.Clamp((meaning - 0.35) / 0.35, 0, 1);
            if (score < minScore * 0.8) continue;
            var book = BibleBooks.Get((int)(id / 1_000_000));
            var c = (int)(id / 1000 % 1000);
            var v = (int)(id % 1000);
            var r = new BibleReference(book, [new VerseRange(c, v, c, v)]);
            var text = _text(id) ?? "";
            results.Add(new MeaningMatch(r, text.Length > 70 ? text[..70] + "…" : text, score, matched > 0 ? $"{matched} words met" : "Close in meaning"));
        }

        // A verse inside a story already found is the same suggestion.
        var stories = results.Where(r => r.Why.StartsWith("Story")).ToList();
        results.RemoveAll(r => r.Why != "Quoted" && !r.Why.StartsWith("Story") && stories.Any(s => Contains(s.Reference, r.Reference)));
        // A quoted verse is the best answer: a story around it comes second.
        if (results.Any(r => r.Why == "Quoted"))
            results = results.Select(r => r.Why == "Quoted" ? r with { Score = Math.Max(r.Score, 0.99) } : r).ToList();
        return results.Where(r => r.Score >= minScore)
            .OrderByDescending(r => r.Score)
            .DistinctBy(r => r.Reference.ToString())
            .Take(limit)
            .ToList();
    }

    private static bool Contains(BibleReference outer, BibleReference inner)
    {
        if (outer.Book != inner.Book) return false;
        var set = outer.Verses().ToHashSet();
        return inner.Verses().All(set.Contains);
    }

    internal static float Dot(float[] a, float[] b)
    {
        var s = 0f;
        for (var i = 0; i < a.Length && i < b.Length; i++) s += a[i] * b[i];
        return s;
    }
}

/// <summary>
/// Finds verses quoted word for word: every run of four words of a verse (in the translations people quote most)
/// is kept as a small number, and the spoken words are looked up run by run. About 20 MB for three translations.
/// </summary>
public sealed class QuoteIndex
{
    private readonly uint[] _hashes;
    private readonly int[] _verse;
    private readonly long[] _ids;
    private readonly short[] _runs;

    private QuoteIndex(uint[] hashes, int[] verse, long[] ids, short[] runs)
    {
        _hashes = hashes;
        _verse = verse;
        _ids = ids;
        _runs = runs;
    }

    public const int Run = 4;

    public static QuoteIndex Build(IEnumerable<(long Id, string Text)> verses)
    {
        var idOf = new Dictionary<long, int>();
        var ids = new List<long>();
        var runs = new List<short>();
        var pairs = new List<(uint, int)>();
        foreach (var (id, text) in verses)
        {
            if (!idOf.TryGetValue(id, out var d))
            {
                d = ids.Count;
                idOf[id] = d;
                ids.Add(id);
                runs.Add(0);
            }
            var w = Words(text);
            var seen = new HashSet<uint>();
            for (var i = 0; i + Run <= w.Count; i++)
            {
                var h = Hash(w, i);
                if (seen.Add(h)) pairs.Add((h, d));
            }
            runs[d] = (short)Math.Max(runs[d], Math.Max(0, w.Count - Run + 1));
        }
        pairs.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new QuoteIndex(pairs.Select(p => p.Item1).ToArray(), pairs.Select(p => p.Item2).ToArray(), [.. ids], [.. runs]);
    }

    /// <summary>Verses whose four-word runs were heard: at least 3 runs, or most of a short verse. Share = runs heard / runs in the verse.</summary>
    public IReadOnlyList<(long Id, double Share)> Find(string spoken, int limit = 3)
    {
        var w = Words(spoken);
        var hits = new Dictionary<int, int>();
        var seen = new HashSet<uint>();
        for (var i = 0; i + Run <= w.Count; i++)
        {
            var h = Hash(w, i);
            if (!seen.Add(h)) continue;
            var at = Array.BinarySearch(_hashes, h);
            if (at < 0) continue;
            while (at > 0 && _hashes[at - 1] == h) at--;
            var verses = new HashSet<int>();
            for (; at < _hashes.Length && _hashes[at] == h; at++) verses.Add(_verse[at]);
            // A run found in many verses ("and it came to pass") says nothing.
            if (verses.Count > 12) continue;
            foreach (var d in verses) hits[d] = hits.GetValueOrDefault(d) + 1;
        }
        return hits
            .Select(kv => (Id: _ids[kv.Key], Hits: kv.Value, Share: _runs[kv.Key] == 0 ? 0 : Math.Min(1, kv.Value / (double)_runs[kv.Key])))
            .Where(x => x.Hits >= 3 || (x.Hits >= 2 && x.Share >= 0.5))
            .OrderByDescending(x => x.Hits).ThenByDescending(x => x.Share)
            .Take(limit)
            .Select(x => (x.Id, x.Share))
            .ToList();
    }

    private static List<string> Words(string text)
    {
        var list = new List<string>();
        foreach (var t in SpokenText.Tokens(text))
        {
            if (t == ":") continue;
            // "thee" and "you", "hath" and "has" are the same word to a listener.
            list.Add(t switch
            {
                "thee" or "thou" or "ye" => "you",
                "thy" or "thine" => "your",
                "hath" => "has",
                "doth" => "does",
                "shalt" => "shall",
                "unto" => "to",
                _ => StopWords.Stem(t),
            });
        }
        return list;
    }

    private static uint Hash(List<string> w, int at)
    {
        var h = 2166136261u;
        for (var i = at; i < at + Run; i++)
        {
            foreach (var ch in w[i]) h = (h ^ ch) * 16777619u;
            h = (h ^ ' ') * 16777619u;
        }
        return h;
    }
}

/// <summary>
/// The meaning vector of every verse, worked out once on this PC and kept in a file (about 12 MB, one byte per number).
/// </summary>
public sealed class VerseVectors
{
    private readonly long[] _ids;
    private readonly sbyte[] _data;
    private readonly Dictionary<long, int> _row;

    public VerseVectors(int dimensions, long[] ids, sbyte[] data)
    {
        Dimensions = dimensions;
        _ids = ids;
        _data = data;
        _row = new Dictionary<long, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++) _row[ids[i]] = i;
    }

    public int Dimensions { get; }
    public int Count => _ids.Length;

    /// <summary>The closest verses in meaning to <paramref name="q"/> (a unit vector).</summary>
    public IReadOnlyList<(long Id, double Similarity)> Nearest(float[] q, int k)
    {
        var best = new PriorityQueue<int, float>();
        for (var i = 0; i < _ids.Length; i++)
        {
            var s = Row(q, i);
            if (best.Count < k) best.Enqueue(i, s);
            else if (best.TryPeek(out _, out var low) && s > low)
            {
                best.Dequeue();
                best.Enqueue(i, s);
            }
        }
        var list = new List<(long, double)>();
        while (best.TryDequeue(out var i, out var s)) list.Add((_ids[i], s));
        list.Reverse();
        return list;
    }

    public double Similarity(float[] q, long id) => _row.TryGetValue(id, out var i) ? Row(q, i) : 0;

    private float Row(float[] q, int i)
    {
        var off = i * Dimensions;
        var s = 0f;
        for (var d = 0; d < Dimensions; d++) s += q[d] * _data[off + d];
        return s / 127f;
    }

    /// <summary>Works out every verse's vector (a few minutes, once), reporting progress from 0 to 1.</summary>
    public static VerseVectors Build(IEmbedder e, IReadOnlyList<(long Id, string Text)> verses, Action<double>? progress = null, CancellationToken ct = default)
    {
        var dim = e.Dimensions;
        var data = new sbyte[verses.Count * dim];
        var ids = new long[verses.Count];
        for (var i = 0; i < verses.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ids[i] = verses[i].Id;
            var v = e.Embed(verses[i].Text);
            for (var d = 0; d < dim; d++) data[i * dim + d] = (sbyte)Math.Clamp(MathF.Round(v[d] * 127f), -127, 127);
            if (i % 250 == 0) progress?.Invoke((double)i / verses.Count);
        }
        progress?.Invoke(1);
        return new VerseVectors(dim, ids, data);
    }

    public void Save(string path, string stamp)
    {
        var tmp = path + ".part";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            w.Write("NXVV1");
            w.Write(stamp);
            w.Write(Dimensions);
            w.Write(_ids.Length);
            foreach (var id in _ids) w.Write(id);
            w.Write(MemoryMarshalBytes(_data));
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static byte[] MemoryMarshalBytes(sbyte[] data)
    {
        var b = new byte[data.Length];
        Buffer.BlockCopy(data, 0, b, 0, data.Length);
        return b;
    }

    /// <summary>Loads a saved file, or null when it is missing, damaged or made for something else (<paramref name="stamp"/>).</summary>
    public static VerseVectors? Load(string path, string stamp)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadString() != "NXVV1" || r.ReadString() != stamp) return null;
            var dim = r.ReadInt32();
            var n = r.ReadInt32();
            if (dim <= 0 || dim > 4096 || n <= 0 || n > 100_000) return null;
            var ids = new long[n];
            for (var i = 0; i < n; i++) ids[i] = r.ReadInt64();
            var bytes = r.ReadBytes(n * dim);
            if (bytes.Length != n * dim) return null;
            var data = new sbyte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);
            return new VerseVectors(dim, ids, data);
        }
        catch (Exception) { return null; }
    }
}
