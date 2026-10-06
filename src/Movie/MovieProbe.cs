using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Movie;

/// <summary>
/// <c>--moviecheck --world flat</c> (#638): a player walks, takes a plane and climbs while a replay
/// ring shorter than the run records it (so it wraps). The ring becomes a clip; a puppet on a
/// <see cref="MovieStage"/> must then stand exactly where the recording says, on the right ride,
/// at times sought forwards and backwards, and be gone where a cut left the lane empty. Also fails
/// if the recorder's property list drifts from what <see cref="FootPlayer"/> replicates. Then the
/// sound (#656): a 120 BPM click track goes through the real import (copy, Godot's decoder, beat
/// detection) and onto the timeline, where it must sound playing forwards and fall silent backwards.
/// </summary>
public partial class MovieProbe : Node
{
    public static bool Requested() => CmdArgs.Has("--moviecheck");

    private const double RunSeconds = 16, BufferSeconds = 15, MountAt = 3;
    private readonly WorldOrigin _origin;
    private readonly ActorState _state = new(ActorIo.Names.Length);
    private readonly ReplayRing _ring = new((int)(BufferSeconds * Channels.Rate), ActorIo.Names.Length);
    private FootPlayer? _owner;
    private MovieProject? _project;
    private MovieStage? _stage;
    private double _t, _next, _wait;
    private bool _mounted, _done, _reloaded;
    private int _soundPhase, _soundFrames;
    private SoundImport? _import;
    private Clip? _song;
    private string? _clicksPath, _songFile;
    private int _seek = -1, _settle, _failures;
    private readonly List<(double T, string Why)> _seeks = new();

    public MovieProbe(WorldOrigin origin) => _origin = origin;

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _wait += delta;
        if (_wait > 120) { Fail("TIMEOUT"); Finish(); return; }
        if (_owner == null)
        {
            var (e, n) = SpawnPoint.ParseTarget();
            var at = _origin.ToWorld(e, n, 0);
            if (!TestWorld.TryGround(null, at, out float g)) return;
            _owner = new FootPlayer { Name = "Owner" };
            AddChild(_owner);
            _owner.GlobalPosition = new Vector3(at.X, g + 1f, at.Z);
            _owner.DebugLaunch(_owner.GlobalPosition, Vector3.Zero);
            string drift = ActorIo.Drift();
            if (drift.Length > 0) Fail($"the recorder's properties differ from the replicated ones: {drift}");
            return;
        }
        if (_project != null) return;
        if (!_owner.IsOnFloor() && _t == 0) return;   // settle first

        _t += delta;
        Input.ActionPress(PlayerInput.MoveForward);
        if (_t >= MountAt && !_mounted)
        {
            _mounted = true;
            bool ok = _owner.SetRide(RideKind.Plane);
            if (!ok) { _owner.DebugLaunch(_owner.GlobalPosition, Vector3.Zero); ok = _owner.SetRide(RideKind.Plane); }
            if (!ok) Fail("could not mount the plane");
            _owner.DebugLaunch(_owner.GlobalPosition + Vector3.Up * 300f, new Vector3(0, 0, -50));
            Input.ActionPress(PlayerInput.FlyUp);
        }

