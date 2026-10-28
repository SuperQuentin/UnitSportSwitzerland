using Godot;
using UnitSport.Net;

namespace UnitSport.Interiors;

/// <summary>
/// A lane pair's moving parts on a client (#1323): its twenty pins, each lane's pinsetter table and
/// sweep, and the balls on the hood. Each is built once and moved as a node: the pins are one
/// <see cref="MultiMeshInstance3D"/> whose instance transforms change only while something moves.
/// What they do comes from a start time on the shared clock (<see cref="PinsetterCycle"/>), so every
/// peer shows the same machine without anything sent while it runs. Asleep (no processing) when no
/// lane of the pair is cycling.
/// </summary>
public partial class LaneRig : Node3D
{
    /// <summary>Where a pin is: standing on its spot or knocked off it (still on the deck), or gone into the pit.</summary>
    public enum PinState : byte { Standing, Down, Gone }

    private sealed class Lane
    {
        public readonly Transform3D[] Pins = new Transform3D[10];
        public readonly PinState[] State = new PinState[10];
        public MeshInstance3D Table = null!, Sweep = null!;
        public PinsetterCycle.Kind Kind;
        public double CycleStart = double.NaN;
        /// <summary>Pins standing when the cycle started: the ones a pick lifts and sets back.</summary>
        public ushort Picked;
    }

    private static ArrayMesh? _pinMesh, _sweepMesh;
    private static readonly Dictionary<bool, ArrayMesh> TableMeshes = new();
    private static readonly Dictionary<int, ArrayMesh> BallMeshes = new();

    public LaneGeometry Geometry { get; private set; }
    /// <summary>The clock cycles are timed on: the shared one in game, a local one in the model viewer.</summary>
    public Func<double> Clock = () => ClockSync.SimNow;

    private readonly Lane[] _lanes = { new(), new() };
    private MultiMeshInstance3D _pins = null!;
    private readonly List<MeshInstance3D> _balls = new();
    private Material _material = null!;

