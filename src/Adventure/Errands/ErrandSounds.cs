using Godot;
using UnitSport.Audio;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// The errands' synthesised sounds (#1267), each built once (static: the sound player lists them by itself): a giver's greeting
/// chirp when the dialogue opens, and the stamp's thud with a bright tick when a card is taken.
/// </summary>
public static class ErrandSounds
{
    private static AudioStreamWav? _greeting, _stamp;
    private static AudioStreamPlayer? _player;

    /// <summary>A short two-note chirp, rising: "Grüezi".</summary>
    public static AudioStreamWav Greeting => _greeting ??= Dsp.OneShot(0.3f, 12671, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float f = t < 0.12f ? 520f : 690f;
            float env = Mathf.Min(1, t * 120f) * Mathf.Exp(-(t < 0.12f ? t : t - 0.12f) * 14f);
            s[i] = (Mathf.Sin(Mathf.Tau * f * t) + 0.3f * Mathf.Sin(Mathf.Tau * f * 2f * t)) * env * 0.5f;
        }
        return s;
    });

    /// <summary>A rubber stamp on paper: a low thud and a dry tick.</summary>
    public static AudioStreamWav Stamp => _stamp ??= Dsp.OneShot(0.35f, 12672, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float thud = Mathf.Sin(Mathf.Tau * (110f - 40f * t) * t) * Mathf.Exp(-t * 22f) * 0.9f;
            float tick = t < 0.02f ? (float)(rng.NextDouble() * 2 - 1) * (1 - t / 0.02f) * 0.5f : 0f;
            s[i] = thud + tick;
        }
        return s;
    });

    /// <summary>Plays a sound on the UI bus-free master, once (no node per sound).</summary>
    public static void Play(Node host, AudioStream stream)
    {
        if (Net.NetworkManager.DedicatedServer) return;
        if (_player == null || !GodotObject.IsInstanceValid(_player))
        {
            _player = new AudioStreamPlayer { Name = "ErrandSfx", Bus = "Master" };
            host.AddChild(_player);
        }
        _player.Stream = stream;
        _player.Play();
    }
}
