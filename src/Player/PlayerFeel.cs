using Godot;
using UnitSport.Audio;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Everything that makes movement feel physical without changing it: sound, camera shake,
/// speed lines, particles, rumble and a small HUD. Attached to the local player only.
///
/// <para>
/// Kept apart from <see cref="FootPlayer"/> on purpose. The controller decides where the body
/// goes; this only listens — to its events (landed, jumped, wall-jumped, slid, crashed) and to
/// its live state (speed, lean, what is being ridden) — and turns them into feedback. Nothing
/// here can move the player, so none of it can introduce a physics bug, and every effect can be
/// switched off in the settings without touching movement.
/// </para>
///
/// <para>
/// Intensity is always measured against what is ordinary for the current mount
/// (<see cref="Excitement"/>): 40 km/h is a gentle roll on a bike and a terrifying sprint on
/// foot, and a single absolute speed would leave one of them silent or the other screaming.
/// </para>
/// </summary>
public partial class PlayerFeel : Node3D
{
    private readonly FootPlayer _player;

    // --- audio ---
    private AudioStreamPlayer _hiss = null!, _tyre = null!, _scrape = null!, _rotor = null!, _engine = null!;
    private float _proximity;
    private readonly AudioStreamPlayer[] _voices = new AudioStreamPlayer[8];
    private int _nextVoice;
    private float _stepAccum;
    private float _tickAccum;
    private readonly Random _rng = new(7);

    // --- shake ---
    private float _trauma;
    private float _time;
    private readonly FastNoiseLite _noise = new() { Frequency = 1f, Seed = 3 };
    private float _rumbleTimer;

    // --- screen ---
    private ShaderMaterial _lines = null!;
    private ColorRect _linesRect = null!;
    private CanvasLayer _screen = null!;
    private Label _speedLabel = null!, _popup = null!;
    private ProgressBar _boostBar = null!, _healthBar = null!;
    private Label _engineLabel = null!, _hint = null!;
    private float _hintPulse;
    private ColorRect _hurtFlash = null!;
    private bool _wasBoosting;
    private float _popupLife;

    // --- particles ---
    private GpuParticles3D _spray = null!, _dust = null!;
    private ParticleProcessMaterial _sprayMat = null!;

    private float _airTime;
    private bool _wasGrounded = true;

    private static readonly Color Snow = new(0.93f, 0.95f, 1.0f);
    private static readonly Color Dirt = new(0.60f, 0.54f, 0.45f);

    public PlayerFeel(FootPlayer player)
    {
        _player = player;
        Name = "Feel";
    }

    public override void _Ready()
    {
        _hiss = Loop(SfxSynth.Hiss);
        _tyre = Loop(SfxSynth.Tyre);
        _scrape = Loop(SfxSynth.Scrape);
        _rotor = Loop(SfxSynth.Rotor);
        _engine = Loop(SfxSynth.Engine);
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AudioStreamPlayer { Name = $"Voice{i}" };
            AddChild(_voices[i]);
        }

        BuildScreen();
        _spray = Emitter("Spray", 90, 0.6f, 0.07f);
        _sprayMat = (ParticleProcessMaterial)_spray.ProcessMaterial;
        _dust = Emitter("Dust", 40, 0.8f, 0.10f);
        ((ParticleProcessMaterial)_dust.ProcessMaterial).Color = Dirt;

