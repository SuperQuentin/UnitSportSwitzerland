using System.Collections.Generic;
using Godot;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// Something that moves with the music its own way (#734): a car's body on its springs, say. Told
/// every frame while music reaches it (<paramref name="reach"/> 0..1) and once with 0 when it no
/// longer does, so it can settle. Purely what is drawn: never a physics body.
/// </summary>
public interface IBeatReactive
{
    void OnBeat(float reach, in RadioGroove groove);
}

/// <summary>
/// Where music plays and how hard it hits (#734): every playing radio near enough to matter (a
/// world radio, one carried in the hand or on the back, a car stereo's CD) with its position, its
/// own volume's reach (<see cref="RadioLoudness"/>) and its groove from the CD's analysis on the
/// shared clock (<see cref="RadioGroove"/>), so every peer moves the same things on the same beat
/// with nothing on the wire. Anything asks <see cref="At"/> how strongly the music reaches a point.
///
/// <para>
/// It also drives the reactions that are not their own code: the registered visuals
/// (<see cref="Add(Node3D, float, float)"/>: a placed thing's mesh, a build piece's) squash and hop on
/// the beat, <see cref="IBeatReactive"/>s are told, and the music nearest the camera goes to the
/// shaders as global uniforms (<c>world_music</c>, <c>world_music_beat</c>: trees sway, glowing props
/// pulse). Rendering only, never physics: a visual child's transform, a shader, never a body.
/// </para>
///
/// <para>
/// Cost: sources are gathered at <see cref="GatherHz"/> (the strings a carried radio replicates
/// are decoded then), positions and grooves every frame; the registry is walked once a frame with
/// fixed arrays, nothing allocated; with no music playing it only puts back what it moved.
/// </para>
/// </summary>
public static class BeatField
{
    public const int MaxSources = 8;
    private const float GatherHz = 8f;
    /// <summary>Registered things farther than this from the camera are left at rest (nobody reads them), m.</summary>
    private const float AnimateRange = 70f;

    private struct Source
    {
        public Node3D Node;
        public int Cd;
        public double StartedAt;
        public float Volume;
        public Vector3 At;
        public RadioGroove Groove;
    }

    private static readonly Source[] _sources = new Source[MaxSources];
    private static int _count;
    private static double _gatherAt;

    /// <summary>Playing sources this frame.</summary>
    public static int Count => _count;

    /// <summary>
    /// How strongly music reaches <paramref name="at"/> (global), 0..1, and the groove of the source
    /// that reaches it most; 0 and <see cref="RadioGroove.Silent"/> when none does.
    /// </summary>
    public static float At(Vector3 at, out RadioGroove groove)
    {
        groove = RadioGroove.Silent;
        float best = 0f;
        for (int i = 0; i < _count; i++)
        {
            ref readonly var s = ref _sources[i];
            if (!s.Groove.Beating) continue;
            float reach = RadioLoudness.Reach(s.At.DistanceTo(at), s.Volume);
            if (reach > best) { best = reach; groove = s.Groove; }
        }
        return best;
    }

    // ---- once a frame, from RadioManager on a client -------------------------------------------

    private static readonly StringName GMusic = "world_music", GMusicBeat = "world_music_beat";
    private static Vector4 _sentMusic = new(float.NaN, 0, 0, 0), _sentBeat = new(float.NaN, 0, 0, 0);

    /// <summary>What the shaders were last given as <c>world_music</c> (xyz, reach). For the probes.</summary>
    public static Vector4 ShaderMusic => _sentMusic;

    internal static void Step(RadioManager manager, Vector3? camera, double now)
    {
        if (now >= _gatherAt)
        {
            _gatherAt = now + 1.0 / GatherHz;
            Gather(manager);
        }
        // positions and grooves every frame: the beat is sharp, the radios move
        for (int i = 0; i < _count; i++)
        {
            ref var s = ref _sources[i];
            if (!GodotObject.IsInstanceValid(s.Node) || !s.Node.IsInsideTree()) { s.Groove = RadioGroove.Silent; continue; }
            s.At = s.Node.GlobalPosition;
            s.Groove = RadioGroove.Of(s.Cd, s.StartedAt, now);
        }
        StepRegistry(camera);
        SendShaders(camera);
    }

    /// <summary>The playing radios worth listening for: the world's, the carried ones, the car stereos' CDs.</summary>
    private static void Gather(RadioManager manager)
    {
        _count = 0;
        for (int i = 0, n = manager.GetChildCount(); i < n && _count < MaxSources; i++)
            if (manager.GetChild(i) is RadioBody { Playing: true } r && r.Speaker is { Playing: true })
                AddSource(r, r.CdId, r.StartedAt, r.Volume);
        if (manager.Players == null) return;
        double now = ClockSync.ServerNow;
        foreach (var p in manager.Players())
        {
            if (_count >= MaxSources) break;
            if (!GodotObject.IsInstanceValid(p) || !p.IsInsideTree()) continue;
            if (RadioPlay.Decode(p.HeldRadio) is { } held && held.Sounding(now)) AddSource(p, held.CdId, held.StartedAt, p.RadioVolume);
            else if (p.PlayingCarCd is { } car && car.Sounding(now)) AddSource(p, car.CdId, car.StartedAt, p.RadioVolume);
        }
    }