        // the recorder's own rate and clock
        double now = GameClock.Now;
        if (now + 1e-6 >= _next)
        {
            _next = now + 1.0 / Channels.Rate;
            ActorIo.Read(_owner, _state);
            _ring.Append(now, _state);
        }
        if (_t >= RunSeconds) Cut();
    }

    private void Cut()
    {
        Input.ActionRelease(PlayerInput.MoveForward);
        Input.ActionRelease(PlayerInput.FlyUp);
        bool wrapped = _ring.Count == _ring.Capacity;
        var track = _ring.Slice(double.NegativeInfinity, out _);
        if (track == null) { Fail("nothing recorded"); Finish(); return; }
        GD.Print($"[moviecheck] recorded {track.Frames} frames, {track.Duration:F2} s (ring of {_ring.Capacity}, wrapped {wrapped}), "
            + $"{track.Events.Length} changes, starting {(RideKind)track.BaseNum[ActorIo.RideKind]}");
        if (!wrapped) Fail("the ring never wrapped");
        if ((RideKind)track.BaseNum[ActorIo.RideKind] != RideKind.OnFoot) Fail("the buffer should start on foot");
        if (!track.Events.Any(e => e.Prop == ActorIo.RideKind && e.Num == (long)RideKind.Plane)) Fail("no change to the plane recorded");

        _project = new MovieProject(ActorIo.Names);
        _project.AddTrack(_project.LaneFor("Owner", "Pilot", 1), track, 0);
        // the owner leaves: from here on only the puppet is drawn
        _owner!.QueueFree();
        _owner = null;
        _stage = new MovieStage(_project, _origin);
        AddChild(_stage);

        double end = track.Duration, mount = track.Events.First(e => e.Prop == ActorIo.RideKind).T;
        _seeks.Add((end, "the end"));
        _seeks.Add((0.5, "back to the walk"));
        _seeks.Add((mount + 1.0, "forward into the flight"));
        _seeks.Add((mount - 0.5, "back to just before the plane"));
        var random = new Random(638);
        for (int k = 0; k < 6; k++) _seeks.Add((random.NextDouble() * end, "anywhere"));
        _seek = 0;
        _stage.Seek(_seeks[0].T);
    }

    public override void _Process(double delta)
    {
        if (_done || _stage == null || _project == null || _seek < 0) return;
        if (_soundPhase > 0) { SoundStep(); return; }
        // two frames: the puppet takes its ride (a new visual) on the first
        if (++_settle < 3) return;
        _settle = 0;

        if (_seek < _seeks.Count)
        {
            var (t, why) = _seeks[_seek];
            Check(t, why);
            if (++_seek < _seeks.Count) _stage.Seek(_seeks[_seek].T);
            else
            {
                // a cut: the second half moved away leaves a hole in the lane, where nobody is drawn
                var clip = _project.Clips[0];
                var right = _project.Split(clip.Id, 8)!;
                _project.Move(right.Id, 30);
                _stage.Seek(12);
            }
            return;
        }

        if (!_reloaded)
        {
            var puppet = _stage.Puppet(0);
            bool hidden = puppet == null && GetNodeOrNull<FootPlayer>($"MovieStage/{MovieStage.PuppetBase}") is { Visible: false };
            GD.Print($"[moviecheck] in the hole a cut left: puppet {(hidden ? "hidden" : "STILL DRAWN")}");
            if (!hidden) Fail("a lane with no clip still draws its puppet");
            // the project again (Open, New, Save do this): the new puppet must get the old one's name,
            // which a passenger finds its host by
            _reloaded = true;
            _stage.Use(_project);
            _stage.Seek(5);
            return;
        }
        bool named = GetNodeOrNull<FootPlayer>($"MovieStage/{MovieStage.PuppetBase}") is { Visible: true } p && p == _stage.Puppet(0);
        GD.Print($"[moviecheck] after reloading the project: puppet {(named ? $"back as {MovieStage.PuppetBase}" : "MISSING or renamed")}");
        if (!named) Fail("a reloaded project's puppet lost its name");
        _soundPhase = 1;
    }

    /// <summary>The sound part, a step per frame (#656).</summary>
    private void SoundStep()
    {
        switch (_soundPhase)
        {
            case 1:
            {
                // 8 s of clicks at 120 BPM, from 0.25 s
                const int rate = 22050;
                var s = new short[rate * 8];
                var random = new Random(656);
                for (double t = 0.25; t < 8; t += 0.5)
                {
                    int at = (int)(t * rate);
                    for (int k = 0; k < rate / 40 && at + k < s.Length; k++)
                        s[at + k] = (short)Math.Clamp((random.NextDouble() - 0.5) * 1.6 * Math.Exp(-k / (rate / 200.0)) * 32767, -32768, 32767);
                }
                _clicksPath = ProjectSettings.GlobalizePath("user://moviecheck_clicks.wav");
                using (var f = System.IO.File.Create(_clicksPath)) Pcm.WriteWav(f, s, rate);
                _import = SoundImport.Start(_clicksPath, out var error);
                if (_import == null) { Fail($"the click track could not be imported: {error}"); Finish(); return; }
                _soundPhase = 2;
                return;
            }
            case 2:
            {
                if (!_import!.Step())
                {
                    if (++_soundFrames > 900) { Fail("the import never finished"); Finish(); }
                    return;
                }
                var asset = _import.Result();
                _songFile = asset.File;
                int ghosts;
                _song = _project!.AddAudio(_project.AudioLaneFor("Music"), asset, 1.0);
                ghosts = _project.Beats().Count;
                bool beat = asset.Beat.Bpm is > 118 and < 122 && asset.Beat.Beats.Length >= 14 && ghosts == asset.Beat.Beats.Length;
                GD.Print($"[moviecheck] click track imported: {asset.Duration:F2} s, {asset.Beat.Bpm:F1} BPM (120), "
                    + $"{asset.Beat.Beats.Length} beats (16), {ghosts} ghost markers, waveform {asset.Peaks.Length} peaks {(beat ? "ok" : "WRONG")}");
                if (!beat) Fail("the click track's beat was not found");
                if (asset.Peaks.Length < 350) Fail("no waveform");
                _stage!.Seek(2);
                _stage.Speed = 1;
                _stage.Playing = true;
                _soundFrames = 0;
                _soundPhase = 3;
                return;
            }
            case 3:
                if (++_soundFrames < 10) return;
                bool forward = _stage!.Deck.Sounding(_song!.Id);
                GD.Print($"[moviecheck] playing forward at {_stage.Time:F2} s: the song {(forward ? "sounds" : "is SILENT")}");
                if (!forward) Fail("the song does not sound while the movie plays");
                _stage.Speed = -1;   // backwards: silent
                _soundFrames = 0;
                _soundPhase = 4;
                return;
            case 4:
                if (++_soundFrames < 5) return;
                bool back = _stage!.Deck.Sounding(_song!.Id);
                GD.Print($"[moviecheck] playing backwards: the song {(back ? "STILL SOUNDS" : "is silent")}");
                if (back) Fail("the song sounds while the movie plays backwards");
                try
                {
                    if (_clicksPath != null) System.IO.File.Delete(_clicksPath);
                    if (_songFile != null) System.IO.File.Delete(SoundImport.PathOf(_songFile));
                }
                catch (System.IO.IOException) { }
                Finish();
                return;
        }
    }

    private void Check(double t, string why)
    {
        var puppet = _stage!.Puppet(0);
        var track = _project!.Tracks[0];
        track.Sample(t, _state);
        var want = _origin.ToWorld(new GlobalPos(_state.E, _state.N, _state.Alt));
        var kind = (RideKind)_state.Num[ActorIo.RideKind];
        if (puppet == null) { Fail($"t {t:F2} ({why}): no puppet"); return; }
        float off = puppet.GlobalPosition.DistanceTo(want);
        bool same = off < 0.01f && puppet.Ride == kind && puppet.Visible;
        GD.Print($"[moviecheck] t {t,6:F2} s {why,-30} {kind,-7} puppet {puppet.Ride,-7} off by {off:F4} m {(same ? "ok" : "WRONG")}");
        if (!same) Fail($"t {t:F2}: puppet {puppet.Ride} {off:F3} m away, expected {kind}");
    }

    private void Fail(string why)
    {
        _failures++;
        GD.Print($"[moviecheck] FAIL: {why}");
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        Input.ActionRelease(PlayerInput.MoveForward);
        Input.ActionRelease(PlayerInput.FlyUp);
        GD.Print(_failures == 0 ? "[moviecheck] RESULT: ok" : $"[moviecheck] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