        _player.Landed += OnLanded;
        _player.Jumped += () => Play(SfxSynth.Whoosh, 0.30f, 1.25f);
        _player.WallJumped += () =>
        {
            Play(SfxSynth.Whoosh, 0.6f, 0.9f);
            Play(SfxSynth.Steps[_rng.Next(4)], 0.7f, 0.8f);   // the foot hitting the wall
            AddTrauma(0.18f);
        };
        _player.SlideStarted += () => Play(SfxSynth.Whoosh, 0.45f, 0.7f);
        _player.Mantled += () =>
        {
            Play(SfxSynth.Steps[_rng.Next(4)], 0.6f, 0.75f);   // hands on the lip
            Play(SfxSynth.Whoosh, 0.25f, 1.3f);
        };
        _player.Hurt += amount =>
        {
            AddTrauma(Mathf.Clamp(amount / 60f, 0.15f, 0.7f));
            _hurtFlash.Color = new Color(0.8f, 0f, 0f, Mathf.Clamp(amount / 80f, 0.15f, 0.45f));
        };
        _player.Shaken += strength => AddTrauma(strength * strength);
        _player.EngineToggled += on =>
        {
            // the starter's clunk; the spool-up or wind-down that follows is the rest of it
            Play(SfxSynth.Impact, 0.35f, on ? 1.6f : 1.1f);
            Popup(on ? "ENGINE ON" : "ENGINE OFF", on);
        };
        _player.Announced += (text, good) =>
        {
            Popup(text, good);
            if (good)
            {
                Play(SfxSynth.Chime, 0.45f, 1f);
                PlayerInput.Rumble(0.5f, 0.2f, 0.12f);
            }
        };
        _player.Impacted += lost =>
        {
            Play(SfxSynth.Impact, Mathf.Clamp(lost * 0.12f, 0.25f, 1f), 1f);
            AddTrauma(Mathf.Clamp(lost * 0.12f, 0.15f, 0.8f));
        };
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool viewing = _player.IsViewing;
        _screen.Visible = viewing;
        if (!viewing)
        {
            // someone else's camera is on screen (the fly camera): nothing of this belongs there
            SetLoop(_hiss, 0, 1); SetLoop(_tyre, 0, 1); SetLoop(_scrape, 0, 1);
            SetLoop(_rotor, 0, 1); SetLoop(_engine, 0, 1);
            _spray.Emitting = _dust.Emitting = false;
            return;
        }

        var ride = _player.Ride;
        bool grounded = _player.IsOnFloor();
        float speed = _player.GroundSpeed;
        float excite = _player.Vehicle is Flyer flyer
            ? Mathf.Clamp((speed - flyer.Thrill.Calm) / (flyer.Thrill.Fast - flyer.Thrill.Calm), 0f, 1.5f)
            : Excitement(ride, speed);

        UpdateAir(dt, grounded);
        UpdateAudio(dt, ride, grounded, speed);
        UpdateParticles(ride, grounded, speed);
        UpdateShake(dt, ride, grounded, excite);
        UpdateHud(dt, ride, speed);

        // boosting always shows them: the meter was earned, and spending it should look like it
        float lines = Mathf.Clamp((excite - 0.35f) / 0.65f, 0f, 1f);
        if (_player.Boosting) lines = Mathf.Max(lines, 0.75f);
        _lines.SetShaderParameter("intensity", GameSettings.Current.SpeedLines ? lines : 0f);