    private static void AddSource(Node3D node, int cd, double startedAt, float volume)
    {
        _sources[_count++] = new Source { Node = node, Cd = cd, StartedAt = startedAt, Volume = volume, At = node.GlobalPosition };
    }

    /// <summary>The source the camera hears most (else the nearest playing one) for the shaders: trees, glowing props.</summary>
    private static void SendShaders(Vector3? camera)
    {
        var music = Vector4.Zero;
        var beat = Vector4.Zero;
        if (camera is { } cam && _count > 0)
        {
            int best = -1;
            float bestScore = float.MaxValue;
            for (int i = 0; i < _count; i++)
            {
                ref readonly var s = ref _sources[i];
                if (!s.Groove.Beating) continue;
                float score = s.At.DistanceTo(cam) / RadioLoudness.Radius(s.Volume);
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0 && bestScore < 2.5f)
            {
                ref readonly var s = ref _sources[best];
                var g = s.Groove;
                music = new Vector4(s.At.X, s.At.Y, s.At.Z, RadioLoudness.Radius(s.Volume));
                // the sway's clock: one swing over two beats
                float swing = ((g.Beat & 1) + g.Phase) * 0.5f;
                beat = new Vector4(g.Kick, g.Level, g.BarKick, swing);
            }
        }
        if (!music.IsEqualApprox(_sentMusic)) { _sentMusic = music; RenderingServer.GlobalShaderParameterSet(GMusic, music); }
        if (!beat.IsEqualApprox(_sentBeat)) { _sentBeat = beat; RenderingServer.GlobalShaderParameterSet(GMusicBeat, beat); }
    }

    // ---- the registry ------------------------------------------------------------------------

    private struct Entry
    {
        public Node3D Node;
        public IBeatReactive? Custom;
        public Transform3D Rest;
        public float Amount, Half;
        public bool Moved;
    }

    private static Entry[] _entries = new Entry[256];
    private static int _entryCount;

    /// <summary>
    /// Makes <paramref name="visual"/> (a mesh, never a physics body) squash and hop on the beat
    /// whenever music reaches it, from its transform now; <paramref name="amount"/> scales the
    /// move (1 a light thing, 0.2 a wall), <paramref name="half"/> is half its height (it squashes
    /// about its bottom). Forgets it by itself once the node is freed.
    /// </summary>
    public static void Add(Node3D visual, float amount = 1f, float half = 0.15f) =>
        Push(new Entry { Node = visual, Rest = visual.Transform, Amount = amount, Half = half });

    /// <summary>Tells <paramref name="reactive"/> the music at <paramref name="anchor"/> every frame it is reached.</summary>
    public static void Add(IBeatReactive reactive, Node3D anchor) =>
        Push(new Entry { Node = anchor, Custom = reactive, Amount = 1f });

    private static void Push(in Entry e)
    {
        if (_entryCount == _entries.Length) System.Array.Resize(ref _entries, _entries.Length * 2);
        _entries[_entryCount++] = e;
    }

    private static void StepRegistry(Vector3? camera)
    {
        for (int i = 0; i < _entryCount; i++)
        {
            ref var e = ref _entries[i];
            if (!GodotObject.IsInstanceValid(e.Node))
            {
                _entries[i] = _entries[--_entryCount];
                _entries[_entryCount] = default;
                i--;
                continue;
            }
            if (!e.Moved && _count == 0) continue;
            float reach = 0f;
            var groove = RadioGroove.Silent;
            if (_count > 0 && e.Node.IsInsideTree() && e.Node.IsVisibleInTree())
            {
                var at = e.Node.GlobalPosition;
                if (camera is not { } cam || at.DistanceSquaredTo(cam) < AnimateRange * AnimateRange)
                    reach = At(at, out groove);
            }
            if (reach < 0.01f || !groove.Beating)
            {
                if (!e.Moved) continue;
                e.Moved = false;
                if (e.Custom != null) e.Custom.OnBeat(0f, RadioGroove.Silent);
                else e.Node.Transform = e.Rest;
                continue;
            }
            e.Moved = true;
            if (e.Custom != null) { e.Custom.OnBeat(reach, groove); continue; }
            e.Node.Transform = e.Rest * RadioBody.Bounce(groove.Phase, groove.Beat, e.Half, 0.8f * e.Amount * reach * groove.BounceScale);
        }
    }
}
