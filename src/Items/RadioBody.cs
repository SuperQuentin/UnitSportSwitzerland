using Godot;
using UnitSport.Audio;
using UnitSport.Audio.Cd;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Items;

/// <summary>
/// A radio lying in the world: thrown by a player, tumbling to rest on their machine (client
/// authority, as vehicles are), then frozen where it landed on every peer; playing a CD from the
/// shared library for whoever is near.
///
/// <para>
/// Two things replicate, by two synchronizers with different owners. <c>Sync</c> (the thrower)
/// carries the fall: position, rotation, and <see cref="Settled"/> once it stops. <c>State</c>
/// (the server) carries what plays: <see cref="CdId"/>, <see cref="StartedAt"/>,
/// <see cref="Playing"/> — set by the server on request, so two players cannot both win the
/// remote. A peer that joins later gets both with the spawn.
/// </para>
///
/// <para>
/// Nobody streams audio to anyone: the <see cref="RadioSpeaker"/> fetches the Ogg once and plays
/// it from <c>ServerNow − StartedAt</c> on the shared clock. The beat the dancers follow (<see cref="BeatAt"/>) is computed
/// from that same clock and never from the playback position, so a client still downloading the
/// CD dances in time with everyone else, in silence, until it arrives.
/// </para>
/// </summary>
public partial class RadioBody : RigidBody3D, IOriginShiftAware
{
    public const string Group = "radios";

    [Export] public int CdId { get; set; }
    [Export] public double StartedAt { get; set; }
    [Export] public bool Playing { get; set; }
    [Export] public bool Settled { get; set; }

    /// <summary>Seconds the CD lasts, set by the server with the CD (it may not know a personal CD).</summary>
    [Export] public float Length { get; set; }

    /// <summary>Peer that simulated the fall; 0 for the server.</summary>
    public long Owner { get; private set; }

    /// <summary>Seconds with no player within range — for despawning.</summary>
    public double LonelyFor { get; set; }

    /// <summary>What the speaker is at right now, seconds into the CD, or NaN while silent. For the probes.</summary>
    public double HeardPosition => _speaker?.HeardPosition ?? double.NaN;

    /// <summary>The speaker, on peers that have one (not the dedicated server, not headless).</summary>
    public RadioSpeaker? Speaker => _speaker;

    /// <summary>Where the clock says the CD is, seconds, or NaN when nothing plays.</summary>
    public double WantedPosition => Playing ? ClockSync.ServerNow - StartedAt : double.NaN;

    /// <summary>The CD in the tray, when this peer knows it (a personal CD only its owner does).</summary>
    public CdInfo? Cd => CdLibrary.Instance?.Find(CdId);

    private const float BodyW = 0.46f, BodyH = 0.22f, BodyD = 0.16f;
    private const double SettleAfter = 8, RestFor = 1;

    private RadioState _initial;
    private WorldOrigin _origin = null!;
    /// <summary>The position on the wire (#185): published by whoever throws it, applied everywhere else.</summary>
    private NetPlace _place = null!;
    private MultiplayerSynchronizer? _sync;
    private double _age, _restTime;
    private RadioSpeaker? _speaker;
    private Vector3 _lastPos, _lastVel;
    private bool _bonked;

    public static RadioBody Create(RadioState state, WorldOrigin origin)
    {
        var r = new RadioBody
        {
            Name = string.IsNullOrEmpty(state.Name) ? $"radio_local_{Interlocked.Increment(ref _localCounter)}" : state.Name,
            _initial = state,
            _origin = origin,
            Owner = state.Owner,
            CdId = state.CdId,
            StartedAt = state.StartedAt,
            Playing = state.Playing,
            Settled = state.Settled,
            Length = state.Length,
        };
        r.SetMultiplayerAuthority(state.Owner > 0 ? (int)state.Owner : 1);
        return r;
    }

    private static int _localCounter;

    private static bool Headless => DisplayServer.GetName() == "headless";

