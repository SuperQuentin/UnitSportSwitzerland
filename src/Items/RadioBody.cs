using Godot;
using UnitSport.Audio;
using UnitSport.Audio.Cd;
using UnitSport.Avatar;
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
/// Nobody streams audio to anyone. Each client fetches the Ogg once (<see cref="CdCache"/>) and
/// plays it from <c>ServerNow − StartedAt</c> on the shared clock, nudging the playback position
/// back in line when it drifts. The beat the dancers follow (<see cref="BeatAt"/>) is computed
/// from that same clock and never from the playback position, so a client still downloading the
/// CD dances in time with everyone else, in silence, until it arrives.
/// </para>
/// </summary>
public partial class RadioBody : RigidBody3D
{
    public const string Group = "radios";

    [Export] public int CdId { get; set; }
    [Export] public double StartedAt { get; set; }
    [Export] public bool Playing { get; set; }
    [Export] public bool Settled { get; set; }

    /// <summary>Peer that simulated the fall; 0 for the server.</summary>
    public long Owner { get; private set; }

    /// <summary>Seconds with no player within range — for despawning.</summary>
    public double LonelyFor { get; set; }

    /// <summary>What the speaker is at right now, seconds into the CD, or NaN while silent. For the probes.</summary>
    public double HeardPosition { get; private set; } = double.NaN;

    /// <summary>Where the clock says the CD is, seconds, or NaN when nothing plays.</summary>
    public double WantedPosition => Playing ? ClockSync.ServerNow - StartedAt : double.NaN;

    /// <summary>The CD in the tray, when the library knows it.</summary>
    public CdInfo? Cd => CdLibrary.Instance?.All.GetValueOrDefault(CdId);

    private const float BodyW = 0.46f, BodyH = 0.22f, BodyD = 0.16f;
    private const double SettleAfter = 8, RestFor = 1;
    private const float DriftTolerance = 0.08f;

    private RadioState _initial;
    private MultiplayerSynchronizer? _sync;
    private double _age, _restTime, _sinceSeek;
    private AudioStreamPlayer3D? _speaker;
    private int _loadedCd = -1;
    private bool _fetching, _fetchFailed;
    private string? _oggPath;

