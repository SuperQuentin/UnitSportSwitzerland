using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Farming;
using UnitSport.Terrain;

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>
/// The Désalpe's herd drawn on this client (#1269 part 7, <c>docs/notes/occasions/festival-desalpe.md</c>): the server's 8 Hz snapshots
/// (<see cref="DesalpeWire"/>) eased into place, the cows drawn as MultiMeshes beside the field herds' own: bodies and heads from
/// <see cref="CowMeshBuilder"/>, a Treichel at each neck (swinging with the walk, three sizes by scale), and one MultiMesh for each
/// headdress that is worn (the style material takes no per-instance colour, so a shape in a colour is a mesh; only the groups in use exist and a group's
/// instances are the cows that wear it). Nothing is rebuilt per frame: a pose writes four float buffers, one engine call each. The Treichel
/// rings from the nearest cows while the herd walks.
/// </summary>
public partial class DesalpeHerd : Node3D
{
    public static DesalpeHerd? Instance { get; private set; }

    private const float DrawRange = 1500f;

    private readonly Func<WorldOrigin?> _origin;
    private readonly Func<ChunkManager?> _chunks;
    private readonly Random _rng = new();
    private FestivalService? _svc;
    private ResolvedSite? _site;
    private CowView[] _target = [];
    private DesalpePhase _phase;
    private float _progress;
    private float[] _x = [], _y = [], _yaw = [], _gait = [], _pitch = [], _ground = [];
    private Vector2[] _groundAt = [];
    private Transform3D[] _headAt = [];
    private byte[] _dress = [];
    private float _anchorGround = float.NaN;
    private MultiMeshInstance3D? _bodies, _heads, _bells;
    private float[] _bodyBuf = [], _headBuf = [], _bellBuf = [];
    private readonly Dictionary<byte, Group> _groups = new();
    private bool _groupsStale = true, _fresh = true;
    private double _t, _bellIn = 0.6, _mooIn = 6;
    private AudioStreamPlayer3D[] _voices = [];
    private int _voice;

    private sealed class Group
    {
        public required MultiMeshInstance3D Node;
        public int[] Cows = [];
        public float[] Buffer = [];
    }

    public DesalpeHerd(Func<WorldOrigin?> origin, Func<ChunkManager?> chunks)
    {
        _origin = origin;
        _chunks = chunks;
        Name = "DesalpeHerd";
    }

