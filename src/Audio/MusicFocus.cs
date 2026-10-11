using Godot;
using UnitSport.Items;

namespace UnitSport.Audio;

/// <summary>A loudspeaker that plays music for a long time (a radio, a car stereo), ducked by <see cref="MusicFocus"/>.</summary>
public interface IMusicSpeaker
{
    /// <summary>Its level this frame before any duck, dB: base, its own volume, what is in the way.</summary>
    float SourceDb { get; }

    /// <summary>The ears are part of it: a radio one carries, the stereo of the car one sits in. It always wins.</summary>
    bool Close { get; }
}

/// <summary>
/// Picks, once a frame, the music speaker this machine's ears hear loudest (#813,
/// <see cref="RadioFocus"/>); every other one ducks under it. Speakers add themselves while in the
/// tree and ask <see cref="Full"/> each frame. Per frame: one pass over the speakers that are
/// playing, a distance each, no allocation.
/// </summary>
public static class MusicFocus
{
    private static readonly List<AudioStreamPlayer3D> Speakers = new();
    private static float[] _levels = new float[8];
    private static AudioStreamPlayer3D? _focus;
    private static ulong _frame = ulong.MaxValue;

    /// <summary>The speaker the ears follow now, or null when none is heard. For the probes.</summary>
    public static AudioStreamPlayer3D? Focus { get { Update(); return _focus; } }

    public static void Add(AudioStreamPlayer3D speaker)
    {
        if (!Speakers.Contains(speaker)) Speakers.Add(speaker);
    }

    public static void Remove(AudioStreamPlayer3D speaker)
    {
        Speakers.Remove(speaker);
        if (_focus == speaker) _focus = null;
    }

    /// <summary>Whether <paramref name="speaker"/> plays at its own level this frame (it is the focus, or nothing is).</summary>
    public static bool Full(AudioStreamPlayer3D speaker)
    {
        Update();
        return _focus == null || _focus == speaker;
    }

    private static void Update()
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame == _frame) return;
        _frame = frame;
        if (_levels.Length < Speakers.Count) _levels = new float[Speakers.Count * 2];
        int current = -1;
        for (int i = 0; i < Speakers.Count; i++)
        {
            var s = Speakers[i];
            if (s == _focus) current = i;
            _levels[i] = Level(s);
        }
        int pick = RadioFocus.Pick(_levels.AsSpan(0, Speakers.Count), current);
        _focus = pick < 0 ? null : Speakers[pick];
    }

    /// <summary>How loud <paramref name="s"/> is at the ears, dB; minus infinity when silent or out of earshot.</summary>
    private static float Level(AudioStreamPlayer3D s)
    {
        if (!GodotObject.IsInstanceValid(s) || !s.IsInsideTree() || !s.Playing || s is not IMusicSpeaker music)
            return float.NegativeInfinity;
        if (music.Close) return RadioFocus.CloseDb;
        if (Ears.Of(s) is not { } ears) return float.NegativeInfinity;
        return RadioFocus.LevelAt(music.SourceDb, s.GlobalPosition.DistanceTo(ears), s.UnitSize, s.MaxDistance);
    }
}
