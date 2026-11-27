using System.Buffers.Binary;
using System.Text;

namespace Nexora.Audio.Record;

/// <summary>How the recorded samples are stored.</summary>
public enum RecordFormat
{
    /// <summary>24-bit whole numbers: what most recording programs use. Half the size of 32-bit float.</summary>
    Pcm24,
    /// <summary>32-bit floating point: cannot clip in the file.</summary>
    Float32,
}

/// <summary>
/// Writes a WAV file as it goes. The header is brought up to date every few seconds, so a file cut short
/// by a power cut still opens in any program, up to the last few seconds.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly RecordFormat _format;
    private readonly int _channels;
    private readonly byte[] _scratch = new byte[65536 * 4];
    private long _frames;
    private DateTime _headerAt = DateTime.UtcNow;

    public WavWriter(string path, int sampleRate, int channels, RecordFormat format)
    {
        _format = format;
        _channels = channels;
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        SampleRate = sampleRate;
        WriteHeader();
    }

    public int SampleRate { get; }
    public long Frames => _frames;
    private int BytesPerSample => _format == RecordFormat.Pcm24 ? 3 : 4;

    /// <summary>Adds interleaved samples (frames × channels).</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        var bps = BytesPerSample;
        var maxPerChunk = _scratch.Length / bps;
        while (interleaved.Length > 0)
        {
            var n = Math.Min(interleaved.Length, maxPerChunk);
            var b = _scratch.AsSpan(0, n * bps);
            if (_format == RecordFormat.Float32)
                for (var i = 0; i < n; i++) BinaryPrimitives.WriteSingleLittleEndian(b[(i * 4)..], interleaved[i]);
            else
                for (var i = 0; i < n; i++)
                {
                    var v = Math.Clamp(interleaved[i], -1f, 1f);
                    var x = (int)MathF.Round(v * 8388607f);
                    b[i * 3] = (byte)x;
                    b[i * 3 + 1] = (byte)(x >> 8);
                    b[i * 3 + 2] = (byte)(x >> 16);
                }
            _fs.Write(b);
            _frames += n / _channels;
            interleaved = interleaved[n..];
        }
        if (DateTime.UtcNow - _headerAt > TimeSpan.FromSeconds(5)) Flush();
    }

    /// <summary>Puts the current length in the header and pushes everything to the disk.</summary>
    public void Flush()
    {
        _headerAt = DateTime.UtcNow;
        var pos = _fs.Position;
        _fs.Position = 0;
        WriteHeader();
        _fs.Position = pos;
        _fs.Flush(true);
    }

    private void WriteHeader()
    {
        var bps = BytesPerSample;
        var data = _frames * _channels * bps;
        var h = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)Math.Min(uint.MaxValue, 36 + data));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(h, 8);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), (short)(_format == RecordFormat.Float32 ? 3 : 1));
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), (short)_channels);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), SampleRate * _channels * bps);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), (short)(_channels * bps));
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), (short)(bps * 8));
        Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)Math.Min(uint.MaxValue, data));
        _fs.Write(h);
    }

    public void Dispose()
    {
        try { Flush(); }
        catch (Exception) { }
        _fs.Dispose();
    }
}

/// <summary>Reads WAV files: 16, 24 or 32-bit whole numbers, or 32-bit float, any number of channels.</summary>
public sealed class WavReader : IDisposable
{
    private readonly FileStream _fs;
    private readonly long _dataStart;
    private readonly int _bits;
    private readonly bool _float;
    private byte[] _scratch = new byte[1 << 16];

    public WavReader(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        Span<byte> head = stackalloc byte[12];
        if (_fs.Read(head) != 12 || !head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a WAV file.");
        long dataLen = -1;
        Span<byte> ch = stackalloc byte[8];
        var gotFmt = false;
        while (_fs.Read(ch) == 8)
        {
            var id = Encoding.ASCII.GetString(ch[..4]);
            var len = BinaryPrimitives.ReadUInt32LittleEndian(ch[4..]);
            if (id == "fmt ")
            {
                var fmt = new byte[len];
                _fs.ReadExactly(fmt);
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                Channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                SampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4));
                _bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (tag == 0xFFFE && len >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                _float = tag == 3;
                if (!(tag == 1 && _bits is 16 or 24 or 32) && !(_float && _bits == 32))
                    throw new InvalidDataException($"This WAV file is {_bits}-bit in a format NEX-Audio cannot play. Save it as 16 or 24-bit, or 32-bit float.");
                gotFmt = true;
                if ((len & 1) == 1) _fs.Position++;
            }
            else if (id == "data")
            {
                _dataStart = _fs.Position;
                // A file still being written (or cut short) may say 0 or more than is there.
                var left = _fs.Length - _dataStart;
                dataLen = len == 0 || len > left ? left : len;
                break;
            }
            else _fs.Position += len + (len & 1);
        }
        if (!gotFmt || dataLen < 0 || Channels <= 0) throw new InvalidDataException("This WAV file has no sound in it.");
        Frames = dataLen / (Channels * (_bits / 8));
        _fs.Position = _dataStart;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public long Frames { get; }
    public long Position { get; private set; }

    public void Seek(long frame)
    {
        Position = Math.Clamp(frame, 0, Frames);
        _fs.Position = _dataStart + Position * Channels * (_bits / 8);
    }

    /// <summary>Reads up to dst.Length / Channels frames, interleaved. Returns the number of frames read.</summary>
    public int Read(Span<float> dst)
    {
        var bps = _bits / 8;
        var frames = (int)Math.Min(dst.Length / Channels, Frames - Position);
        if (frames <= 0) return 0;
        var bytes = frames * Channels * bps;
        if (_scratch.Length < bytes) _scratch = new byte[bytes];
        var got = 0;
        while (got < bytes)
        {
            var n = _fs.Read(_scratch, got, bytes - got);
            if (n == 0) break;
            got += n;
        }
        var samples = got / bps;
        var b = _scratch.AsSpan();
        for (var i = 0; i < samples; i++)
        {
            dst[i] = _bits switch
            {
                16 => BinaryPrimitives.ReadInt16LittleEndian(b[(i * 2)..]) / 32768f,
                24 => ((b[i * 3] | (b[i * 3 + 1] << 8) | (b[i * 3 + 2] << 16)) << 8 >> 8) / 8388608f,
                _ when _float => BinaryPrimitives.ReadSingleLittleEndian(b[(i * 4)..]),
                _ => BinaryPrimitives.ReadInt32LittleEndian(b[(i * 4)..]) / 2147483648f,
            };
        }
        var read = samples / Channels;
        Position += read;
        return read;
    }

    public void Dispose() => _fs.Dispose();
}