    public DesalpeHerd() : this(() => null, () => null) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        Unhook();
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        _voices = new AudioStreamPlayer3D[4];
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AudioStreamPlayer3D
            {
                Name = $"Voice{i}", Bus = SfxBus.Name, UnitSize = 10f, MaxDistance = 200f,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            };
            AddChild(_voices[i]);
        }
    }

    // ---- what the server told ----------------------------------------------------------------------

    public int Count => _target.Length;

    public DesalpePhase Phase => _phase;

    /// <summary>How far the Leitkuh has come, 0..1 of the route.</summary>
    public float Progress => _progress;

    public CowState StateOf(int cow) => cow >= 0 && cow < _target.Length ? _target[cow].State : CowState.Lost;

    public byte DressOf(int cow) => cow >= 0 && cow < _dress.Length ? _dress[cow] : (byte)0;

    /// <summary>Where cow <paramref name="cow"/> is drawn, LV95 (the eased position), or null before the first snapshot.</summary>
    public (double E, double N)? CowAt(int cow) => _site == null || cow < 0 || cow >= _x.Length ? null : (_site.E + _x[cow], _site.N + _y[cow]);

    /// <summary>The cow nearest to an LV95 point and how far it is, or (-1, +inf).</summary>
    public (int Cow, double Distance) Nearest(double e, double n)
    {
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _x.Length; i++)
        {
            double de = _site!.E + _x[i] - e, dn = _site.N + _y[i] - n, d = Math.Sqrt(de * de + dn * dn);
            if (d < bestD) { bestD = d; best = i; }
        }
        return (best, bestD);
    }

    private void Hook()
    {
        if (_svc != null || FestivalService.Instance is not { } svc) return;
        _svc = svc;
        svc.DesalpeSnapped += OnSnapshot;
        svc.DesalpeChanged += OnChanged;
        if (svc.DesalpeSnapshot is { } last) OnSnapshot(last);
        OnChanged();
    }

    private void Unhook()
    {
        if (_svc == null) return;
        _svc.DesalpeSnapped -= OnSnapshot;
        _svc.DesalpeChanged -= OnChanged;
        _svc = null;
    }

    private void OnSnapshot(byte[] bytes)
    {
        if (!DesalpeWire.Unpack(bytes, out var phase, out float progress, out var cows)) return;
        bool resized = cows.Length != _x.Length;
        if (resized) Allocate(cows.Length);
        // a herd that jumped (a fresh one on the alp, or the first snapshot) is put there at once
        for (int i = 0; i < cows.Length && !_fresh; i++)
            if (Math.Abs(cows[i].X - _x[i]) + Math.Abs(cows[i].Y - _y[i]) > 25f) { _fresh = true; break; }
        _target = cows;
        _phase = phase;
        _progress = progress;
        if (_fresh)
        {
            for (int i = 0; i < cows.Length; i++)
            {
                _x[i] = cows[i].X; _y[i] = cows[i].Y; _yaw[i] = cows[i].Yaw; _gait[i] = cows[i].Gait;
                _ground[i] = float.NaN;
            }
            _fresh = false;
        }
    }

    private void OnChanged()
    {
        var view = _svc?.DesalpeNow;
        _site = _svc?.RunningOf(DesalpeRules.FestivalId)?.Site;
        if (view == null || _site == null)
        {
            _target = [];
            return;
        }
        if (!_dress.AsSpan().SequenceEqual(view.Dress))
        {
            _dress = (byte[])view.Dress.Clone();
            _groupsStale = true;
        }
    }

    private void Allocate(int n)
    {
        _x = new float[n]; _y = new float[n]; _yaw = new float[n]; _gait = new float[n]; _pitch = new float[n]; _ground = new float[n];
        _groundAt = new Vector2[n];
        _headAt = new Transform3D[n];
        Array.Fill(_ground, float.NaN);
        _bodyBuf = new float[n * 12]; _headBuf = new float[n * 12]; _bellBuf = new float[n * 12];
        _fresh = true;
        _groupsStale = true;
        if (_bodies != null) { _bodies.QueueFree(); _heads!.QueueFree(); _bells!.QueueFree(); _bodies = _heads = _bells = null; }
    }

    // ---- drawing ------------------------------------------------------------------------------------

    private MultiMeshInstance3D Make(string name, Mesh mesh, int count) => new()
    {
        Name = name,
        Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = count },
        MaterialOverride = Herds.Material(),
        // the herd walks a long way from its anchor: never cull it by its spread
        CustomAabb = new Aabb(new Vector3(-1200, -200, -1200), new Vector3(2400, 800, 2400)),
    };

    private void Build()
    {
        int n = _x.Length;
        _bodies = Make("Cows", CowMeshBuilder.BodyWithoutBell(CowBreed.Brown), n);
        _heads = Make("Heads", CowMeshBuilder.Head(CowBreed.Brown), n);
        _bells = Make("Treicheln", DesalpeModels.Treichel(), n);
        AddChild(_bodies);
        AddChild(_heads);
        AddChild(_bells);
    }

    /// <summary>The groups of cows that wear the same headdress: rebuilt when the dress changes (rarely), never per frame.</summary>
    private void Regroup()
    {
        _groupsStale = false;
        var by = new Dictionary<byte, List<int>>();
        for (int i = 0; i < _dress.Length && i < _x.Length; i++)
            if (_dress[i] != 0)
            {
                if (!by.TryGetValue(_dress[i], out var list)) by[_dress[i]] = list = new List<int>();
                list.Add(i);
            }
        foreach (var (dress, g) in _groups)
            if (!by.ContainsKey(dress)) { g.Cows = []; g.Node.Multimesh.InstanceCount = 0; g.Node.Visible = false; }
        foreach (var (dress, list) in by)
        {
            if (!_groups.TryGetValue(dress, out var g))
            {
                var node = Make($"Headdress{dress}", DesalpeModels.Headdress(dress), 0);
                AddChild(node);
                _groups[dress] = g = new Group { Node = node };
            }
            g.Cows = list.ToArray();
            g.Node.Multimesh.InstanceCount = g.Cows.Length;
            g.Buffer = new float[g.Cows.Length * 12];
            g.Node.Visible = true;
        }
    }

    private float GroundAt(double e, double n)
    {
        if (_chunks() is not { } chunks || _origin() is not { } origin) return float.NaN;
        var at = origin.ToWorld(e, n, 0);
        if (chunks.TryGetSurface(at, out float s)) return s;
        return chunks.TryGetHeight(at, out float h) ? h : float.NaN;
    }

    public override void _Process(double delta)
    {
        Hook();
        float dt = (float)delta;
        _t += delta;
        if (_site == null || _target.Length == 0 || _origin() is not { } origin || GetViewport()?.GetCamera3D() is not { } cam)
        {
            if (_bodies != null && _bodies.Visible) _bodies.Visible = _heads!.Visible = _bells!.Visible = false;
            return;
        }
        var eye = cam.GlobalPosition;
        var (ee, en) = origin.ToLv95(eye);
        double de = _site.E + _target[0].X - ee, dn = _site.N + _target[0].Y - en;
        if (de * de + dn * dn > DrawRange * DrawRange)
        {
            if (_bodies != null && _bodies.Visible) _bodies.Visible = _heads!.Visible = _bells!.Visible = false;
            foreach (var g in _groups.Values) g.Node.Visible = false;
            return;
        }
        if (_bodies == null) Build();
        if (_groupsStale) Regroup();
        _bodies!.Visible = _heads!.Visible = _bells!.Visible = true;
        foreach (var g in _groups.Values) g.Node.Visible = g.Cows.Length > 0;

        if (float.IsNaN(_anchorGround)) _anchorGround = GroundAt(_site.E, _site.N);
        float anchorY = float.IsNaN(_anchorGround) ? eye.Y : _anchorGround;
        var anchor = origin.ToWorld(_site.E, _site.N, anchorY);
        if (!_bodies.Position.IsEqualApprox(anchor)) _bodies.Position = _heads.Position = _bells.Position = anchor;
        foreach (var g in _groups.Values) if (!g.Node.Position.IsEqualApprox(anchor)) g.Node.Position = anchor;

        float ease = MathX.Damp(8f, dt), turn = MathX.Damp(6f, dt), look = MathX.Damp(3f, dt);
        for (int i = 0; i < _x.Length; i++)
        {
            var t = _target[i];
            _x[i] += (t.X - _x[i]) * ease;
            _y[i] += (t.Y - _y[i]) * ease;
            _yaw[i] += Mathf.AngleDifference(_yaw[i], t.Yaw) * turn;
            _gait[i] += (t.Gait - _gait[i]) * ease;
            var at = new Vector2(_x[i], _y[i]);
            if (float.IsNaN(_ground[i]) || at.DistanceSquaredTo(_groundAt[i]) > 0.16f)
            {
                _ground[i] = GroundAt(_site.E + _x[i], _site.N + _y[i]);
                _groundAt[i] = at;
            }
            float y = float.IsNaN(_ground[i]) ? anchorY : _ground[i];
            var local = new Vector3(_x[i], y - anchorY, -_y[i]);
            if (_gait[i] > 0.5f) local.Y += Mathf.Abs(Mathf.Sin((float)_t * 4.2f + i)) * 0.035f;
            var basis = new Basis(Vector3.Up, _yaw[i]);

            // the head: down grazing, up while walking, up now and then for a look
            bool up = _gait[i] > 0.4f || Mathf.Sin((float)_t * 0.37f + i * 2.1f) > 0.86f;
            _pitch[i] += ((up ? 0.12f : 1f) * CowMeshBuilder.GrazePitch - _pitch[i]) * look;
            var headT = new Transform3D(basis * new Basis(Vector3.Right, -_pitch[i]), local + basis * CowMeshBuilder.Neck);
            MultiMeshPack.Transform(_bodyBuf, i * 12, new Transform3D(basis, local));
            MultiMeshPack.Transform(_headBuf, i * 12, headT);
            // the Treichel swings with the stride about the collar
            float sway = Mathf.Sin((float)_t * 4.2f + i * 1.3f) * 0.38f * _gait[i] + Mathf.Sin((float)_t * 0.8f + i) * 0.03f;
            var bellBasis = (basis * new Basis(Vector3.Right, sway)).Scaled(Vector3.One * DesalpeModels.BellScale(DesalpeRules.BellSize(i)));
            MultiMeshPack.Transform(_bellBuf, i * 12, new Transform3D(bellBasis, local + basis * DesalpeModels.TreichelAt));
            _headAt[i] = headT;
        }
        _bodies.Multimesh.Buffer = _bodyBuf;
        _heads.Multimesh.Buffer = _headBuf;
        _bells.Multimesh.Buffer = _bellBuf;
        foreach (var g in _groups.Values)
        {
            if (g.Cows.Length == 0) continue;
            for (int k = 0; k < g.Cows.Length; k++)
                MultiMeshPack.Transform(g.Buffer, k * 12, _headAt[g.Cows[k]]);
            g.Node.Multimesh.Buffer = g.Buffer;
        }
        Sounds(delta, ee, en, origin, anchorY);
    }

    // ---- the Treichel rings from the cows that are near ------------------------------------------------

    private void Sounds(double delta, double ee, double en, WorldOrigin origin, float anchorY)
    {
        _bellIn -= delta;
        _mooIn -= delta;
        if (_bellIn > 0 && _mooIn > 0) return;
        bool moo = _mooIn <= 0;
        if (moo) _mooIn = 6 + _rng.NextDouble() * 8;
        else _bellIn = 0.45 + _rng.NextDouble() * 0.9;
        int seen = 0, pick = -1;
        float range = moo ? 90f : 70f;
        for (int i = 0; i < _x.Length; i++)
        {
            if (!moo && _gait[i] < 0.3f && i != 0) continue;   // the bells ring where cows walk, the Leitkuh's always a little
            double de = _site!.E + _x[i] - ee, dn = _site.N + _y[i] - en;
            if (de * de + dn * dn > range * range) continue;
            if (_rng.Next(++seen) == 0) pick = i;
        }
        if (pick < 0 || _voices.Length == 0) return;
        float ground = float.IsNaN(_ground[pick]) ? anchorY : _ground[pick];
        var (stream, pitch, jitter) = (moo ? CowSounds.Moo : DesalpeSounds.Treichel(DesalpeRules.BellSize(pick))).Pick(_rng);
        var v = _voices[_voice++ % _voices.Length];
        v.GlobalPosition = origin.ToWorld(_site!.E + _x[pick], _site.N + _y[pick], ground + 1.1f);
        v.Stream = stream;
        v.PitchScale = pitch;
        v.VolumeDb = (moo ? -4f : _gait[pick] > 0.3f ? -9f : -14f) + jitter;
        v.Play();
    }

    /// <summary>The herder's call, from where the player stands (the drive starting).</summary>
    public void Call(Vector3 at)
    {
        if (_voices.Length == 0) return;
        var (stream, pitch, jitter) = DesalpeSounds.HerderCall.Pick(_rng);
        var v = _voices[_voice++ % _voices.Length];
        v.GlobalPosition = at;
        v.Stream = stream;
        v.PitchScale = pitch;
        v.VolumeDb = -4f + jitter;
        v.Play();
    }
}
