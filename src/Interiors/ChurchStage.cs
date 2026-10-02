using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Net;
using static UnitSport.Interiors.InteriorMeshBuilder;

namespace UnitSport.Interiors;

/// <summary>
/// A church's figures and what the chess type beat does to them (#370): the pastor rat and its
/// congregation as nodes (<see cref="Figure"/>), the rat dance, the night-club lights
/// (<c>ChurchStage.Disco.cs</c>) and the intro's camera (<c>ChurchStage.Intro.cs</c>). A child of
/// the church's <see cref="InteriorNode"/>, so everything here is in interior-local space.
///
/// <para>
/// One rule drives it all: <see cref="ChurchRadios.RatBeatPlaying"/>. Every pose is a function of
/// the replicated play (CD, start) and the shared clock, so every peer sees the same dance on the
/// same beat with nothing else on the wire. The moment the radio stops, or plays anything else,
/// everything is put back as it was, in that frame: seated, unlit, the player's own camera.
/// </para>
/// </summary>
public partial class ChurchStage : Node3D
{
    /// <summary>When the trumpet comes in if the CD's grid says nothing better, s (#370).</summary>
    public const float IntroEnd = 2.0f;

    /// <summary>
    /// When the trumpet comes in, s from the CD's start: on the beat after the five intro hits
    /// (2.36 s on the chess type beat's grid), else <see cref="IntroEnd"/>.
    /// </summary>
    public static float IntroEndOf(CdInfo cd)
    {
        float end = cd.BeatOffset + IntroHits * 60f / cd.Bpm;
        return cd.BeatOffset >= 0 && end > 1.6f && end < 2.8f ? end : IntroEnd;
    }

    /// <summary>Hits before the trumpet, one pose (and one camera cut) each.</summary>
    public const int IntroHits = 5;

    private string _plan = "";
    private InteriorNode _interior = null!;
    private Figure[] _figures = Array.Empty<Figure>();
    private Node3D[][] _parts = Array.Empty<Node3D[]>();
    private Transform3D[][] _rest = Array.Empty<Transform3D[]>();
    /// <summary>Every mesh of this church the disco tints: the room and each figure's parts.</summary>
    private readonly List<MeshInstance3D> _meshes = new();
    private int _rat = -1;

    /// <summary>The chess type beat plays here and everything is on.</summary>
    private bool _active;
    private int _standing = -1;

    public static ChurchStage Create(InteriorNode interior, Figure[] figures, Material material, MeshInstance3D room)
    {
        var stage = new ChurchStage
        {
            Name = "ChurchStage", _plan = interior.Layout.Key, _interior = interior, _figures = figures,
        };
        stage._meshes.Add(room);
        stage._parts = new Node3D[figures.Length][];
        stage._rest = new Transform3D[figures.Length][];
        for (int f = 0; f < figures.Length; f++)
        {
            var figure = figures[f];
            if (figure.Kind == FigureKind.Rat && stage._rat < 0) stage._rat = f;
            var root = new Node3D { Name = $"Figure{f}", Transform = figure.Frame };
            stage.AddChild(root);
            var parts = new Node3D[figure.Parts.Length];
            var rest = new Transform3D[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                var part = figure.Parts[i];
                var parentPivot = part.Parent >= 0 ? figure.Parts[part.Parent].Pivot : Vector3.Zero;
                rest[i] = new Transform3D(Basis.Identity, part.Pivot - parentPivot);
                var node = new Node3D { Name = $"Part{i}", Transform = rest[i], Visible = !part.Hidden };
                (part.Parent >= 0 ? parts[part.Parent] : root).AddChild(node);
                parts[i] = node;
                if (part.Vertices.Length == 0) continue;
                var mesh = new MeshInstance3D
                {
                    Name = "Mesh",
                    Mesh = InteriorNode.BuildMesh(new MeshData(part.Vertices, part.Colors, Array.Empty<Vector3>()), material),
                };
                node.AddChild(mesh);
                stage._meshes.Add(mesh);
            }
            stage._parts[f] = parts;
            stage._rest[f] = rest;
        }
        return stage;
    }

