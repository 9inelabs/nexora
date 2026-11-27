using System.Numerics;

namespace Nexora.Audio.Dsp;

public enum FilterShape
{
    Peak,
    LowShelf,
    HighShelf,
    HighPass,
    LowPass,
    BandPass,
    Notch,
    HighPass1,
    LowPass1,
    AllPass,
}

/// <summary>
/// One second-order filter section (the standard "cookbook" biquad, published by Robert Bristow-Johnson).
/// Coefficients are already divided by a0.
/// </summary>
public readonly struct Biquad
{
    public readonly double B0, B1, B2, A1, A2;

    public Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        B0 = b0; B1 = b1; B2 = b2; A1 = a1; A2 = a2;
    }

    public static Biquad Identity => new(1, 0, 0, 0, 0);

    public bool IsIdentity => B0 == 1 && B1 == 0 && B2 == 0 && A1 == 0 && A2 == 0;

    public static Biquad Make(FilterShape shape, double freq, double q, double gainDb, double sampleRate)
    {
        var nyq = sampleRate * 0.5;
        freq = Math.Clamp(freq, 5, nyq * 0.98);
        q = Math.Clamp(q, 0.05, 40);
        var w0 = 2 * Math.PI * freq / sampleRate;
        var cos = Math.Cos(w0);
        var sin = Math.Sin(w0);
        var alpha = sin / (2 * q);
        var a = Math.Pow(10, gainDb / 40);
        double b0, b1, b2, a0, a1, a2;
        switch (shape)
        {
            case FilterShape.Peak:
                if (Math.Abs(gainDb) < 1e-6) return Identity;
                b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
                break;
            case FilterShape.LowShelf:
            {
                if (Math.Abs(gainDb) < 1e-6) return Identity;
                var s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + s);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - s);
                a0 = (a + 1) + (a - 1) * cos + s;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - s;
                break;
            }
            case FilterShape.HighShelf:
            {
                if (Math.Abs(gainDb) < 1e-6) return Identity;
                var s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cos + s);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - s);
                a0 = (a + 1) - (a - 1) * cos + s;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - s;
                break;
            }
            case FilterShape.LowPass:
                b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case FilterShape.HighPass:
                b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case FilterShape.BandPass:
                b0 = alpha; b1 = 0; b2 = -alpha;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case FilterShape.Notch:
                b0 = 1; b1 = -2 * cos; b2 = 1;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case FilterShape.AllPass:
                b0 = 1 - alpha; b1 = -2 * cos; b2 = 1 + alpha;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case FilterShape.LowPass1:
            {
                var k = Math.Tan(Math.PI * freq / sampleRate);
                var n = k / (1 + k);
                return new Biquad(n, n, 0, (k - 1) / (k + 1), 0);
            }
            case FilterShape.HighPass1:
            {
                var k = Math.Tan(Math.PI * freq / sampleRate);
                var n = 1 / (1 + k);
                return new Biquad(n, -n, 0, (k - 1) / (k + 1), 0);
            }
            default:
                return Identity;
        }
        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>The filter's gain at this frequency, in dB.</summary>
    public double MagnitudeDb(double freq, double sampleRate)
    {
        var w = 2 * Math.PI * freq / sampleRate;
        var z1 = Complex.FromPolarCoordinates(1, -w);
        var z2 = z1 * z1;
        var h = (B0 + B1 * z1 + B2 * z2) / (1 + A1 * z1 + A2 * z2);
        var m = h.Magnitude;
        return m <= 1e-12 ? -240 : 20 * Math.Log10(m);
    }
}

/// <summary>The memory of one biquad on one channel (transposed direct form II, in double precision).</summary>
public struct BiquadState
{
    private double _z1, _z2;

    public float Tick(in Biquad c, float input)
    {
        var x = (double)input;
        var y = c.B0 * x + _z1;
        _z1 = c.B1 * x - c.A1 * y + _z2;
        _z2 = c.B2 * x - c.A2 * y;
        return (float)y;
    }

    public double TickD(in Biquad c, double x)
    {
        var y = c.B0 * x + _z1;
        _z1 = c.B1 * x - c.A1 * y + _z2;
        _z2 = c.B2 * x - c.A2 * y;
        return y;
    }

    public void Process(in Biquad c, Span<float> buf)
    {
        if (c.IsIdentity) return;
        double z1 = _z1, z2 = _z2;
        double b0 = c.B0, b1 = c.B1, b2 = c.B2, a1 = c.A1, a2 = c.A2;
        for (var n = 0; n < buf.Length; n++)
        {
            double x = buf[n];
            var y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            buf[n] = (float)y;
        }
        // Flush tiny values so silence never becomes slow denormal maths.
        _z1 = Math.Abs(z1) < 1e-25 ? 0 : z1;
        _z2 = Math.Abs(z2) < 1e-25 ? 0 : z2;
    }

    public void Reset() => _z1 = _z2 = 0;
}

public static class Db
{
    public const double Floor = -90;

    public static float ToGain(double db) => db <= Floor ? 0f : (float)Math.Pow(10, db / 20);
    public static double FromGain(double g) => g <= 1e-9 ? -180 : 20 * Math.Log10(g);
}

/// <summary>Butterworth sections for steeper high-pass and low-pass filters.</summary>
public static class Butterworth
{
    /// <summary>The Q of each second-order section for a filter of this order (2, 4, 6 or 8).</summary>
    public static double[] Qs(int order) => order switch
    {
        <= 2 => [0.70710678],
        4 => [0.54119610, 1.30656296],
        6 => [0.51763809, 0.70710678, 1.93185165],
        _ => [0.50979558, 0.60134489, 0.89997622, 2.56291545],
    };
}
