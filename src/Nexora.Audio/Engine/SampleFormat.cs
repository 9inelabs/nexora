using System.Runtime.CompilerServices;

namespace Nexora.Audio.Engine;

/// <summary>How one channel's samples are stored in a driver buffer (the ASIO sample types NEX-Audio supports).</summary>
public enum SampleFormat
{
    /// <summary>16-bit whole numbers.</summary>
    Int16 = 16,
    /// <summary>24-bit whole numbers packed in 3 bytes.</summary>
    Int24 = 17,
    /// <summary>32-bit whole numbers (what the X32's X-USB card normally uses).</summary>
    Int32 = 18,
    Float32 = 19,
    Float64 = 20,
    /// <summary>32-bit containers holding 24-bit values in the low bits.</summary>
    Int32In24 = 27,
}

/// <summary>Converts between driver buffers and NEX-Audio's float samples. Never allocates.</summary>
public static unsafe class SampleConvert
{
    public static bool IsSupported(int asioType) => asioType is 16 or 17 or 18 or 19 or 20 or 27;

    public static int BytesPerSample(SampleFormat f) => f switch
    {
        SampleFormat.Int16 => 2,
        SampleFormat.Int24 => 3,
        SampleFormat.Float64 => 8,
        _ => 4,
    };

    public static void ToFloat(SampleFormat f, void* src, float[] dst, int frames)
    {
        switch (f)
        {
            case SampleFormat.Int16:
            {
                var s = (short*)src;
                for (var i = 0; i < frames; i++) dst[i] = s[i] * (1f / 32768f);
                break;
            }
            case SampleFormat.Int24:
            {
                var b = (byte*)src;
                for (var i = 0; i < frames; i++, b += 3)
                {
                    var v = (b[0] << 8) | (b[1] << 16) | (b[2] << 24);
                    dst[i] = v * (1f / 2147483648f);
                }
                break;
            }
            case SampleFormat.Int32:
            {
                var s = (int*)src;
                for (var i = 0; i < frames; i++) dst[i] = s[i] * (1f / 2147483648f);
                break;
            }
            case SampleFormat.Int32In24:
            {
                var s = (int*)src;
                for (var i = 0; i < frames; i++) dst[i] = (s[i] << 8) * (1f / 2147483648f);
                break;
            }
            case SampleFormat.Float32:
                new ReadOnlySpan<float>(src, frames).CopyTo(dst);
                break;
            case SampleFormat.Float64:
            {
                var s = (double*)src;
                for (var i = 0; i < frames; i++) dst[i] = (float)s[i];
                break;
            }
        }
    }

    public static void FromFloat(SampleFormat f, float[] src, void* dst, int frames)
    {
        switch (f)
        {
            case SampleFormat.Int16:
            {
                var d = (short*)dst;
                for (var i = 0; i < frames; i++) d[i] = (short)(Clip(src[i]) * 32767f);
                break;
            }
            case SampleFormat.Int24:
            {
                var b = (byte*)dst;
                for (var i = 0; i < frames; i++, b += 3)
                {
                    var v = (int)(Clip(src[i]) * 8388607f);
                    b[0] = (byte)v;
                    b[1] = (byte)(v >> 8);
                    b[2] = (byte)(v >> 16);
                }
                break;
            }
            case SampleFormat.Int32:
            {
                var d = (int*)dst;
                for (var i = 0; i < frames; i++) d[i] = ToInt32(src[i]);
                break;
            }
            case SampleFormat.Int32In24:
            {
                var d = (int*)dst;
                for (var i = 0; i < frames; i++) d[i] = (int)(Clip(src[i]) * 8388607f);
                break;
            }
            case SampleFormat.Float32:
            {
                var d = (float*)dst;
                for (var i = 0; i < frames; i++) d[i] = Clip(src[i]);
                break;
            }
            case SampleFormat.Float64:
            {
                var d = (double*)dst;
                for (var i = 0; i < frames; i++) d[i] = Clip(src[i]);
                break;
            }
        }
    }

    /// <summary>Silence in any format is all zero bytes.</summary>
    public static void Silence(SampleFormat f, void* dst, int frames) =>
        new Span<byte>(dst, frames * BytesPerSample(f)).Clear();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clip(float v) => v > 1f ? 1f : v < -1f ? -1f : float.IsNaN(v) ? 0f : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ToInt32(float v)
    {
        // Full scale: 2147483647 cannot be held exactly by a float, so stay in double for the top.
        var d = (double)Clip(v) * 2147483647.0;
        return (int)d;
    }
}