    public override void _Process(double delta)
    {
        bool on = ChurchRadios.RatBeatPlaying(_plan, out var play)
            && CdLibrary.Instance?.Find(play.CdId) is { Bpm: >= 1f } cd;
        if (!on)
        {
            if (_active) Stop();
            return;
        }
        var info = CdLibrary.Instance!.Find(play.CdId)!;
        double t = ClockSync.ServerNow - play.StartedAt;
        if (!_active) Start(info, t);

        // beats since the first one, continuous: the dance's whole clock
        float beat = (float)((t - info.BeatOffset) * info.Bpm / 60.0);
        bool intro = t < IntroEndOf(info);
        Pose((float)t, beat, intro, info);
        StepDisco(beat);
        StepIntro((float)t, intro, info);
    }

    public override void _ExitTree()
    {
        if (_active) Stop();
    }

    private void Start(CdInfo cd, double t)
    {
        _active = true;
        StartDisco(cd);
        StartIntro(cd, t);
    }

    /// <summary>The music stopped (or changed): everything as it was, in this frame.</summary>
    private void Stop()
    {
        _active = false;
        for (int f = 0; f < _parts.Length; f++)
            for (int i = 0; i < _parts[f].Length; i++)
            {
                _parts[f][i].Transform = _rest[f][i];
                _parts[f][i].Visible = !_figures[f].Parts[i].Hidden;
            }
        _standing = -1;
        StopDisco();
        StopIntro();
    }

    // ---- the intro's hits ------------------------------------------------------------------------

    /// <summary>
    /// When hit <paramref name="k"/> of the intro lands, s from the CD's start: on the CD's own beats
    /// when its first five fall before the trumpet, else evenly over the intro (the analyser may
    /// have read half or double the tempo).
    /// </summary>
    public static float HitTime(CdInfo cd, int k)
    {
        float spb = 60f / cd.Bpm, last = cd.BeatOffset + (IntroHits - 1) * spb;
        if (cd.BeatOffset >= 0 && last < IntroEndOf(cd) - 0.05f && last > IntroEnd * 0.6f) return cd.BeatOffset + k * spb;
        return k * IntroEnd / IntroHits;
    }

    /// <summary>The last intro hit at or before <paramref name="t"/>, -1 before the first.</summary>
    public static int HitAt(CdInfo cd, float t)
    {
        int k = -1;
        for (int i = 0; i < IntroHits; i++)
            if (HitTime(cd, i) <= t) k = i;
        return k;
    }

    // ---- poses -----------------------------------------------------------------------------------

    private void Pose(float t, float beat, bool intro, CdInfo cd)
    {
        // the congregation gets up on the trumpet, in a quarter of a second
        float end = IntroEndOf(cd);
        float stand = intro ? 0f : Mathf.Clamp((t - end) / 0.25f, 0f, 1f);
        bool up = stand > 0.5f;
        if ((up ? 1 : 0) != _standing)
        {
            _standing = up ? 1 : 0;
            for (int f = 0; f < _figures.Length; f++)
            {
                if (_figures[f].Kind != FigureKind.Person) continue;
                _parts[f][PersonParts.LegsSeated].Visible = !up;
                _parts[f][PersonParts.LegsStanding].Visible = up;
            }
        }
        for (int f = 0; f < _figures.Length; f++)
        {
            if (_figures[f].Kind == FigureKind.Rat)
            {
                if (intro) RatIntro(f, t, cd);
                else RatDance(f, beat, t - end);
            }
            else if (!intro) PersonDance(f, beat, stand);
        }
    }

    private void Set(int f, int part, Basis rotation, Vector3 offset = default) =>
        _parts[f][part].Transform = new Transform3D(rotation, _rest[f][part].Origin + offset);

