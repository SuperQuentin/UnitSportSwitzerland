using Godot;
using UnitSport.Audio;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// The world events' synthesised sounds (#1339), each built once (static: the sound player lists them by itself): the avalanche
/// transceiver's beep, a fire's crackle, a loose cow's bell and the lost hiker's call.
/// </summary>
public static class WorldEventSounds
{
    private static AudioStreamWav? _beep, _crackle, _bell, _call;
    private static AudioStreamPlayer? _player;

    /// <summary>The transceiver's beep: a short, clean tone at 457 Hz-ish (up an octave and a bit so small speakers carry it).</summary>
    public static AudioStreamWav Beep => _beep ??= Dsp.OneShot(0.14f, 13671, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = Mathf.Min(1, t * 400f) * Mathf.Min(1, (0.14f - t) * 400f);
            s[i] = Mathf.Sin(Mathf.Tau * 1830f * t) * env * 0.45f;
        }
        return s;
    });

    /// <summary>A fire's crackle: a few dry pops and a soft hiss.</summary>
    public static AudioStreamWav Crackle => _crackle ??= Dsp.OneShot(0.7f, 13672, (rng, n) =>
    {
        var s = new float[n];
        var pops = new[] { 0.02f, 0.11f, 0.19f, 0.33f, 0.46f };
        float hiss = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float v = (float)(rng.NextDouble() * 2 - 1);
            hiss = hiss * 0.85f + v * 0.15f;
            float x = hiss * 0.12f * Mathf.Exp(-t * 2.2f);
            foreach (float p in pops)
                if (t >= p && t < p + 0.025f) x += v * (1 - (t - p) / 0.025f) * 0.55f;
            s[i] = x;
        }
        return s;
    });

    /// <summary>A cowbell: two inharmonic partials, a dull clank.</summary>
    public static AudioStreamWav Bell => _bell ??= Dsp.OneShot(0.6f, 13673, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = Mathf.Min(1, t * 600f) * Mathf.Exp(-t * 9f);
            s[i] = (Mathf.Sin(Mathf.Tau * 540f * t) + 0.7f * Mathf.Sin(Mathf.Tau * 812f * t) + 0.3f * Mathf.Sin(Mathf.Tau * 1370f * t)) * env * 0.3f;
        }
        return s;
    });

    /// <summary>"Hallo!" from a distance: a rising call and a falling answer, voiced by a few stacked partials.</summary>
    public static AudioStreamWav Call => _call ??= Dsp.OneShot(0.7f, 13674, (rng, n) =>
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float f = t < 0.28f ? 300f + 260f * (t / 0.28f) : t < 0.34f ? 0f : 520f - 190f * ((t - 0.34f) / 0.36f);
            float env = t < 0.28f ? Mathf.Min(1, t * 30f) * Mathf.Min(1, (0.28f - t) * 25f) : t < 0.34f ? 0f : Mathf.Min(1, (t - 0.34f) * 30f) * Mathf.Min(1, (0.7f - t) * 12f);
            s[i] = (Mathf.Sin(Mathf.Tau * f * t) + 0.5f * Mathf.Sin(Mathf.Tau * f * 2f * t) + 0.25f * Mathf.Sin(Mathf.Tau * f * 3f * t)) * env * 0.3f;
        }
        return s;
    });

    /// <summary>Plays a sound at a volume (dB) on the master bus, once; no node per sound.</summary>
    public static void Play(Node host, AudioStream stream, float volumeDb = -6f)
    {
        if (Net.NetworkManager.DedicatedServer) return;
        if (_player == null || !GodotObject.IsInstanceValid(_player))
        {
            _player = new AudioStreamPlayer { Name = "WorldEventSfx", Bus = "Master" };
            host.AddChild(_player);
        }
        _player.Stream = stream;
        _player.VolumeDb = volumeDb;
        _player.Play();
    }
}
