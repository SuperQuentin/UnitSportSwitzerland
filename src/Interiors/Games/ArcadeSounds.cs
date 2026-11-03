using Godot;
using UnitSport.Audio;

namespace UnitSport.Interiors.Games;

/// <summary>
/// The arcade's synthesised sounds (#1326), each built once (static: the sound player lists them by itself): coins
/// clattering out of the change machine, one dropping into a slot, a strip of tickets printing.
/// </summary>
public static class ArcadeSounds
{
    private static AudioStreamWav? _coins, _coin, _tickets;

    /// <summary>A handful of coins into the tray: bright metal pings, one after another.</summary>
    public static AudioStreamWav Coins => _coins ??= OneShot(Dsp.OneShot(0.9f, 14281, (rng, n) =>
    {
        var s = new float[n];
        float at = 0;
        for (int k = 0; k < 5; k++)
        {
            Ping(s, at, 2600f + (float)rng.NextDouble() * 900f, 0.5f);
            at += 0.06f + (float)rng.NextDouble() * 0.08f;
        }
        return s;
    }));

    /// <summary>One coin into a slot: a ping and its roll down the chute.</summary>
    public static AudioStreamWav Coin => _coin ??= OneShot(Dsp.OneShot(0.5f, 14282, (rng, n) =>
    {
        var s = new float[n];
        Ping(s, 0, 3100f, 0.5f);
        var roll = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(1500f), Dsp.Coef(4000f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            if (t > 0.05f) s[i] += roll[i] * 0.3f * Mathf.Exp(-(t - 0.05f) * 9f);
        }
        return s;
    }));

    /// <summary>Tickets printing out of a machine: a quick, rattling feed.</summary>
    public static AudioStreamWav Tickets => _tickets ??= OneShot(Dsp.OneShot(1.0f, 14283, (rng, n) =>
    {
        var s = new float[n];
        var noise = Dsp.BandPass(Dsp.Noise(rng, n), Dsp.Coef(600f), Dsp.Coef(3000f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float teeth = 0.5f + 0.5f * Mathf.Sin(Mathf.Tau * 38f * t);
            s[i] = noise[i] * teeth * 0.6f * Mathf.Min(1, (1 - t) * 6);
        }
        return s;
    }));

    /// <summary>Plays a sound flat (the player's own hands), off the games' node.</summary>
    public static void Play(AudioStream stream)
    {
        if (TableGames.Instance is not { } games) return;
        var player = new AudioStreamPlayer { Stream = stream, VolumeDb = -6, Bus = SfxBus.Name };
        games.AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
    }

    private static void Ping(float[] s, float at, float f, float amp)
    {
        int from = (int)(at * Dsp.Rate);
        for (int i = from; i < s.Length; i++)
        {
            float t = (i - from) / (float)Dsp.Rate;
            float env = Mathf.Exp(-t * 22f) * Mathf.Min(1, t * 5000);
            if (t > 0.01f && env < 1e-4f) break;
            s[i] += (Mathf.Sin(Mathf.Tau * f * t) + 0.5f * Mathf.Sin(Mathf.Tau * f * 2.76f * t)) * env * amp;
        }
    }

    private static AudioStreamWav OneShot(AudioStreamWav w)
    {
        w.LoopMode = AudioStreamWav.LoopModeEnum.Disabled;
        return w;
    }
}
