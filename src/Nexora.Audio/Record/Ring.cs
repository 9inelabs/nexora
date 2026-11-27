namespace Nexora.Audio.Record;

/// <summary>
/// A ring of samples between exactly one writer and one reader thread, with no locks, so the sound
/// thread can hand samples to the disk thread (or take them from it) without ever waiting.
/// </summary>
public sealed class SampleRing
{
    private readonly float[] _buf;
    private readonly int _mask;
    private long _write, _read;

    public SampleRing(int minCapacity)
    {
        var n = 1;
        while (n < minCapacity) n <<= 1;
        _buf = new float[n];
        _mask = n - 1;
    }

    public int Capacity => _buf.Length;
    public int Count => (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read));
    public int Free => Capacity - Count;

    /// <summary>Writer: adds the samples if they all fit. Returns false (and adds nothing) if they do not.</summary>
    public bool Write(float[] src, int offset, int count)
    {
        var w = Volatile.Read(ref _write);
        var r = Volatile.Read(ref _read);
        if (_buf.Length - (int)(w - r) < count) return false;
        var start = (int)(w & _mask);
        var first = Math.Min(count, _buf.Length - start);
        Array.Copy(src, offset, _buf, start, first);
        if (first < count) Array.Copy(src, offset + first, _buf, 0, count - first);
        Volatile.Write(ref _write, w + count);
        return true;
    }

    /// <summary>Reader: takes up to count samples. Returns how many it took.</summary>
    public int Read(float[] dst, int offset, int count)
    {
        var w = Volatile.Read(ref _write);
        var r = Volatile.Read(ref _read);
        var n = Math.Min(count, (int)(w - r));
        if (n <= 0) return 0;
        var start = (int)(r & _mask);
        var first = Math.Min(n, _buf.Length - start);
        Array.Copy(_buf, start, dst, offset, first);
        if (first < n) Array.Copy(_buf, 0, dst, offset + first, n - first);
        Volatile.Write(ref _read, r + n);
        return n;
    }
}