        if (_player.Boosting && !_wasBoosting)
        {
            Play(SfxSynth.Whoosh, 0.6f, 0.8f);
            AddTrauma(0.2f);
        }
        _wasBoosting = _player.Boosting;
        var size = GetViewport().GetVisibleRect().Size;
        _lines.SetShaderParameter("aspect", size.X / Mathf.Max(1f, size.Y));
    }

    /// <summary>0 at a mount's everyday pace, 1 where it starts to feel fast, beyond that above.</summary>
    private static float Excitement(RideKind ride, float speed)
    {
        var (calm, fast) = ride switch
        {
            RideKind.RoadBike => (9f, 18f),    // 32 → 65 km/h
            RideKind.Skis => (9f, 22f),        // 32 → 80 km/h
            _ => (4.8f, 9f),                   // above a run: only slides and launches get here
        };
        return Mathf.Clamp((speed - calm) / (fast - calm), 0f, 1.5f);
    }

    // ------------------------------------------------------------------------------------
    // audio
    // ------------------------------------------------------------------------------------

    private void UpdateAudio(float dt, RideKind ride, bool grounded, float speed)
    {
        var flight = _player.Flight;
        SetLoop(_rotor, ride == RideKind.Helicopter ? 0.25f + 0.55f * flight.Spool : 0f,
            0.55f + 0.5f * flight.Spool);
        SetLoop(_engine, ride == RideKind.Plane ? 0.2f + 0.45f * flight.Spool : 0f,
            0.6f + 0.8f * flight.Spool);

        // Proximity: a wingsuit fast and low is the whole point of one. Time spent under 20 m at
        // speed pays out as a named popup once the pilot climbs out of it (or lands).
        if (ride == RideKind.Wingsuit && _player.Clearance < 20f && speed > 30f) _proximity += dt;
        else if (_proximity > 0)
        {
            if (_proximity > 1f) _player.Announce($"PROXIMITY  {_proximity:0.0} s", true);
            _proximity = 0;
        }

        var motion = _player.Motion;
        var input = _player.LastRideInput;

        // tyres: roar with speed, only while they touch something
        bool bike = ride == RideKind.RoadBike;
        SetLoop(_tyre, bike && grounded ? Mathf.Clamp(speed / 14f, 0f, 1f) * 0.55f : 0f,
            0.55f + speed / 22f);

        // freewheel: the pawls tick when the wheel turns and the legs do not
        if (bike && grounded && speed > 1.5f && input.Throttle < 0.05f)
        {
            _tickAccum += Mathf.Min(speed * 5f, 55f) * dt;
            if (_tickAccum >= 1f)
            {
                _tickAccum -= Mathf.Floor(_tickAccum);
                Play(SfxSynth.Tick, 0.18f, 0.9f + (float)_rng.NextDouble() * 0.2f);
            }
        }

        // skis: a glide hiss with speed, and the edge biting as the carve deepens
        bool skis = ride == RideKind.Skis;
        float edge = Mathf.Abs(motion.Bank);
        float hiss = skis && grounded
            ? Mathf.Clamp(speed / 20f, 0f, 1f) * 0.3f + edge * Mathf.Clamp(speed / 10f, 0f, 1f) * 0.7f
            : 0f;
        SetLoop(_hiss, Mathf.Min(hiss, 1f), 0.8f + edge * 0.35f + speed / 60f);

        // scrape: a slide on foot, or a bike braking hard
        float scrape = 0f, scrapePitch = 1f;
        if (ride == RideKind.OnFoot && _player.IsSliding && grounded)
        {
            scrape = Mathf.Clamp(speed / 8f, 0.2f, 1f) * 0.8f;
            scrapePitch = 0.8f + speed / 20f;
        }
        else if (bike && grounded && input.Brake > 0.4f && speed > 3f)
        {
            scrape = input.Brake * Mathf.Clamp(speed / 12f, 0f, 1f) * 0.5f;
            scrapePitch = 1.4f;
        }
        SetLoop(_scrape, scrape, scrapePitch);

        // footsteps, at the same cadence the drawn gait uses
        if (ride == RideKind.OnFoot && grounded && !_player.IsSliding && speed > 0.4f)
        {
            _stepAccum += Avatar.HumanMeshBuilder.Cadence(speed) * dt;
            if (_stepAccum >= 1f)
            {
                _stepAccum -= 1f;
                float run = Mathf.Clamp(speed / _player.RunSpeed, 0f, 1f);
                Play(SfxSynth.Steps[_rng.Next(SfxSynth.Steps.Length)], 0.22f + 0.45f * run,
                    0.9f + (float)_rng.NextDouble() * 0.2f);
            }
        }
        else _stepAccum = 0.6f;   // the first step after stopping lands promptly
    }

    private AudioStreamPlayer Loop(AudioStream stream)
    {
        var p = new AudioStreamPlayer { Stream = stream, VolumeDb = -80f, Autoplay = true };
        AddChild(p);
        return p;
    }

    /// <summary>Eases a loop toward a linear volume and a pitch, so nothing jumps between frames.</summary>
    private static void SetLoop(AudioStreamPlayer p, float volume, float pitch)
    {
        float target = volume * GameSettings.Current.SfxVolume;
        float now = Mathf.DbToLinear(p.VolumeDb);
        float eased = Mathf.Lerp(now, target, 0.12f);
        p.VolumeDb = eased < 0.001f ? -80f : Mathf.LinearToDb(eased);
        p.PitchScale = Mathf.Lerp(p.PitchScale, Mathf.Clamp(pitch, 0.3f, 3f), 0.12f);
        if (!p.Playing) p.Play();
    }

    private void Play(AudioStream stream, float volume, float pitch)
    {
        float v = volume * GameSettings.Current.SfxVolume;
        if (v < 0.005f) return;
        var voice = _voices[_nextVoice];
        _nextVoice = (_nextVoice + 1) % _voices.Length;
        voice.Stream = stream;
        voice.VolumeDb = Mathf.LinearToDb(v);
        voice.PitchScale = pitch;
        voice.Play();
    }

    // ------------------------------------------------------------------------------------
    // landing, air time
    // ------------------------------------------------------------------------------------

    private void OnLanded(float fall)
    {
        float hard = Mathf.Clamp((fall - 2f) / 9f, 0f, 1f);
        Play(SfxSynth.Landing, 0.25f + 0.75f * hard, 1.15f - 0.35f * hard);
        AddTrauma(hard * 0.65f);

        // the FOV dips and springs back: FootPlayer eases its FOV every frame, so a nudge here
        // is undone on its own
        if (_player.Camera is { } cam) cam.Fov -= 6f * hard;

        if (fall > 4f) Puff(_player.Ride == RideKind.Skis ? Snow : Dirt, (int)(10 + 30 * hard));
    }

    private void UpdateAir(float dt, bool grounded)
    {
        // on foot only: time spent flying a plane is not a jump
        if (_player.Ride != RideKind.OnFoot) _airTime = 0;
        else if (!grounded) _airTime += dt;
        else if (!_wasGrounded)
        {
            // a real jump, not a kerb: the number is the reward for going big
            if (_airTime > 0.7f && _player.Ride == RideKind.OnFoot) Popup($"AIR  {_airTime:0.0} s", true);
            _airTime = 0;
        }
        _wasGrounded = grounded;
    }

    // ------------------------------------------------------------------------------------
    // shake and rumble
    // ------------------------------------------------------------------------------------

    private void AddTrauma(float amount) => _trauma = Mathf.Min(1f, _trauma + amount);

    private void UpdateShake(float dt, RideKind ride, bool grounded, float excite)
    {
        _time += dt;
        _trauma = Mathf.Max(0f, _trauma - 1.3f * dt);

        // Speed hums through the frame while the wheels or skis are on the ground: a floor under
        // the trauma rather than trauma itself, so it never accumulates into a jolt.
        float hum = ride != RideKind.OnFoot && (grounded || _player.IsFlying)
            ? Mathf.Min(excite, 1f) * (_player.IsFlying ? 0.16f : 0.22f) : 0f;
        float shake = Mathf.Max(_trauma, hum);
        // squared: small amounts are barely there, big hits are big — the standard trauma curve
        float amp = shake * shake * GameSettings.Current.ScreenShake;

        if (_player.Camera is { } cam)
        {
            float f = 22f;
            cam.HOffset = _noise.GetNoise2D(_time * f, 0f) * 0.22f * amp;
            cam.VOffset = _noise.GetNoise2D(0f, _time * f) * 0.22f * amp;
        }

        // the pad buzzes with the road, lightly, in short overlapping pulses
        _rumbleTimer -= dt;
        if (hum > 0.05f && _rumbleTimer <= 0f)
        {
            _rumbleTimer = 0.1f;
            PlayerInput.Rumble(hum * 0.8f, 0f, 0.12f);
        }
    }

    // ------------------------------------------------------------------------------------
    // particles
    // ------------------------------------------------------------------------------------

    private void UpdateParticles(RideKind ride, bool grounded, float speed)
    {
        var motion = _player.Motion;
        var input = _player.LastRideInput;

        // snow thrown off the edges: a little from any glide, a sheet from a hard carve,
        // sprayed to the outside of the turn and back
        float edge = Mathf.Abs(motion.Bank);
        bool spray = ride == RideKind.Skis && grounded && speed > 3f;
        _spray.Emitting = spray;
        if (spray)
        {
            _spray.AmountRatio = Mathf.Clamp(0.15f + edge * speed / 12f, 0f, 1f);
            float outward = -Mathf.Sign(motion.Bank);   // bank>0 turns left: snow goes right
            // Mostly sideways and up: thrown straight back, the specks fly into the chase camera
            // and the one crossing the lens fills a tenth of the screen.
            _sprayMat.Direction = new Vector3(outward * (0.5f + edge), 0.8f, 0.35f).Normalized();
            _sprayMat.InitialVelocityMin = 1f + speed * 0.12f;
            _sprayMat.InitialVelocityMax = 2f + speed * 0.25f;
            _sprayMat.Color = Snow;
        }

        // dust behind a slide, or a skidding back wheel
        bool slide = ride == RideKind.OnFoot && _player.IsSliding && grounded;
        bool skid = ride == RideKind.RoadBike && grounded && input.Brake > 0.5f && speed > 4f;
        _dust.Emitting = slide || skid;
        if (slide || skid) _dust.AmountRatio = Mathf.Clamp(speed / 10f, 0.2f, 1f);
    }

    /// <summary>A continuous emitter at the feet, trailing behind (+Z is behind a node).</summary>
    private GpuParticles3D Emitter(string name, int amount, float life, float size)
    {
        var mat = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.25f,
            Direction = new Vector3(0, 0.6f, 1f),
            Spread = 25f,
            InitialVelocityMin = 1f,
            InitialVelocityMax = 2.5f,
            Gravity = new Vector3(0, -7f, 0),
            DampingMin = 1f,
            DampingMax = 2f,
            ScaleMin = 0.6f,
            ScaleMax = 1.4f,
            Color = Snow,
            ColorRamp = FadeRamp(),
        };
        var p = new GpuParticles3D
        {
            Name = name,
            Amount = amount,
            Lifetime = life,
            Emitting = false,
            ProcessMaterial = mat,
            DrawPass1 = Speck(size),
            Position = new Vector3(0, 0.1f, 0.5f),
            // particles are left where they were thrown, not dragged along with the player
            LocalCoords = false,
            VisibilityAabb = new Aabb(new Vector3(-8, -4, -8), new Vector3(16, 8, 16)),
        };
        AddChild(p);
        return p;
    }

    /// <summary>A one-shot burst left at the landing spot, freed once it has settled.</summary>
    private void Puff(Color color, int amount)
    {
        var mat = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingAxis = Vector3.Up,
            EmissionRingRadius = 0.5f,
            EmissionRingInnerRadius = 0.2f,
            EmissionRingHeight = 0.05f,
            Direction = Vector3.Up,
            Spread = 70f,
            InitialVelocityMin = 1.5f,
            InitialVelocityMax = 3.5f,
            Gravity = new Vector3(0, -5f, 0),
            DampingMin = 2f,
            DampingMax = 3f,
            ScaleMin = 0.8f,
            ScaleMax = 1.6f,
            Color = color,
            ColorRamp = FadeRamp(),
        };
        var puff = new GpuParticles3D
        {
            Amount = amount,
            Lifetime = 0.8f,
            OneShot = true,
            Explosiveness = 0.95f,
            ProcessMaterial = mat,
            DrawPass1 = Speck(0.16f),
            TopLevel = true,
        };
        AddChild(puff);
        puff.GlobalPosition = _player.GlobalPosition + Vector3.Up * 0.1f;
        puff.Emitting = true;
        GetTree().CreateTimer(1.5).Timeout += puff.QueueFree;
    }

    /// <summary>A flat, unlit billboard speck: dither-world particles, not soft smoke.</summary>
    private static QuadMesh Speck(float size) => new()
    {
        Size = new Vector2(size, size),
        Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        },
    };

    private static GradientTexture1D FadeRamp()
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, 0.9f));
        g.SetColor(1, new Color(1, 1, 1, 0f));
        return new GradientTexture1D { Gradient = g };
    }

    // ------------------------------------------------------------------------------------
    // screen: speed lines and HUD
    // ------------------------------------------------------------------------------------

    private void BuildScreen()
    {
        // one layer for both: under the lens (5) is pointless — the lens is replay-only — and
        // under the HUDs (10) that menus and chat draw on
        _screen = new CanvasLayer { Name = "FeelScreen", Layer = 4 };
        AddChild(_screen);

        _lines = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/speed_lines.gdshader") };
        _linesRect = new ColorRect { Material = _lines, MouseFilter = Control.MouseFilterEnum.Ignore };
        _linesRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _screen.AddChild(_linesRect);

        _speedLabel = HudLabel(30);
        _speedLabel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _speedLabel.GrowHorizontal = Control.GrowDirection.Both;
        _speedLabel.GrowVertical = Control.GrowDirection.Begin;
        _speedLabel.OffsetBottom = -22;
        _screen.AddChild(_speedLabel);

        _boostBar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = false,
            CustomMinimumSize = new Vector2(180, 8),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _boostBar.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _boostBar.OffsetLeft = -90; _boostBar.OffsetRight = 90;
        _boostBar.OffsetTop = -14; _boostBar.OffsetBottom = -6;
        var fill = new StyleBoxFlat { BgColor = new Color(0.25f, 0.8f, 1f) };
        var back = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.5f) };
        _boostBar.AddThemeStyleboxOverride("fill", fill);
        _boostBar.AddThemeStyleboxOverride("background", back);
        _screen.AddChild(_boostBar);

        _healthBar = new ProgressBar
        {
            MinValue = 0, MaxValue = FootPlayer.MaxHealth, ShowPercentage = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _healthBar.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _healthBar.OffsetLeft = 18; _healthBar.OffsetRight = 218;
        _healthBar.OffsetTop = -30; _healthBar.OffsetBottom = -20;
        _healthBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.9f, 0.2f, 0.2f) });
        _healthBar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.5f) });
        _screen.AddChild(_healthBar);

        _engineLabel = HudLabel(16);
        _engineLabel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _engineLabel.GrowHorizontal = Control.GrowDirection.Begin;
        _engineLabel.GrowVertical = Control.GrowDirection.Begin;
        _engineLabel.OffsetRight = -18; _engineLabel.OffsetBottom = -16;
        _screen.AddChild(_engineLabel);

        // What the next button does, while it matters: the base jump chain has no menu, so a
        // player who does not already know that Jump opens the suit mid-fall would never find it.
        _hint = HudLabel(22);
        _hint.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _hint.GrowHorizontal = Control.GrowDirection.Both;
        _hint.GrowVertical = Control.GrowDirection.Begin;
        _hint.OffsetBottom = -70;
        _hint.Visible = false;
        _screen.AddChild(_hint);

        _hurtFlash = new ColorRect { Color = new Color(0.8f, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _hurtFlash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _screen.AddChild(_hurtFlash);

        _popup = HudLabel(34);
        _popup.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _popup.GrowHorizontal = Control.GrowDirection.Both;
        _popup.OffsetTop = 90;
        _popup.AddThemeColorOverride("font_color", new Color(1f, 0.82f, 0.25f));
        _popup.Visible = false;
        _screen.AddChild(_popup);
    }

    private static Label HudLabel(int size)
    {
        var label = new Label { HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeConstantOverride("outline_size", 6);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        return label;
    }

    private void UpdateHud(float dt, RideKind ride, float speed)
    {
        // mounted only: on foot the pace is the walk, and a number would be clutter
        _speedLabel.Visible = ride != RideKind.OnFoot;
        if (_speedLabel.Visible)
            _speedLabel.Text = _player.IsFlying
                ? $"{speed * 3.6f:0} km/h    {_player.Clearance:0} m"
                + (ride == RideKind.Plane ? $"    {_player.Flight.Control * 100:0}%" : "")
                : $"{speed * 3.6f:0} km/h";

        // health only when it is not full: a bar that is always full is clutter
        _healthBar.Visible = _player.Health < FootPlayer.MaxHealth - 0.5f;
        _healthBar.Value = _player.Health;
        _hurtFlash.Color = _hurtFlash.Color with { A = Mathf.MoveToward(_hurtFlash.Color.A, 0f, 1.2f * dt) };

        var vehicle = _player.Vehicle;
        _engineLabel.Visible = vehicle is { IsVehicle: true };
        if (_engineLabel.Visible)
            _engineLabel.Text = (vehicle!.HasEngine ? (_player.EngineOn ? "ENGINE ON  (I / D-pad ↑)" : "ENGINE OFF  (I / D-pad ↑)") + "\n" : "")
                + $"DAMAGE {100f - _player.VehicleHealth / vehicle.MaxHealth * 100f:0}%    E / (Y) get out";

        UpdateHint(dt, ride);

        // the boost meter, only where boost exists: mounted, Game profile
        _boostBar.Visible = ride is RideKind.RoadBike or RideKind.Skis && Rideable.Arcade;
        _boostBar.Value = _player.BoostMeter;
        _boostBar.Modulate = _player.Boosting ? new Color(1.6f, 1.6f, 1.6f) : Colors.White;

        if (_popupLife > 0)
        {
            _popupLife -= dt;
            float t = 1f - _popupLife / 1.4f;
            // pops in big, settles, then fades
            float scale = 1f + 0.5f * Mathf.Exp(-12f * t);
            _popup.Scale = new Vector2(scale, scale);
            _popup.PivotOffset = _popup.Size * 0.5f;
            _popup.Modulate = new Color(1, 1, 1, Mathf.Clamp(_popupLife / 0.4f, 0f, 1f));
            _popup.Visible = _popupLife > 0;
        }
    }

    /// <summary>
    /// Button prompts for the base-jump chain, in the words of the device last used:
    /// falling with room → open the wingsuit; in the suit → open the canopy (urgent when low);
    /// under the canopy → how to steer and flare.
    /// </summary>
    private void UpdateHint(float dt, RideKind ride)
    {
        bool pad = PlayerInput.LastDevice == InputDevice.Gamepad;
        string jump = pad ? "(A)" : "SPACE";
        string text = "";
        bool urgent = false;

        switch (ride)
        {
            case RideKind.OnFoot:
                // mirrors FootPlayer's deploy test: falling, and more than 12 m of air below
                if (!_player.IsOnFloor() && _player.Velocity.Y < -3f && _player.Terrain != null
                    && _player.Terrain.TryGetHeight(_player.GlobalPosition, out float g)
                    && _player.GlobalPosition.Y - g > 12f)
                    text = $"{jump}  open WINGSUIT";
                break;
            case RideKind.Wingsuit:
                urgent = _player.Clearance < 80f;
                text = $"{jump}  open PARACHUTE" + (urgent ? "  — NOW!" : "")
                    + $"\n{(pad ? "left stick" : "W / S")} dive · flare     {(pad ? "left stick" : "A / D")} turn";
                break;
            case RideKind.Parachute:
                text = $"{(pad ? "left stick" : "A / D")} steer     {(pad ? "pull back" : "S")} brake — hold it to flare the landing";
                break;
        }

        _hint.Visible = text.Length > 0;
        if (!_hint.Visible) return;
        _hint.Text = text;
        _hintPulse += dt * (urgent ? 9f : 3f);
        float a = 0.75f + 0.25f * Mathf.Sin(_hintPulse);
        _hint.Modulate = urgent ? new Color(1f, 0.35f, 0.3f, a) : new Color(1f, 1f, 1f, a);
    }

    public void Popup(string text, bool good)
    {
        _popup.Text = text;
        _popup.AddThemeColorOverride("font_color", good ? new Color(1f, 0.82f, 0.25f) : new Color(1f, 0.35f, 0.3f));
        _popupLife = 1.4f;
        _popup.Visible = true;
    }
}
