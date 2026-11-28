namespace Nexora.Audio.Drums;

/// <summary>
/// NEX-Audio's own starter drum kit, made from scratch by synthesis (tuned sine bodies with pitch drops, filtered noise
/// for the snare wires and the beater click). Four hit strengths per drum, two takes each, so every part of the
/// trigger can be tried before choosing your own samples. Nothing is copied from any sample pack.
/// </summary>
public static class StarterKit
{
    public static readonly string[] Strengths = ["soft", "medium", "hard", "full"];

    /// <summary>The kit's file names in the library ("Snare hard 2") with their sound.</summary>
    public static IEnumerable<(string Name, float[] Data)> Make(int rate)
    {
        foreach (var drum in new[] { "Kick", "Snare", "Rack tom", "Floor tom" })
            for (var layer = 0; layer < Strengths.Length; layer++)
                for (var take = 1; take <= 2; take++)
                    yield return ($"{drum} {Strengths[layer]} {take}", Drum(drum, layer, take, rate));
    }

    /// <summary>The library paths of one drum's layers, softest first, two takes each (for setting up a trigger).</summary>
    public static List<List<string>> Layers(string drum) =>
        Strengths.Select(st => Enumerable.Range(1, 2).Select(t => $"{DrumLibrary.StarterKit}/{drum} {st} {t}.wav").ToList()).ToList();

    public static float[] Drum(string drum, int layer, int take, int rate)
    {
        // 0 soft to 1 full: harder hits are brighter, with more attack.
        var hard = layer / 3.0;
        var rnd = new Random(drum.Aggregate(17, (h, c) => h * 31 + c) ^ (layer * 31 + take * 7919));
        double Vary(double amount) => 1 + (rnd.NextDouble() * 2 - 1) * amount;
        var (len, data) = drum switch
        {
            "Kick" => Kick(rate, hard, Vary(0.02), rnd),
            "Snare" => Snare(rate, hard, Vary(0.02), rnd),
            "Rack tom" => Tom(rate, hard, 170 * Vary(0.015), 125, rnd),
            _ => Tom(rate, hard, 105 * Vary(0.015), 78, rnd),
        };
        // Softer layers are recorded quieter, as a real drum would be.
        var peak = data.Max(MathF.Abs);
        var target = (float)Math.Pow(10, (-7 + hard * 6) / 20);
        if (peak > 0) for (var i = 0; i < len; i++) data[i] *= target / peak;
        // A short fade at the very end so nothing clicks.
        var fade = Math.Min(len, rate / 100);
        for (var i = 0; i < fade; i++) data[len - 1 - i] *= i / (float)fade;
        return data;
    }

    private static (int, float[]) Kick(int rate, double hard, double tune, Random rnd)
    {
        var len = (int)(0.55 * rate);
        var d = new float[len];
        double ph = 0;
        var lp = 0.0;
        for (var i = 0; i < len; i++)
        {
            var t = i / (double)rate;
            var f = (52 + (90 + 70 * hard) * Math.Exp(-t / 0.035)) * tune;
            ph += 2 * Math.PI * f / rate;
            var body = Math.Sin(ph) * Math.Exp(-t / (0.16 + 0.06 * hard));
            // The beater: a short burst of filtered noise, louder on hard hits.
            var n = rnd.NextDouble() * 2 - 1;
            lp += (n - lp) * (0.25 + 0.3 * hard);
            var click = lp * Math.Exp(-t / 0.004) * (0.15 + 0.45 * hard);
            d[i] = (float)Math.Tanh((body + click) * (1.2 + hard));
        }
        return (len, d);
    }

    private static (int, float[]) Snare(int rate, double hard, double tune, Random rnd)
    {
        var len = (int)(0.42 * rate);
        var d = new float[len];
        double p1 = 0, p2 = 0, hp = 0, prev = 0, lp = 0;
        for (var i = 0; i < len; i++)
        {
            var t = i / (double)rate;
            p1 += 2 * Math.PI * 185 * tune * (1 + 0.25 * Math.Exp(-t / 0.01)) / rate;
            p2 += 2 * Math.PI * 330 * tune / rate;
            var body = (Math.Sin(p1) * 0.8 + Math.Sin(p2) * 0.35) * Math.Exp(-t / 0.07);
            // The wires: noise, high-passed, brighter and longer on hard hits.
            var n = rnd.NextDouble() * 2 - 1;
            hp = 0.92 * (hp + n - prev);
            prev = n;
            lp += (hp - lp) * (0.35 + 0.45 * hard);
            var wires = lp * Math.Exp(-t / (0.09 + 0.07 * hard)) * (0.55 + 0.6 * hard);
            var crack = n * Math.Exp(-t / 0.003) * 0.6 * hard;
            d[i] = (float)Math.Tanh((body * (0.9 - 0.2 * hard) + wires + crack) * 1.4);
        }
        return (len, d);
    }

    private static (int, float[]) Tom(int rate, double hard, double start, double end, Random rnd)
    {
        var len = (int)(0.9 * rate);
        var d = new float[len];
        double ph = 0, ph2 = 0, lp = 0;
        for (var i = 0; i < len; i++)
        {
            var t = i / (double)rate;
            var f = end + (start - end) * Math.Exp(-t / 0.08) + 20 * hard * Math.Exp(-t / 0.01);
            ph += 2 * Math.PI * f / rate;
            ph2 += 2 * Math.PI * f * 1.58 / rate;
            var body = (Math.Sin(ph) + 0.25 * Math.Sin(ph2) * Math.Exp(-t / 0.05)) * Math.Exp(-t / (0.25 + 0.1 * hard));
            var n = rnd.NextDouble() * 2 - 1;
            lp += (n - lp) * (0.2 + 0.4 * hard);
            var stick = lp * Math.Exp(-t / 0.006) * (0.1 + 0.4 * hard);
            d[i] = (float)Math.Tanh((body + stick) * (1.1 + 0.6 * hard));
        }
        return (len, d);
    }
}
