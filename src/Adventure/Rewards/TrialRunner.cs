using Godot;
using UnitSport.Core;
using UnitSport.Gpx;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The trial this client runs (#1227, rewards plan 6): its gates in the world (the next two shown, a run's flat on
/// the ground, a glide's upright across the course), each reported when the body passes it (the server times and
/// checks); the run sampled at 4 Hz (<see cref="GhostTrack"/>) and uploaded after a personal best; the ghost to
/// race (one's own best, else the leader's) played as a <see cref="Runner"/>, without collision. A plain child of
/// the world.
/// </summary>
public partial class TrialRunner : Node3D
{
    private readonly Func<FootPlayer?> _me;
    private readonly Func<WorldOrigin?> _origin;
    private readonly Func<ChunkManager?> _chunks;
    private readonly List<MeshInstance3D> _gates = new();
    private readonly List<GhostTrack.Sample> _samples = new();
    private Runner? _ghost;
    private double _ghostStart, _nextSample, _sentAt;
    private int _sent = -1;
    private bool _glide;

    public static TrialRunner? Instance { get; private set; }

    /// <summary>Gates reported (checks).</summary>
    public int Reported { get; private set; }
    /// <summary>A ghost runs beside us (checks).</summary>
    public bool HasGhost => _ghost != null;

    public TrialRunner(Func<FootPlayer?> me, Func<WorldOrigin?> origin, Func<ChunkManager?> chunks)
    {
        _me = me;
        _origin = origin;
        _chunks = chunks;
        Name = "TrialRunner";
    }

    public TrialRunner() : this(() => null, () => null, () => null) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (RewardService.Instance is not { } r) return;
        r.TrialStarted -= OnStarted;
        r.TrialGate -= OnGate;
        r.TrialEnded -= OnEnded;
        r.GhostReceived -= OnGhost;
    }

    public override void _Ready()
    {
        if (RewardService.Instance is not { } r) return;
        r.TrialStarted += OnStarted;
        r.TrialGate += OnGate;
        r.TrialEnded += OnEnded;
        r.GhostReceived += OnGhost;
    }

    private void OnStarted(ActiveTrial t)
    {
        Clear();
        _samples.Clear();
        _sent = -1;
        if (_origin() is not { } origin) return;
        var material = RewardMeshes.GlowMaterial();
        bool glide = _glide = Trials.Resolve(t.Id, Places.PoiCatalogueHost.Current)?.Kind == TrialKind.Glide;
        for (int i = 0; i < t.Gates.Length; i++)
        {
            var g = t.Gates[i];
            var at = origin.ToWorld(g.E, g.N, g.Alt) - GlobalPosition;
            var prev = i == 0 ? (Trials.Resolve(t.Id, Places.PoiCatalogueHost.Current) is { } d ? (d.StartE, d.StartN) : (g.E, g.N)) : (t.Gates[i - 1].E, t.Gates[i - 1].N);
            float yaw = (float)Math.Atan2(-(g.E - prev.Item1), (g.N - prev.Item2));
            var gate = new MeshInstance3D
            {
                Name = $"Gate{i}", Mesh = RewardMeshes.Gate(), MaterialOverride = material, Position = at + new Vector3(0, glide ? 0 : 0.3f, 0),
                // a run's gate lies on the ground as a ring to cross; a glide's stands across the course
                Rotation = glide ? new Vector3(0, yaw, 0) : new Vector3(Mathf.Pi / 2, 0, 0),
                Scale = Vector3.One * g.Radius, Visible = i < 2,
            };
            AddChild(gate);
            _gates.Add(gate);
        }
        RewardService.Instance?.AskGhost(t.Id);
    }

    private void OnGate(string id, int gate)
    {
        for (int i = 0; i < _gates.Count; i++) _gates[i].Visible = i > gate && i <= gate + 2;
    }

    private void OnEnded(string id, double seconds, int medal, bool pb, int rank)
    {
        Clear();
        if (pb && _samples.Count > 4) RewardService.Instance?.UploadGhost(id, GhostTrack.Encode(_samples));
        _samples.Clear();
    }

    private void OnGhost(string id, List<GhostTrack.Sample> samples)
    {
        if (RewardService.Instance?.Trial?.Id != id || _chunks() is not { } chunks || _origin() is not { } origin || samples.Count < 2) return;
        var points = new List<TrackPoint>(samples.Count);
        double dist = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (i > 0) dist += Math.Sqrt((samples[i].E - samples[i - 1].E) * (samples[i].E - samples[i - 1].E) + (samples[i].N - samples[i - 1].N) * (samples[i].N - samples[i - 1].N));
            points.Add(new TrackPoint(samples[i].E, samples[i].N, samples[i].Alt, i / GhostTrack.Rate, dist));
        }
        var track = new GpxTrack { Name = "Ghost", Points = points, HasTiming = true, ElevationIsSurface = true };
        _ghost?.QueueFree();
        _ghost = Runner.Create(track, chunks, origin, 1);
        GetParent().AddChild(_ghost);
        _ghostStart = RewardService.Instance!.Trial!.Since;
        GD.Print($"[rewards] racing a ghost of {id} ({samples.Count} samples)");
    }

    private void Clear()
    {
        foreach (var g in _gates) g.QueueFree();
        _gates.Clear();
        _ghost?.QueueFree();
        _ghost = null;
    }

    public override void _Process(double delta)
    {
        if (RewardService.Instance?.Trial is not { } t || _me() is not { } me || !IsInstanceValid(me)) return;
        double now = Net.ClockSync.ServerNow;
        var at = me.Global;
        if (now >= _nextSample && _samples.Count * 16 < GhostTrack.MaxBytes - 64)
        {
            _nextSample = now + 1 / GhostTrack.Rate;
            _samples.Add(new GhostTrack.Sample(at.E, at.N, (float)at.Alt, me.GlobalRotation.Y));
        }
        _ghost?.UpdateTo(now - _ghostStart, 1, delta);
        // a gate the server did not count (the body's published position lagged) is reported again a second later
        if (t.Next >= t.Gates.Length || t.Next == _sent && now - _sentAt < 1.0) return;
        var g = t.Gates[t.Next];
        double de = at.E - g.E, dn = at.N - g.N, dz = at.Alt - g.Alt;
        double d2 = de * de + dn * dn + (_glide ? dz * dz : 0);
        if (d2 > g.Radius * g.Radius || !_glide && Math.Abs(dz) > 15) return;
        _sent = t.Next;
        _sentAt = now;
        Reported++;
        RewardService.Instance.ReportGate(t.Id, t.Next);
    }
}
