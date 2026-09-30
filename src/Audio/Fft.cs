using System;
using System.Collections.Concurrent;

namespace UnitSport.Audio;

/// <summary>Small in-place radix-2 complex FFT. Pure C#, thread-safe (twiddle tables are cached per size).</summary>
public static class Fft
{
    private static readonly ConcurrentDictionary<int, (float[] cos, float[] sin)> Tables = new();

    /// <summary>
    /// Forward transform in place: X[k] = sum x[n] e^(-2 pi i k n / N). Both arrays must have the same
    /// power-of-two length.
    /// </summary>
    public static void Forward(float[] re, float[] im)
    {
        int n = re.Length;
        if (n != im.Length || n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT length must be a power of two and re/im must match.");
        var (cos, sin) = Tables.GetOrAdd(n, Build);

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1, step = n / len;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0, t = 0; k < half; k++, t += step)
                {
                    float wr = cos[t], wi = sin[t];
                    int a = i + k, b = a + half;
                    float xr = re[b] * wr - im[b] * wi;
                    float xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                }
            }
        }
    }

    private static (float[], float[]) Build(int n)
    {
        var c = new float[n / 2];
        var s = new float[n / 2];
        for (int k = 0; k < n / 2; k++)
        {
            double a = -2.0 * Math.PI * k / n;
            c[k] = (float)Math.Cos(a);
            s[k] = (float)Math.Sin(a);
        }
        return (c, s);
    }
}