    public static LaneRig Create(FurniturePlan p, Transform3D piece, Material material)
    {
        var g = LaneGeometry.Of(p);
        var rig = new LaneRig { Name = "LaneRig", Transform = piece, Geometry = g, _material = material };
        _pinMesh ??= Mesh(InteriorMeshBuilder.Pin());
        _sweepMesh ??= Mesh(InteriorMeshBuilder.Sweep());
        if (!TableMeshes.TryGetValue(g.Mini, out var table))
            TableMeshes[g.Mini] = table = Mesh(InteriorMeshBuilder.PinTable(g.Scale));
        // headless (the z-fight check) a MultiMesh's instances all read at its origin, on the hall's floor:
        // the centres' baseline lines carry those 200 places a pair (zfight note); in game they stand on the deck
        rig._pins = new MultiMeshInstance3D
        {
            Name = "Pins",
            MaterialOverride = material,
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _pinMesh, InstanceCount = 20 },
        };
        rig.AddChild(rig._pins);
        var rest = PinsetterCycle.Rest(g);
        for (int lane = 0; lane < 2; lane++)
        {
            var l = rig._lanes[lane];
            float x = g.LaneX(lane);
            l.Table = new MeshInstance3D { Name = $"Table{lane}", Mesh = table, MaterialOverride = material, Position = new Vector3(x, rest.TableY, g.HeadPin) };
            l.Sweep = new MeshInstance3D { Name = $"Sweep{lane}", Mesh = _sweepMesh, MaterialOverride = material, Position = new Vector3(x, rest.SweepY, rest.SweepZ) };
            rig.AddChild(l.Table);
            rig.AddChild(l.Sweep);
            rig.SetRack(lane);
        }
        rig.SetProcess(false);
        return rig;
    }

    // built once for every centre, without a material: each interior's own (its building's lights) goes on as an override
    private static ArrayMesh Mesh(InteriorMeshBuilder.MeshData data)
    {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Godot.Mesh.ArrayType.Color] = data.Colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>A full rack of ten standing on lane <paramref name="lane"/>'s spots.</summary>
    public void SetRack(int lane)
    {
        _pinsStill[lane] = true;
        var l = _lanes[lane];
        for (int i = 0; i < 10; i++)
        {
            l.Pins[i] = new Transform3D(Basis.Identity, Geometry.PinSpot(lane, i));
            l.State[i] = PinState.Standing;
        }
        Push(lane);
    }

    /// <summary>A pin of lane <paramref name="lane"/> placed as a roll left it (the replayed clip's last frame).</summary>
    public void SetPin(int lane, int pin, Transform3D at, PinState state)
    {
        _lanes[lane].Pins[pin] = at;
        _lanes[lane].State[pin] = state;
        Push(lane);
    }

    public PinState StateOf(int lane, int pin) => _lanes[lane].State[pin];
    public Transform3D PinAt(int lane, int pin) => _lanes[lane].Pins[pin];

    /// <summary>
    /// Starts lane <paramref name="lane"/>'s deck cycle at <paramref name="start"/> on <see cref="Clock"/>:
    /// a pick lifts the pins standing now and sets them back, a reset clears the deck for a new rack.
    /// </summary>
    public void StartCycle(int lane, PinsetterCycle.Kind kind, double start)
    {
        var l = _lanes[lane];
        l.Kind = kind;
        l.CycleStart = start;
        _machineHeard[lane] = false;
        l.Picked = 0;
        for (int i = 0; i < 10; i++)
            if (l.State[i] == PinState.Standing) l.Picked |= (ushort)(1 << i);
        SetProcess(true);
    }

    public bool Cycling(int lane) => !double.IsNaN(_lanes[lane].CycleStart);

    public override void _Process(double delta)
    {
        bool any = false;
        double now = Clock();
        for (int lane = 0; lane < 2; lane++)
        {
            var l = _lanes[lane];
            if (double.IsNaN(l.CycleStart)) continue;
            float t = (float)(now - l.CycleStart);
            if (t < 0) { any = true; continue; }
            if (!_machineHeard[lane])
            {
                _machineHeard[lane] = true;
                DeckSound(lane, BowlingSounds.Machine, new Vector3(Geometry.LaneX(lane), 1f, Geometry.HeadPin), -6f);
            }
            Step(lane, PinsetterCycle.At(Geometry, l.Kind, t));
            if (t >= PinsetterCycle.Length(l.Kind)) { l.CycleStart = double.NaN; _pinsStill[lane] = true; }
            else any = true;
        }
        bool replay = StepReplay(delta);
        if (!any && !replay) SetProcess(false);
    }

    private void Step(int lane, PinsetterCycle.Pose pose)
    {
        var g = Geometry;
        var l = _lanes[lane];
        float x = g.LaneX(lane);
        l.Table.Position = new Vector3(x, pose.TableY, g.HeadPin);
        l.Sweep.Position = new Vector3(x, pose.SweepY, pose.SweepZ);
        bool sweepLow = pose.SweepY < LaneGeometry.DeckTop + 0.1f;
        for (int i = 0; i < 10; i++)
        {
            var spot = g.PinSpot(lane, i);
            bool picked = (l.Picked & (1 << i)) != 0;
            if (pose.Carry == PinsetterCycle.Carry.Rack || (pose.Carry == PinsetterCycle.Carry.Standing && picked))
            {
                // hanging from the table, its head in the grip
                l.Pins[i] = new Transform3D(Basis.Identity, spot with { Y = pose.TableY - PinsetterCycle.Grip });
                l.State[i] = PinState.Standing;
                continue;
            }
            if (pose.Placed && (l.Kind == PinsetterCycle.Kind.Reset || picked))
            {
                l.Pins[i] = new Transform3D(Basis.Identity, spot);
                l.State[i] = PinState.Standing;
                continue;
            }
            if (l.State[i] == PinState.Gone || !sweepLow) continue;
            // the deadwood (and on a reset, everything) pushed ahead of the sweep, into the pit
            var at = l.Pins[i];
            float front = pose.SweepZ + LaneGeometry.PinRadius;
            if (at.Origin.Z < front)
            {
                at.Origin = at.Origin with { Z = front };
                if (l.State[i] == PinState.Standing) at.Basis = new Basis(Vector3.Right, Mathf.Pi / 2) * at.Basis;
                l.State[i] = PinState.Down;
            }
            // over the deck's end it tumbles down into the pit, and is gone only once it is down there (#1334): it
            // used to vanish the moment the sweep pushed it past the edge
            float over = at.Origin.Z - g.LaneEnd;
            if (over > 0)
            {
                float drop = Mathf.Min(PitDrop * g.Scale, over * 2.5f);
                at.Origin = at.Origin with { Y = Mathf.Min(at.Origin.Y, LaneGeometry.DeckTop + LaneGeometry.PinRadius - drop) };
                if (over * 2.5f >= PitDrop * g.Scale) l.State[i] = PinState.Gone;
            }
            l.Pins[i] = at;
        }
        Push(lane);
    }

    /// <summary>How far a pin pushed over the deck's end falls into the pit before it is gone, m (the pit's depth, under the masking).</summary>
    private const float PitDrop = 0.45f;

    // a pin gone into the pit is folded away to nothing: the machine has it now
    private static readonly Transform3D Hidden = new(Basis.Identity.Scaled(Vector3.Zero), Vector3.Zero);

    private void Push(int lane)
    {
        var l = _lanes[lane];
        var mm = _pins.Multimesh;
        for (int i = 0; i < 10; i++)
            mm.SetInstanceTransform(lane * 10 + i, l.State[i] == PinState.Gone ? Hidden : l.Pins[i]);
    }

    // ---- a roll shown: the ball on the lane, then the deck's frames ----------------------------------

    private readonly MeshInstance3D?[] _rolling = new MeshInstance3D?[2];
    private readonly int[] _rollingColor = { -1, -1 };

    private MeshInstance3D RollingBall(int lane, int color)
    {
        var ball = _rolling[lane];
        if (ball == null)
        {
            ball = _rolling[lane] = new MeshInstance3D { Name = $"Rolling{lane}", MaterialOverride = _material };
            AddChild(ball);
            if (Audible)
            {
                _rollVoice[lane] = new AudioStreamPlayer3D { Name = "Roll", Bus = Audio.SfxBus.Name, UnitSize = 6f, MaxDistance = 45f, VolumeDb = -4f };
                ball.AddChild(_rollVoice[lane]);
            }
        }
        if (_rollingColor[lane] != color)
        {
            _rollingColor[lane] = color;
            ball.Mesh = BallMesh(color);
        }
        ball.Visible = true;
        return ball;
    }

    public void HideBall(int lane)
    {
        if (_rolling[lane] is { Visible: true } ball) ball.Visible = false;
        if (_rollVoice[lane] is { Playing: true } voice) voice.Stop();
        _lastBall[lane] = (double.NaN, Vector3.Zero);
    }

    // ---- sound ---------------------------------------------------------------------------------

    private static bool Audible => DisplayServer.GetName() != "headless";
    private readonly AudioStreamPlayer3D?[] _rollVoice = new AudioStreamPlayer3D?[2];
    private readonly AudioStreamPlayer3D?[] _deckVoice = new AudioStreamPlayer3D?[2];
    private AudioStreamPlayer3D? _hoodVoice;
    private readonly (double At, Vector3 Pos)[] _lastBall = { (double.NaN, Vector3.Zero), (double.NaN, Vector3.Zero) };
    /// <summary>Whether a lane's pins stand still on their spots: the first movement after is the crash.</summary>
    private readonly bool[] _pinsStill = { true, true };
    private readonly bool[] _machineHeard = { true, true };

    /// <summary>The rolling ball's rumble (or the gutter's rattle), pitched by its speed.</summary>
    private void RollSound(int lane, Vector3 at, bool gutter)
    {
        if (_rollVoice[lane] is not { } voice) return;
        double now = Core.RealClock.Now;
        var (was, from) = _lastBall[lane];
        _lastBall[lane] = (now, at);
        if (double.IsNaN(was) || now - was < 1e-3) return;
        float speed = (at - from).Length() / (float)(now - was);
        var stream = gutter ? BowlingSounds.Gutter : BowlingSounds.Roll;
        if (voice.Stream != stream) { voice.Stream = stream; voice.Stop(); }
        if (!voice.Playing && speed > 0.5f) voice.Play();
        voice.PitchScale = Mathf.Clamp(0.6f + speed / 12f, 0.5f, 1.6f);
    }

    private void DeckSound(int lane, AudioStream stream, Vector3 at, float db = 0f)
    {
        if (!Audible) return;
        var voice = _deckVoice[lane] ??= new AudioStreamPlayer3D { Name = $"Deck{lane}", Bus = Audio.SfxBus.Name, UnitSize = 8f, MaxDistance = 60f };
        if (voice.GetParent() == null) AddChild(voice);
        voice.Stream = stream;
        voice.VolumeDb = db;
        voice.Position = at;
        voice.Play();
    }

    /// <summary>
    /// Lane <paramref name="lane"/>'s ball on its run, at <paramref name="pos"/> in the run's frame
    /// (<see cref="BowlingBall"/>: from the foul line, y up from the lane), stepping up onto the deck's
    /// height over its last metre.
    /// </summary>
    public void ShowBall(int lane, Vector3 pos, Quaternion turn, int color, bool onDeck)
    {
        var g = Geometry;
        float deck = g.HeadPin - g.Foul - BowlingBall.HandOver;
        float up = onDeck ? 1 : Mathf.SmoothStep(deck - 1f, deck, pos.Z);
        var at = new Vector3(g.LaneX(lane) + pos.X, LaneGeometry.LaneTop + up * (LaneGeometry.DeckTop - LaneGeometry.LaneTop) + pos.Y, g.Foul + pos.Z);
        RollingBall(lane, color).Transform = new Transform3D(new Basis(turn), at);
        RollSound(lane, at, Mathf.Abs(pos.X) > LaneGeometry.HalfLane);
    }

    /// <summary>
    /// Lane <paramref name="lane"/>'s deck between two of the server's frames (the ball, then the ten
    /// pins, in the deck's frame), <paramref name="w"/> of the way from <paramref name="a"/> to <paramref name="b"/>.
    /// </summary>
    public void ShowDeck(int lane, Transform3D[] a, Transform3D[] b, float w, int color)
    {
        var g = Geometry;
        var origin = new Vector3(g.LaneX(lane), LaneGeometry.DeckTop, g.Foul);
        Transform3D Lerp(int i) => w <= 0 ? a[i] : a[i].InterpolateWith(b[i], w);
        var ball = Lerp(0);
        if (ball.Origin.Y < -1f) HideBall(lane);
        else RollingBall(lane, color).Transform = ball with { Origin = ball.Origin + origin };
        var l = _lanes[lane];
        if (_pinsStill[lane])
        {
            int moved = 0;
            for (int i = 0; i < 10; i++)
                if (l.State[i] == PinState.Standing && (Lerp(i + 1).Origin + origin).DistanceTo(l.Pins[i].Origin) > 0.03f) moved++;
            if (moved > 0)
            {
                _pinsStill[lane] = false;
                DeckSound(lane, moved >= 4 ? BowlingSounds.Crash : BowlingSounds.Light, new Vector3(g.LaneX(lane), 0.3f, g.HeadPin + 0.3f));
            }
        }
        if (ball.Origin.Y > -1f) RollSound(lane, ball.Origin + origin, false);
        for (int i = 0; i < 10; i++)
        {
            var t = Lerp(i + 1);
            if (t.Origin.Y < -0.35f) { l.State[i] = PinState.Gone; continue; }
            l.Pins[i] = t with { Origin = t.Origin + origin };
            l.State[i] = t.Basis.Y.Normalized().Y > 0.94f ? PinState.Standing : PinState.Down;
        }
        Push(lane);
    }

    /// <summary>Lane <paramref name="lane"/>'s pins at rest as <paramref name="standing"/> says: those on their spots, the rest gone.</summary>
    public void SetStanding(int lane, ushort standing)
    {
        _pinsStill[lane] = true;
        var l = _lanes[lane];
        for (int i = 0; i < 10; i++)
        {
            bool up = (standing & (1 << i)) != 0;
            l.Pins[i] = new Transform3D(Basis.Identity, Geometry.PinSpot(lane, i));
            l.State[i] = up ? PinState.Standing : PinState.Gone;
        }
        Push(lane);
    }

    private readonly List<int> _hoodShown = new();

    /// <summary>The thunk of a ball pushed up into the hood (a player's, or a decoration bowler's, #1334).</summary>
    public void HoodThunk()
    {
        if (!Audible) return;
        _hoodVoice ??= new AudioStreamPlayer3D { Name = "Hood", Bus = Audio.SfxBus.Name, UnitSize = 5f, MaxDistance = 35f, Stream = BowlingSounds.Thunk, Position = Geometry.HoodLift };
        if (_hoodVoice.GetParent() == null) AddChild(_hoodVoice);
        _hoodVoice.Play();
    }

    /// <summary>
    /// The balls waiting on the hood's rack: one per entry (a house ball's colour), in slot order.
    /// Rebuilt only when the set changes.
    /// </summary>
    public void SetHoodBalls(IReadOnlyList<int> colors)
    {
        if (_hoodShown.SequenceEqual(colors)) return;
        if (colors.Count > _hoodShown.Count) HoodThunk();
        _hoodShown.Clear();
        _hoodShown.AddRange(colors);
        while (_balls.Count < colors.Count)
        {
            var b = new MeshInstance3D { Name = $"Ball{_balls.Count}", MaterialOverride = _material };
            AddChild(b);
            _balls.Add(b);
        }
        for (int i = 0; i < _balls.Count; i++)
        {
            bool shown = i < colors.Count && i < LaneGeometry.HoodSlots;
            _balls[i].Visible = shown;
            if (!shown) continue;
            _balls[i].Mesh = BallMesh(colors[i]);
            _balls[i].Position = Geometry.HoodSlot(i);
        }
    }

    /// <summary>A house ball's mesh of colour <paramref name="color"/>, shared by every centre (the material goes on the instance).</summary>
    public static ArrayMesh BallMeshOf(int color) => BallMesh(color);

    private static ArrayMesh BallMesh(int color)
    {
        if (!BallMeshes.TryGetValue(color, out var mesh))
            BallMeshes[color] = mesh = Mesh(InteriorMeshBuilder.Ball(color));
        return mesh;
    }
}