    private static Basis Rot(float pitch, float yaw, float roll) => Basis.FromEuler(new Vector3(pitch, yaw, roll));

    private static float Smooth(float x)
    {
        x = Mathf.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>A pose of the rat: angles in radians, the body's drop in rat heights.</summary>
    private readonly record struct RatPose(float BodyRoll, float BodyPitch, float BodyDrop, float HeadYaw, float HeadPitch,
        float HeadRoll, float ArmL, float ArmR);

    /// <summary>The intro: standing, then one pose snapped onto each of the five hits.</summary>
    private static readonly RatPose[] IntroPoses =
    {
        new(0, 0, 0, 0, 0, 0, 0, 0),
        new(0, 0, 0, 0.6f, 0, 0.25f, 0.3f, -0.3f),          // the head turns to the camera
        new(0, -0.1f, 0, 0, -0.3f, 0, -1.5f, 1.5f),         // arms up
        new(0.35f, 0, 0, 0, 0, -0.3f, -1.1f, 0.2f),         // lean one way
        new(-0.35f, 0, 0, 0, 0, 0.3f, -0.2f, 1.1f),         // lean the other
        new(0, 0.3f, 0.10f, 0, 0.3f, 0, 0.5f, -0.5f),       // crouch, ready to spring
    };

    private void RatIntro(int f, float t, CdInfo cd)
    {
        int k = HitAt(cd, t);
        var from = IntroPoses[k < 0 ? 0 : k];
        var to = IntroPoses[k + 1];
        // snapped onto the hit in 80 ms, held until the next
        float e = k < 0 ? 0f : Smooth((t - HitTime(cd, k)) / 0.08f);
        if (k < 0) to = from;
        ApplyRat(f, Lerp(from, to, e), 0f, 0f);
    }

    private static RatPose Lerp(in RatPose a, in RatPose b, float e) => new(
        Mathf.Lerp(a.BodyRoll, b.BodyRoll, e), Mathf.Lerp(a.BodyPitch, b.BodyPitch, e), Mathf.Lerp(a.BodyDrop, b.BodyDrop, e),
        Mathf.Lerp(a.HeadYaw, b.HeadYaw, e), Mathf.Lerp(a.HeadPitch, b.HeadPitch, e), Mathf.Lerp(a.HeadRoll, b.HeadRoll, e),
        Mathf.Lerp(a.ArmL, b.ArmL, e), Mathf.Lerp(a.ArmR, b.ArmR, e));

    /// <summary>
    /// The rat dance: the body swings side to side, a beat each way, the head tilting against it;
    /// the arms pump up on their side; a bounce on every beat, a hop off the crouch when the trumpet
    /// comes in, and every eighth bar a full turn.
    /// </summary>
    private void RatDance(int f, float b, float since)
    {
        float s = Mathf.Sin(Mathf.Pi * b);
        float ph = b - Mathf.Floor(b);
        float pulse = Mathf.Exp(-ph * 5f);
        int bar = Mathf.FloorToInt(b / 4f);
        float spin = ((bar % 8) + 8) % 8 == 7 ? Mathf.Tau * Smooth(b / 4f - bar) : 0f;
        var pose = new RatPose(0.22f * s, 0.08f * pulse, 0f, -0.25f * s, 0.25f * pulse, -0.35f * s,
            -(0.3f + 0.9f * Mathf.Max(0f, s)), 0.3f + 0.9f * Mathf.Max(0f, -s));
        float hop = 0.05f * Mathf.Abs(s) + (since < 0.35f ? 0.3f * Mathf.Sin(Mathf.Pi * since / 0.35f) : 0f);
        ApplyRat(f, pose, hop, spin, sway: 0.05f * s, tail: 0.7f * Mathf.Sin(Mathf.Tau * b));
    }

    private void ApplyRat(int f, in RatPose p, float hop, float spin, float sway = 0f, float tail = 0f)
    {
        Set(f, RatParts.Body, Rot(p.BodyPitch, spin, p.BodyRoll), new Vector3(sway, hop - p.BodyDrop, 0));
        Set(f, RatParts.Head, Rot(p.HeadPitch, p.HeadYaw, p.HeadRoll));
        Set(f, RatParts.ArmL, Rot(0, 0, p.ArmL));
        Set(f, RatParts.ArmR, Rot(0, 0, p.ArmR));
        Set(f, RatParts.Tail, Rot(0, tail, 0));
    }

    /// <summary>
    /// A congregant dancing (standing <paramref name="stand"/> 0..1): its own variant of the rat's
    /// moves, amplitude and a little phase from its hash, so the row is a crowd and not a drill
    /// team; but every fourth bar they all copy the rat together.
    /// </summary>
    private void PersonDance(int f, float beat, float stand)
    {
        int h = _figures[f].Hash;
        float amp = 0.7f + (h >> 8) % 50 / 100f;
        float b = beat - (h >> 16) % 10 / 100f;
        int bar = Mathf.FloorToInt(beat / 4f);
        int variant = ((bar % 4) + 4) % 4 == 3 ? 0 : h % 5;
        float s = Mathf.Sin(Mathf.Pi * b);
        float ph = b - Mathf.Floor(b);
        float pulse = Mathf.Exp(-ph * 5f);
        var up = new Vector3(0, 0, StandForward * stand);

        Basis torso = Basis.Identity, head = Basis.Identity, armL = Basis.Identity, armR = Basis.Identity;
        var lift = Vector3.Zero;
        switch (variant)
        {
            case 0: // the rat's swing
                torso = Rot(0, 0.25f * s * amp, 0.22f * s * amp);
                head = Rot(0.2f * pulse, 0, -0.3f * s * amp);
                armL = Rot(0, 0, -(0.4f + 1.6f * Mathf.Max(0f, s)) * amp);
                armR = Rot(0, 0, (0.4f + 1.6f * Mathf.Max(0f, -s)) * amp);
                break;
            case 1: // head-bang, fists forward
                torso = Rot(0.15f * pulse * amp, 0, 0);
                head = Rot(0.45f * pulse * amp, 0, 0);
                armL = armR = Rot(-1.2f + 0.3f * pulse, 0, 0);
                break;
            case 2: // arms up, waving
                torso = Rot(0, 0, 0.15f * s * amp);
                head = Rot(-0.2f, 0, -0.2f * s);
                armL = Rot(0, 0, -(2.5f + 0.3f * s) * amp);
                armR = Rot(0, 0, (2.5f - 0.3f * s) * amp);
                break;
            case 3: // half-time bounce
                lift = new Vector3(0, 0.06f * Mathf.Abs(Mathf.Sin(Mathf.Pi * b / 2f)) * amp, 0);
                head = Rot(0.25f * pulse, 0, 0);
                armL = Rot(0, 0, -0.5f * Mathf.Abs(s) * amp);
                armR = Rot(0, 0, 0.5f * Mathf.Abs(s) * amp);
                break;
            default: // clap on two and four
                float clap = Mathf.FloorToInt(b) % 2 != 0 ? pulse : 0f;
                torso = Rot(0, 0, 0.1f * s * amp);
                head = Rot(0.15f * pulse, 0.2f * s, 0);
                armL = Rot(-1.1f, -0.5f * clap, 0);
                armR = Rot(-1.1f, 0.5f * clap, 0);
                break;
        }
        // standing up: the upper body moves over the standing legs, then dances
        Set(f, PersonParts.Torso, Weigh(torso, stand), up + lift * stand);
        Set(f, PersonParts.Head, Weigh(head, stand));
        Set(f, PersonParts.ArmL, Weigh(armL, stand));
        Set(f, PersonParts.ArmR, Weigh(armR, stand));
    }

    private static Basis Weigh(Basis b, float w) => w >= 1f ? b : Basis.Identity.Slerp(b.Orthonormalized(), w);
}
