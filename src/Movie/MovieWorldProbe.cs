using Godot;
using UnitSport.Core;

namespace UnitSport.Movie;

/// <summary>
/// <c>--movieworldcheck --world flat</c> (#775): the recorded world. A watched container holds a car
/// that drives a circle with a wheel turning, a parked car, a bird that hides half way and a crate
/// that is freed; a <see cref="WorldRecorder"/> records them for four seconds while the origin
/// shifts once. The grab goes onto a <see cref="MovieStage"/>, whose ghosts must stand where the
/// objects really stood (kept here as the truth) at times sought forwards and backwards, hidden
/// and gone where they were. Then the project is saved and loaded back through the real look store,
/// and the loaded ghosts must stand in the same places wearing the same mesh. An instancer (#782)
/// stands for the dormant fleet: one of its three boxes undrawn in place at 1 s (a hall car woken),
/// then the whole replaced by one of two boxes at 3 s (a car park redrawn after a wake).
/// </summary>
public partial class MovieWorldProbe : Node
{
    public static bool Requested() => CmdArgs.Has("--movieworldcheck");

    private const double RunSeconds = 4;
    private readonly WorldOrigin _origin;
    private readonly WorldRecorder _recorder = new(300);
    private Node3D? _watched, _car, _wheel, _parked, _bird, _crate;
    private OmniLight3D? _lamp;
    private MultiMeshInstance3D? _fleet;
    private bool _woke, _redrawn;
    private readonly Dictionary<(string, int), ulong> _fleetCopies = new();
    private static readonly Vector3 FleetAt = new(-25, 0, 10);   // from the spawn point, metres east, up, north
    private readonly List<(double T, GlobalPos Car, float Wheel)> _truth = new();
    private double _t0 = double.NaN, _next;
    private bool _shifted, _done;
    private int _failures;

    public MovieWorldProbe(WorldOrigin origin) => _origin = origin;

    private static MeshInstance3D Box(Vector3 size, Color color) => new()
    {
        Mesh = new BoxMesh { Size = size },
        MaterialOverride = new StandardMaterial3D { AlbedoColor = color },
    };

    public override void _Ready()
    {
        _watched = new Node3D { Name = "Watched" };
        AddChild(_watched);
        ReplayWatch.Container(_watched);
        var (e, n) = SpawnPoint.ParseTarget();
        _car = new Node3D { Name = "Car" };
        _car.AddChild(Box(new Vector3(1.8f, 1.2f, 4f), Colors.Red));
        _wheel = Box(new Vector3(0.3f, 0.6f, 0.6f), Colors.Black);
        _wheel.Position = new Vector3(1, -0.4f, 1.3f);
        _car.AddChild(_wheel);
        // a lamp that dims at 2 s: a light is recorded as a copy of itself, a new copy when it changes
        _lamp = new OmniLight3D { LightEnergy = 2f, OmniRange = 12f, Position = new Vector3(0, 0.3f, -2.2f) };
        _car.AddChild(_lamp);
        _parked = new Node3D { Name = "Parked" };
        _parked.AddChild(Box(new Vector3(1.8f, 1.2f, 4f), Colors.Blue));
        _bird = new Node3D { Name = "Bird" };
        _bird.AddChild(Box(new Vector3(0.3f, 0.2f, 0.4f), Colors.White));
        _crate = new Node3D { Name = "Crate" };
        _crate.AddChild(Box(Vector3.One, Colors.Brown));
        foreach (var o in new[] { _car, _parked, _bird, _crate }) _watched.AddChild(o);
        _parked.GlobalPosition = _origin.ToWorld(e + 20, n, 500);
        _bird.GlobalPosition = _origin.ToWorld(e - 10, n, 530);
        _crate.GlobalPosition = _origin.ToWorld(e, n + 15, 500);
        _fleet = Fleet(3);
    }