    public static RadioBody Create(RadioState state)
    {
        var r = new RadioBody
        {
            Name = string.IsNullOrEmpty(state.Name) ? $"radio_local_{Interlocked.Increment(ref _localCounter)}" : state.Name,
            _initial = state,
            Owner = state.Owner,
            CdId = state.CdId,
            StartedAt = state.StartedAt,
            Playing = state.Playing,
            Settled = state.Settled,
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
        Position = s.Position;
        Rotation = new Vector3(0, s.Yaw, 0);
        Mass = 3f;
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(BodyW, BodyH, BodyD) } });

        // the fall: whoever threw it simulates, the others move the box where they are told
        var fall = new SceneReplicationConfig();
        foreach (var prop in new[] { ".:position", ".:rotation", ".:Settled" }) fall.AddProperty(prop);
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
        foreach (var prop in new[] { ".:CdId", ".:StartedAt", ".:Playing" })
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
            AngularVelocity = new Vector3(GD.Randf() * 6f - 3f, GD.Randf() * 2f - 1f, GD.Randf() * 6f - 3f);
            ContactMonitor = false;
        }

        if (!Headless && !NetworkManager.DedicatedServer)
        {
            AddChild(new MeshInstance3D { Name = "Visual", Mesh = Mesh(), MaterialOverride = ItemDefs.Material });
            SfxBus.Ensure();
            _speaker = new AudioStreamPlayer3D
            {
                Name = "Speaker",
                Bus = SfxBus.Name,
                UnitSize = 8f,
                MaxDistance = 120f,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                AttenuationFilterCutoffHz = 8000f,
            };
            AddChild(_speaker);
        }
    }

    /// <summary>Authority: the tumble, until it comes to rest.</summary>
    public override void _PhysicsProcess(double delta)
    {
        if (Settled) return;
        _age += delta;
        _restTime = LinearVelocity.LengthSquared() < 0.05f * 0.05f ? _restTime + delta : 0;
        if (Sleeping || _restTime > RestFor || _age > SettleAfter || Position.Y < -500)
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
        if (_speaker != null) UpdateSpeaker(delta);
    }

    /// <summary>The state to respawn it from: where it is now, what it plays.</summary>
    public RadioState Capture() => new(Name, Owner, Position, Rotation.Y, Vector3.Zero, CdId, StartedAt, Playing, Settled);

    /// <summary>
    /// The beat the CD is on, from the shared clock alone. False when nothing plays or the CD is
    /// unknown here. <paramref name="beatPhase"/> 0..1 within the beat, <paramref name="bar"/>
    /// counts four-beat bars from the first beat.
    /// </summary>
    public bool BeatAt(double serverNow, out float beatPhase, out int beatIndex, out int bar, out MusicStyle style)
    {
        beatPhase = 0; beatIndex = 0; bar = 0; style = MusicStyle.Pop;
        if (!Playing || Cd is not { } cd || cd.Bpm < 1f) return false;
        double t = serverNow - StartedAt - cd.BeatOffset;
        if (t > cd.Duration) return false;
        double beat = t * cd.Bpm / 60.0;
        double floor = Math.Floor(beat);
        beatPhase = (float)(beat - floor);
        beatIndex = (int)floor;
        bar = (int)Math.Floor(floor / 4.0);
        style = cd.Style;
        return true;
    }

    // ---- the speaker ---------------------------------------------------------------------------

    private void UpdateSpeaker(double delta)
    {
        var speaker = _speaker!;
        _sinceSeek += delta;
        double want = WantedPosition;
        if (Cd is not { } cd || double.IsNaN(want) || want < 0 || want >= cd.Duration)
        {
            if (speaker.Playing) speaker.Stop();
            HeardPosition = double.NaN;
            return;
        }

        if (_loadedCd != CdId)
        {
            if (speaker.Playing) speaker.Stop();
            HeardPosition = double.NaN;
            if (!TryLoad()) return;
        }

        if (!speaker.Playing)
        {
            speaker.Play((float)want);
            _sinceSeek = 0;
        }
        else
        {
            double heard = speaker.GetPlaybackPosition() + AudioServer.GetTimeSinceLastMix() - AudioServer.GetOutputLatency();
            HeardPosition = heard;
            // a nudge, not a chase: a seek every frame would stutter, and the clock itself moves
            if (Math.Abs(heard - want) > DriftTolerance && _sinceSeek > 1.0)
            {
                speaker.Seek((float)want);
                _sinceSeek = 0;
            }
        }
    }

    /// <summary>Puts the CD's Ogg in the speaker, fetching it from the server first if need be.</summary>
    private bool TryLoad()
    {
        if (_oggPath == null)
        {
            if (_fetching || _fetchFailed) return false;
            if (CdCache.LocalPath(CdId) is { } here) _oggPath = here;
            else
            {
                if (GetNodeOrNull<ChunkStreamer>("../../" + ChunkStreamer.NodeName) is not { } streamer) { _fetchFailed = true; return false; }
                _fetching = true;
                int id = CdId;
                _ = CdCache.FetchAsync(streamer, id).ContinueWith(t => Callable.From(() =>
                {
                    if (!IsInstanceValid(this)) return;
                    _fetching = false;
                    if (t.Result is { } path && id == CdId) _oggPath = path;
                    else _fetchFailed = true;
                }).CallDeferred());
                return false;
            }
        }
        var stream = AudioStreamOggVorbis.LoadFromFile(_oggPath);
        if (stream == null)
        {
            GD.PushWarning($"[cd] could not load {_oggPath}");
            _fetchFailed = true;
            return false;
        }
        stream.Loop = false;
        _speaker!.Stream = stream;
        _loadedCd = CdId;
        return true;
    }

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