    public override void _Ready()
    {
        AddToGroup(Group);
        CollisionMask |= World.TreeColliders.Layer;
        var s = _initial;
        Position = _origin.ToWorld(s.Position);
        AddChild(_place = new NetPlace(_origin, s.Position));
        Rotation = new Vector3(0, s.Yaw, 0);
        // a heavy boombox (#725): it lands with a thud and stays, rather than skittering off
        Mass = 7f;
        PhysicsMaterialOverride = new PhysicsMaterial { Bounce = 0.05f, Friction = 1f, Rough = true };
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(BodyW, BodyH, BodyD) } });

        // the fall: whoever threw it simulates, the others move the box where they are told
        var fall = new SceneReplicationConfig();
        foreach (var prop in NetPlace.Properties.Append(".:rotation").Append(".:Settled")) fall.AddProperty(prop);
        fall.PropertySetReplicationMode(".:Settled", SceneReplicationConfig.ReplicationMode.OnChange);
        _sync = new MultiplayerSynchronizer
        {
            Name = "Sync", RootPath = new NodePath(".."), ReplicationConfig = fall,
            ReplicationInterval = s.Settled ? 2f : 0.05f,
        };
        _sync.SetMultiplayerAuthority(GetMultiplayerAuthority());
        AddChild(_sync);

        // what plays: the server's word, reliably on change, and with the spawn for late joiners
        var play = new SceneReplicationConfig();
        foreach (var prop in new[] { ".:CdId", ".:StartedAt", ".:Playing", ".:Length", ".:Mode" })
        {
            play.AddProperty(prop);
            play.PropertySetReplicationMode(prop, SceneReplicationConfig.ReplicationMode.OnChange);
        }
        var state = new MultiplayerSynchronizer { Name = "State", RootPath = new NodePath(".."), ReplicationConfig = play };
        state.SetMultiplayerAuthority(1);
        AddChild(state);

        if (!IsMultiplayerAuthority() || s.Settled || NetworkManager.DedicatedServer)
        {
            // moved by the synchronizer, or standing exactly where it was put: no physics here
            FreezeMode = IsMultiplayerAuthority() ? FreezeModeEnum.Static : FreezeModeEnum.Kinematic;
            Freeze = true;
            if (IsMultiplayerAuthority()) Settled = true;
            SetPhysicsProcess(false);
        }
        else
        {
            LinearVelocity = s.Velocity;
            AngularVelocity = new Vector3(GD.Randf() * 3f - 1.5f, GD.Randf() * 1f - 0.5f, GD.Randf() * 3f - 1.5f);
            ContactMonitor = false;
            ContinuousCd = true;
            _lastPos = _origin.ToWorld(s.Position);
            _lastVel = s.Velocity;
        }

        if (!Headless && !NetworkManager.DedicatedServer)
        {
            AddChild(new MeshInstance3D { Name = "Visual", Mesh = Mesh(), MaterialOverride = ItemDefs.Material });
            _speaker = new RadioSpeaker { Name = "Speaker" };
            AddChild(_speaker);
            AddChild(new ImpactFx { Name = "Impact", Size = BodyW });
            AddChild(_sparkles = new RadioSparkles());
        }
    }

    /// <summary>The origin moved (#185): the last step of the flight, kept for hitting someone on the way, moves with it.</summary>
    public void OnOriginShifted(OriginShift shift)
    {
        _lastPos = shift.Point(_lastPos);
        _lastVel = shift.Direction(_lastVel);
    }

    /// <summary>Authority: the tumble, until it comes to rest.</summary>
    public override void _PhysicsProcess(double delta)
    {
        if (Settled) return;
        _age += delta;
        _place.Publish(Position);
        // a boombox thrown at someone hurts (#261)
        if (!_bonked && _age < 4) _bonked = ThrowHits.Step(this, _lastPos, GlobalPosition, _lastVel, ItemId.Radio);
        _lastPos = GlobalPosition;
        _lastVel = LinearVelocity;
        _restTime = LinearVelocity.LengthSquared() < 0.05f * 0.05f ? _restTime + delta : 0;
        if (Sleeping || _restTime > RestFor || _age > SettleAfter || Position.Y < Interiors.InteriorManager.LostBelowY)
        {
            Settled = true;
            Freeze = true;
            FreezeMode = FreezeModeEnum.Static;
            if (_sync != null) _sync.ReplicationInterval = 2f;
            SetPhysicsProcess(false);
        }
    }

    public override void _Process(double delta)
    {
        if (_speaker == null) return;
        _speaker.CdId = CdId;
        _speaker.StartedAt = StartedAt;
        _speaker.On = Playing;
        _speaker.Length = Length > 0 ? Length : Cd?.Duration ?? 0;

        // it bounces and sparkles to the music it is actually making (not while the CD is still downloading)
        var groove = _speaker.Playing && Playing ? RadioGroove.Of(CdId, StartedAt, ClockSync.ServerNow) : RadioGroove.Silent;
        bool beating = groove.Beating;
        _sparkles?.Step(_speaker.Playing, groove, (float)delta);
        _visual ??= GetNodeOrNull<MeshInstance3D>("Visual");
        if (_visual == null) return;
        var dance = beating ? Bounce(groove.Phase, groove.Beat, BodyH * 0.5f, groove.BounceScale) : Transform3D.Identity;
        // switched on or off with a tap (#725): one big squash and hop
        if (_poke >= 0f)
        {
            dance = Bounce(_poke / PokeTime, 0, BodyH * 0.5f, 1.6f) * dance;
            if ((_poke += (float)delta) > PokeTime) _poke = -1f;
        }
        _visual.Transform = dance;
    }

    private MeshInstance3D? _visual;
    private float _poke = -1f;
    private const float PokeTime = 0.4f;

    /// <summary>Its key was just pressed (#725): it jumps, here only (the music that follows is everyone's).</summary>
    public void Poke() => _poke = 0f;
    private RadioSparkles? _sparkles;

    /// <summary>The glints round it while it plays, on peers that draw it. For the probes.</summary>
    public RadioSparkles? Sparkles => _sparkles;

    /// <summary>
    /// A boombox's dance (#261), as a transform about its centre: squashed flat on the beat, then
    /// springing up off the floor and rocking toward the next beat's side — left on one beat, right
    /// on the next. <paramref name="half"/> is half its height (the squash is about its bottom face),
    /// <paramref name="amount"/> scales it all (a radio on someone's back bounces less).
    /// </summary>
    public static Transform3D Bounce(float phase, int beat, float half, float amount)
    {
        float kick = Mathf.Exp(-phase * 7f);                       // the hit, decaying through the beat
        float spring = Mathf.Sin(Mathf.Pi * Mathf.Clamp((phase - 0.08f) / 0.7f, 0f, 1f));
        float sy = 1f - 0.16f * kick * amount + 0.05f * spring * amount;
        float sxz = 1f + 0.09f * kick * amount - 0.02f * spring * amount;
        float hop = 0.03f * spring * spring * amount;
        float side = (beat & 1) == 0 ? 1f : -1f;
        var basis = new Basis(Vector3.Back, 0.07f * side * spring * amount) * Basis.FromScale(new Vector3(sxz, sy, sxz));
        // the bottom face stays down while squashed: lower the centre by what the height lost
        return new Transform3D(basis, new Vector3(0, -half * (1f - sy) + hop, 0));
    }

    /// <summary>The state to respawn it from: where it is now, what it plays.</summary>
    public RadioState Capture() => new(Name, Owner, _place.Global, Rotation.Y, Vector3.Zero, CdId, StartedAt, Playing, Settled, Length);

    /// <summary>What it plays, as the item carries it when picked up; null when silent or finished.</summary>
    public RadioPlay? NowPlaying => Playing && WantedPosition < Length ? new RadioPlay(CdId, StartedAt, Length) : null;

    /// <summary>
    /// The beat the CD is on, from the shared clock alone. False when nothing plays or the CD is
    /// unknown here. <paramref name="beatPhase"/> 0..1 within the beat, <paramref name="bar"/>
    /// counts four-beat bars from the first beat.
    /// </summary>
    public bool BeatAt(double serverNow, out float beatPhase, out int beatIndex, out int bar, out MusicStyle style)
    {
        beatPhase = 0; beatIndex = 0; bar = 0; style = MusicStyle.Pop;
        return Playing && BeatOf(CdId, StartedAt, serverNow, out beatPhase, out beatIndex, out bar, out style);
    }

    /// <summary>
    /// <see cref="BeatAt"/> for any radio, lying here or carried (<see cref="RadioPlay"/>): which CD,
    /// since when, and the clock. False when the CD is unknown here, beatless or over.
    /// </summary>
    public static bool BeatOf(int cdId, double startedAt, double serverNow, out float beatPhase, out int beatIndex, out int bar, out MusicStyle style)
    {
        beatPhase = 0; beatIndex = 0; bar = 0; style = MusicStyle.Pop;
        if (cdId == 0 || CdLibrary.Instance?.Find(cdId) is not { } cd || cd.Bpm < 1f) return false;
        double t = serverNow - startedAt - cd.BeatOffset;
        if (t > cd.Duration) return false;
        double beat = t * cd.Bpm / 60.0;
        double floor = Math.Floor(beat);
        beatPhase = (float)(beat - floor);
        beatIndex = (int)floor;
        bar = (int)Math.Floor(floor / 4.0);
        // the chess type beat is danced as the rat dance, whatever the analyser made of it (#370)
        style = CdLibrary.IsRatBeat(cdId) ? MusicStyle.RatDance : cd.Style;
        return true;
    }

    /// <summary>
    /// The red key on its front, in the world: a VR fingertip poking it switches the radio on or
    /// off (#725). Authored at (0.045, 0.06, front) facing +Z; the mesh build turns it to (−x, y, −z).
    /// </summary>
    public Vector3 KeyPosition => GlobalTransform * new Vector3(-0.045f, 0.06f, -(BodyD * 0.5f + 0.012f));

    // ---- the look -------------------------------------------------------------------------------

    private static ArrayMesh? _mesh;

    /// <summary>A boombox, origin at its centre, 0.46 m wide: real size, like every item.</summary>
    public static ArrayMesh Mesh()
    {
        if (_mesh != null) return _mesh;
        var s = new MeshScratch();
        var shell = new Color(0.16f, 0.17f, 0.19f);
        var grille = new Color(0.08f, 0.08f, 0.09f);
        var chrome = new Color(0.72f, 0.74f, 0.76f);
        var red = new Color(0.80f, 0.12f, 0.10f);
        s.Box(Vector3.Zero, new Vector3(BodyW, BodyH, BodyD), shell);
        // two speakers on the front face (+Z when authored; the build turns it to -Z)
        foreach (float x in new[] { -0.14f, 0.14f })
        {
            s.Box(new Vector3(x, -0.01f, BodyD * 0.5f + 0.004f), new Vector3(0.13f, 0.13f, 0.008f), grille);
            s.Ring(new Vector3(x, -0.01f, BodyD * 0.5f + 0.009f), Vector3.Back, 0.03f, 0.06f, 0.004f, chrome, 12);
        }
        // the tape deck between them, and a row of buttons above it
        s.Box(new Vector3(0, -0.03f, BodyD * 0.5f + 0.004f), new Vector3(0.11f, 0.06f, 0.008f), grille);
        for (int i = 0; i < 4; i++)
            s.Box(new Vector3(-0.045f + i * 0.03f, 0.06f, BodyD * 0.5f + 0.006f), new Vector3(0.02f, 0.015f, 0.012f), i == 3 ? red : chrome);
        // handle over the top, and the aerial off one corner
        s.Tube(new Vector3(-0.15f, BodyH * 0.5f, 0), new Vector3(-0.15f, BodyH * 0.5f + 0.05f, 0), 0.008f, chrome);
        s.Tube(new Vector3(0.15f, BodyH * 0.5f, 0), new Vector3(0.15f, BodyH * 0.5f + 0.05f, 0), 0.008f, chrome);
        s.Tube(new Vector3(-0.15f, BodyH * 0.5f + 0.05f, 0), new Vector3(0.15f, BodyH * 0.5f + 0.05f, 0), 0.008f, chrome);
        s.Tube(new Vector3(0.20f, BodyH * 0.5f, -0.04f), new Vector3(0.28f, BodyH * 0.5f + 0.30f, -0.06f), 0.004f, chrome);
        return _mesh = s.Build();
    }
}