    /// <summary>A car park of <paramref name="count"/> boxes 3 m apart, drawn by one instancer that announces its changes.</summary>
    private MultiMeshInstance3D Fleet(int count)
    {
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new BoxMesh { Size = new Vector3(1.8f, 1.2f, 4f) }, InstanceCount = count };
        for (int i = 0; i < count; i++) mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(3 * i, 0, 0)));
        var node = new MultiMeshInstance3D { Name = "Fleet", Multimesh = mm };
        ReplayWatch.Instanced(node);
        AddChild(node);
        var (e, n) = SpawnPoint.ParseTarget();
        node.GlobalPosition = _origin.ToWorld(e + FleetAt.X, n + FleetAt.Z, 500 + FleetAt.Y);
        return node;
    }

    private GlobalPos CarAt(double t)
    {
        var (e, n) = SpawnPoint.ParseTarget();
        double a = t * 0.8;
        return new GlobalPos(e + 30 * Math.Cos(a), n + 30 * Math.Sin(a), 500 + t);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done || _car == null) return;
        double now = GameClock.Now;
        if (double.IsNaN(_t0)) _t0 = now;
        double t = now - _t0;
        if (t > 30) { Fail("TIMEOUT"); Finish(); return; }
        if (t <= RunSeconds)
        {
            var p = CarAt(t);
            _car.GlobalPosition = _origin.ToWorld(p);
            _car.Rotation = new Vector3(0, (float)(-t * 0.8), 0);
            float wheel = (float)(t * 6);
            _wheel!.Rotation = new Vector3(wheel, 0, 0);
            if (t >= 1.5 && _bird!.Visible) _bird.Visible = false;
            if (t >= 2.0) _lamp!.LightEnergy = 0.5f;
            if (t >= 2.5 && IsInstanceValid(_crate)) { _crate!.Free(); _crate = null; }
            if (t >= 1.0 && !_woke)
            {
                // a hall car woken: undrawn in place, and the recorder told
                _woke = true;
                _fleet!.Multimesh.SetInstanceTransform(1, new Transform3D(Basis.Identity.Scaled(Vector3.Zero), new Vector3(3, 0, 0)));
                ReplayWatch.Instanced(_fleet);
            }
            if (t >= 3.0 && !_redrawn)
            {
                // a car park redrawn after a wake: a new instancer in place of the old
                _redrawn = true;
                _fleet!.Free();
                _fleet = Fleet(2);
            }
            if (t >= 2.0 && !_shifted)
            {
                // an origin shift moves every node; the recording is in LV95 and must not notice
                _shifted = true;
                var (e, n) = SpawnPoint.ParseTarget();
                var shift = _origin.MoveTo(e + 500, n + 500);
                foreach (var o in new Node3D[] { _car, _parked, _bird, _fleet! }) o.GlobalTransform = shift.Transform * o.GlobalTransform;
            }
            if (now + 1e-6 >= _next)
            {
                _next = now + 1.0 / Channels.Rate;
                _recorder.Tick(now, _origin);
                _truth.Add((t, _origin.ToGlobal(_car.GlobalPosition), wheel));
            }
            return;
        }
        Play(now);
    }

    private void Play(double now)
    {
        var track = _recorder.Ring.Slice(double.NegativeInfinity, now, _recorder.Looks, out _);
        if (track == null) { Fail("nothing recorded"); Finish(); return; }
        int moves = track.Entities.Sum(e => e.Times.Length);
        GD.Print($"[movieworldcheck] recorded {track.Entities.Length} objects and parts, {moves} poses, "
            + $"{track.Looks.Length} looks, {track.Duration:F2} s");
        if (track.Entities.Length != 14) Fail($"expected 6 objects with 8 parts (the car has a wheel and a lamp, each instancer is its own part), got {track.Entities.Length}");
        // the parked car and its body never moved: one pose each
        var still = track.Entities.Count(e => e.Times.Length == 1);
        if (still < 4) Fail($"objects that never moved should hold one pose, only {still} do");

        var project = new MovieProject(ActorIo.Names);
        project.AddWorld(track, 0);
        ReplayWatch.HideLive();
        var stage = new MovieStage(project, _origin);
        AddChild(stage);
        Seeks(stage, track, "recorded");

        // through the file and the look store and back
        var bytes = new MemoryStream();
        int stored = WorldLookStore.Store(project);
        MovieFile.Write(project, bytes);
        bytes.Position = 0;
        var loaded = MovieFile.Read(bytes, ActorIo.Names);
        int missing = WorldLookStore.Resolve(loaded);
        GD.Print($"[movieworldcheck] saved {stored} looks, file {bytes.Length / 1024} KB, {missing} missing on load");
        if (missing > 0 || stored != track.Looks.Length) Fail("the looks did not survive the file");
        stage.Use(loaded);
        Seeks(stage, loaded.Worlds[0], "loaded");
        var body = (MeshInstance3D?)stage.Ghosts.GetChildren().OfType<Node3D>().SelectMany(g => g.GetChildren()).OfType<MeshInstance3D>()
            .FirstOrDefault(m => m.Visible && m.Mesh is BoxMesh { Size.Z: 4f });
        if (body == null) Fail("no loaded ghost wears the car's box");
        foreach (var look in loaded.Worlds[0].Looks)
            if (look.Asset != null) DirAccess.RemoveAbsolute($"{WorldLookStore.Folder}/{look.Asset}");
        ReplayWatch.ShowLive();
        Finish();
    }

    private void Seeks(MovieStage stage, WorldTrack track, string what)
    {
        var random = new Random(775);
        var times = new List<double> { 0.2, 3.9, 1.0, 2.2, 0.6, 3.0 };
        for (int k = 0; k < 6; k++) times.Add(0.1 + random.NextDouble() * 3.8);
        float worst = 0, worstWheel = 0;
        foreach (double t in times)
        {
            stage.Seek(t);
            var truth = _truth.MinBy(x => Math.Abs(x.T - t));
            // the car: the group ghost with a moving pose; the truth between ticks is the nearer tick
            var carGhost = Ghost(stage, track, 0);
            if (carGhost == null || !carGhost.Visible) { Fail($"{what} t {t:F2}: the car is not drawn"); continue; }
            var want = _origin.ToWorld(CarAt(t));
            worst = Math.Max(worst, carGhost.GlobalPosition.DistanceTo(want));
            var wheel = carGhost.GetChildren().OfType<MeshInstance3D>().FirstOrDefault(m => m.Mesh is BoxMesh { Size.X: 0.3f });
            if (wheel != null) worstWheel = Math.Max(worstWheel, Math.Abs(Mathf.AngleDifference(wheel.Rotation.X, (float)(t * 6))));
            // the lamp: an OmniLight3D on the car's ghost, bright before 2 s and dim after
            var lamp = carGhost.GetChildren().OfType<OmniLight3D>().FirstOrDefault(l => l.Visible);
            float wantEnergy = t < 2.0 ? 2f : 0.5f;
            if (Math.Abs(t - 2.0) > 0.6 && (lamp == null || !Mathf.IsEqualApprox(lamp.LightEnergy, wantEnergy)))
                Fail($"{what} t {t:F2}: the lamp is {(lamp == null ? "missing" : $"at {lamp.LightEnergy:F1}")}, expected {wantEnergy:F1}");
            bool birdShown = Ghost(stage, track, 2) is { Visible: true } bird && bird.GetChildren().OfType<Node3D>().Any(c => c.Visible);
            bool crateShown = Ghost(stage, track, 3) is { Visible: true };
            bool birdRight = birdShown == t < 1.5 - 0.05 || Math.Abs(t - 1.5) < 0.05;
            bool crateRight = crateShown == t < 2.5 - 0.05 || Math.Abs(t - 2.5) < 0.05;
            if (!birdRight) Fail($"{what} t {t:F2}: the bird is {(birdShown ? "shown" : "hidden")}");
            Fleet(stage, t, what);
            if (!crateRight) Fail($"{what} t {t:F2}: the crate is {(crateShown ? "there" : "gone")}");
        }
        // 0.8 rad/s on a 30 m circle is 24 m/s: a tick is 0.8 m, so between ticks the chord error stays tiny
        bool ok = worst < 0.05f && worstWheel < 0.02f;
        GD.Print($"[movieworldcheck] {what}: {times.Count} seeks, the car at worst {worst:F3} m off, the wheel {worstWheel:F3} rad "
            + $"{(ok ? "ok" : "WRONG")}");
        if (!ok) Fail($"{what}: the ghosts are not where the objects were");
    }

    /// <summary>
    /// The instancer's ghost (#782): one drawn, where the fleet stood, with three boxes before 1 s, the
    /// middle one undrawn after, and two after 3 s. A change is seen on the recorder's next walk, half a
    /// second at most, so times that close to one are not judged. Headless, the renderer keeps no
    /// instance transforms: there the copy shown after 1 s must only be another one than before.
    /// </summary>
    private void Fleet(MovieStage stage, double t, string what)
    {
        if (Math.Abs(t - 1.0) < 0.6 || Math.Abs(t - 3.0) < 0.6) return;
        var drawn = stage.Ghosts.GetChildren().OfType<Node3D>().Where(g => g.Visible && !g.IsQueuedForDeletion())
            .SelectMany(g => g.GetChildren()).OfType<MultiMeshInstance3D>().Where(m => m.Visible).ToList();
        if (drawn.Count != 1) { Fail($"{what} t {t:F2}: {drawn.Count} instancers drawn, expected 1"); return; }
        var mm = drawn[0].Multimesh;
        int count = t < 3 ? 3 : 2, phase = t < 1 ? 0 : t < 3 ? 1 : 2;
        if (mm.InstanceCount != count) Fail($"{what} t {t:F2}: the fleet has {mm.InstanceCount} boxes, expected {count}");
        if (DisplayServer.GetName() != "headless")
        {
            bool middle = mm.InstanceCount > 1 && mm.GetInstanceTransform(1).Basis.Determinant() > 0.5f;
            if (middle != (phase != 1)) Fail($"{what} t {t:F2}: the middle box is {(middle ? "drawn" : "undrawn")}");
        }
        // the ghost's copy of the instances: the same within a phase, another one in the next
        var key = (what, phase);
        ulong copy = mm.GetInstanceId();
        if (_fleetCopies.TryGetValue(key, out ulong seen) && seen != copy) Fail($"{what} t {t:F2}: the fleet's look changed within a phase");
        _fleetCopies[key] = copy;
        if (_fleetCopies.TryGetValue((what, phase ^ 1), out ulong other) && phase < 2 && other == copy)
            Fail($"{what} t {t:F2}: the fleet's look did not change when a box was undrawn");
        var (e, n) = SpawnPoint.ParseTarget();
        float off = drawn[0].GlobalPosition.DistanceTo(_origin.ToWorld(e + FleetAt.X, n + FleetAt.Z, 500 + FleetAt.Y));
        if (off > 0.05f) Fail($"{what} t {t:F2}: the fleet is drawn {off:F2} m from where it stood");
    }

    /// <summary>The ghost of the <paramref name="n"/>th recorded group (in the order they were made).</summary>
    private static Node3D? Ghost(MovieStage stage, WorldTrack track, int n)
    {
        int seen = -1;
        for (int i = 0; i < track.Entities.Length; i++)
            if (track.Entities[i].IsGroup && ++seen == n) return stage.Ghosts.NodeOf(i);
        return null;
    }

    private void Fail(string why)
    {
        _failures++;
        GD.Print($"[movieworldcheck] FAIL: {why}");
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        GD.Print(_failures == 0 ? "[movieworldcheck] RESULT: ok" : $"[movieworldcheck] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
