using System.IO;
using System.Media;

namespace FlipDrop.App;

/// <summary>
/// A soft synthesized "whoosh": low-passed noise with a swelling-then-fading
/// envelope. Rendered once to an in-memory 16-bit PCM WAV and replayed. No files,
/// no packages.
/// </summary>
internal static class Whoosh
{
    private static readonly byte[] WavBytes = Render();

    public static void Play()
    {
        try
        {
            var player = new SoundPlayer(new MemoryStream(WavBytes, writable: false));
            player.Play(); // async on a worker thread
        }
        catch { /* audio is optional */ }
    }

    private static byte[] Render()
    {
        const int sampleRate = 44100;
        const double seconds = 0.55;
        int n = (int)(sampleRate * seconds);
        var samples = new short[n];

        var rng = new Random(1234);
        double lp = 0.0; // one-pole low-pass state
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / n;                       // 0..1
            double env = Math.Sin(Math.PI * t);             // swell up then down
            env *= env;                                      // softer edges
            // Low-pass cutoff opens in the first half, closes in the second → airy sweep.
            double cutoff = 0.04 + 0.22 * Math.Sin(Math.PI * t);
            double noise = rng.NextDouble() * 2.0 - 1.0;
            lp += cutoff * (noise - lp);
            double v = lp * env * 0.38;
            samples[i] = (short)Math.Clamp(v * short.MaxValue, short.MinValue, short.MaxValue);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);                       // fmt chunk size
        w.Write((short)1);                 // PCM
        w.Write((short)1);                 // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);           // byte rate
        w.Write((short)2);                 // block align
        w.Write((short)16);                // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
