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
    private AudioStreamPlayer _hiss = null!, _tyre = null!, _scrape = null!;
    private EngineSynth _rotor = null!, _engine = null!;
    private AudioStreamPlayer _squeal = null!;
    private double _beep;
    private int _puffs;
    private EngineSynth? _carEngine;
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
    private static readonly StringName IntensityParam = "intensity", AspectParam = "aspect";
    private ShaderMaterial _lines = null!;
    private ColorRect _linesRect = null!;
    private CanvasLayer _screen = null!;
    private Label _speedLabel = null!, _popup = null!;
    private ProgressBar _boostBar = null!, _healthBar = null!, _rpmBar = null!, _airBar = null!;
    private StyleBoxFlat _airFill = null!;
    private Label _driftLabel = null!;
    private StyleBoxFlat _rpmFill = null!;
    private Label _engineLabel = null!, _hint = null!;
    private float _hintPulse;
    private ColorRect _hurtFlash = null!;
    private bool _wasBoosting;
    private float _popupLife;

    // --- particles ---
    private GpuParticles3D _spray = null!, _dust = null!;
    private ParticleProcessMaterial _sprayMat = null!;
    private GpuParticles3D _smoke = null!;

    // --- drift score ---
    private const float DriftMinSlip = 0.26f, DriftMaxSlip = 1.4f, DriftMinSpeed = 8f;
    private const float DriftPointsPerRadMetre = 10f, DriftComboSeconds = 1.5f, DriftEndGrace = 0.6f;
    private bool _drifting;
    private float _driftScore, _driftTime, _driftGrace;

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
        _squeal = Loop(SfxSynth.Squeal);
        _rotor = new EngineSynth(EngineProfile.Turboshaft, spatial: false, seed: 1);
        _engine = new EngineSynth(EngineProfile.PistonAero, spatial: false, seed: 2);
        AddChild(_rotor);
        AddChild(_engine);
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AudioStreamPlayer { Name = $"Voice{i}", Bus = SfxBus.Name };
            AddChild(_voices[i]);
        }

        BuildScreen();
        _spray = Emitter("Spray", 90, 0.6f, 0.07f);
        _sprayMat = (ParticleProcessMaterial)_spray.ProcessMaterial;
        _dust = Emitter("Dust", 40, 0.8f, 0.10f);
        ((ParticleProcessMaterial)_dust.ProcessMaterial).Color = Dirt;
        // tyre smoke: big, slow white puffs that drift up from behind the rear wheels
        _smoke = Emitter("TyreSmoke", 70, 1.2f, 0.7f);
        _smoke.Position = new Vector3(0, 0.15f, 1.3f);
        var smokeMat = (ParticleProcessMaterial)_smoke.ProcessMaterial;
        smokeMat.Color = new Color(0.95f, 0.95f, 0.95f);
        smokeMat.EmissionSphereRadius = 0.7f;
        smokeMat.Gravity = new Vector3(0, 0.8f, 0);
        smokeMat.InitialVelocityMin = 0.3f;
        smokeMat.InitialVelocityMax = 1.2f;
        smokeMat.ScaleMax = 2.2f;

        _player.Landed += OnLanded;
        _player.Jumped += () => Play(SfxSynth.WhooshBank, 0.30f, 1.25f);
        _player.WallJumped += () =>
        {
            Play(SfxSynth.WhooshBank, 0.6f, 0.9f);
            Play(SfxSynth.StepsBank, 0.7f, 0.8f);   // the foot hitting the wall
            AddTrauma(0.18f);
        };
        _player.SlideStarted += () => Play(SfxSynth.WhooshBank, 0.45f, 0.7f);
        // in the water (#301): the plunge, each stroke, a breath after a long time under
        _player.Splashed += strength =>
        {
            Play(SfxSynth.SplashBank, 0.35f + 0.65f * strength, 1.15f - 0.3f * strength);
            AddTrauma(strength * 0.35f);
        };
        _player.Stroked += () => Play(SfxSynth.StrokeBank, 0.22f, 0.9f + (float)_rng.NextDouble() * 0.2f);
        _player.Gasped += () => Play(SfxSynth.GaspBank, 0.55f, 1f);
        _player.Mantled += () =>
        {
            Play(SfxSynth.StepsBank, 0.6f, 0.75f);   // hands on the lip
            Play(SfxSynth.WhooshBank, 0.25f, 1.3f);
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
            Play(SfxSynth.ImpactBank, 0.35f, on ? 1.6f : 1.1f);
            Popup(on ? "ENGINE ON" : "ENGINE OFF", on);
        };
        _player.Announced += (text, good) =>
        {
            Popup(text, good);
            if (good)
            {
                PlayChime();
                PlayerInput.Rumble(0.5f, 0.2f, 0.12f);
            }
        };
        _player.Impacted += lost =>
        {
            if (_drifting && lost > 2f)
            {
                _drifting = false;
                Popup("CRASHED", false);
            }
            Play(SfxSynth.ImpactBank, Mathf.Clamp(lost * 0.12f, 0.25f, 1f), 1f);
            AddTrauma(Mathf.Clamp(lost * 0.12f, 0.15f, 0.8f));
            // the same knock through a steering wheel's rim (nothing unless one is driving this)
            SteeringWheel.Knock(Mathf.Clamp(lost / 15f, 0.25f, 1f));
        };
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool viewing = _player.IsViewing;
        _screen.Visible = viewing;
        // a steering wheel's forces (#68): what this vehicle's steering feels, while it is on screen
        if (viewing && _player.Vehicle is { WheelLock: > 0f } wheeled)
        {
            var feel = wheeled.Feel;
            // the engine's shake, from the rpm: the player, not the vehicle model, knows it is running
            if (wheeled is IEngined engined && _player.EngineOn)
            {
                var (shake, hz) = WheelFeel.EngineFrom(engined.Rpm, engined.Rpm01);
                feel = feel with { Engine = shake, EngineHz = hz };
            }
            SteeringWheel.Drive(feel, wheeled.WheelLock);
        }
        if (!viewing)
        {
            // someone else's camera is on screen (the fly camera): nothing of this belongs there
            SetLoop(_hiss, 0, 1); SetLoop(_tyre, 0, 1); SetLoop(_scrape, 0, 1); SetLoop(_squeal, 0, 1);
            _rotor.Set(0, 0, 0, 0); _engine.Set(0, 0, 0, 0); _carEngine?.Set(0, 0, 0, 0);
            _spray.Emitting = _dust.Emitting = _smoke.Emitting = false;
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
        UpdateDrift(dt, grounded, speed);
        UpdateParticles(ride, grounded, speed);
        UpdateShake(dt, ride, grounded, excite);
        UpdateHud(dt, ride, speed);

        // boosting always shows them: the meter was earned, and spending it should look like it
        float lines = Mathf.Clamp((excite - 0.35f) / 0.65f, 0f, 1f);
        if (_player.Boosting) lines = Mathf.Max(lines, 0.75f);
        _lines.SetShaderParameter(IntensityParam, GameSettings.Current.SpeedLines ? lines : 0f);

        if (_player.Boosting && !_wasBoosting)
        {
            Play(SfxSynth.WhooshBank, 0.6f, 0.8f);
            AddTrauma(0.2f);
        }
        _wasBoosting = _player.Boosting;
        var size = GetViewport().GetVisibleRect().Size;
        _lines.SetShaderParameter(AspectParam, size.X / Mathf.Max(1f, size.Y));
    }

    /// <summary>0 at a mount's everyday pace, 1 where it starts to feel fast, beyond that above.</summary>
    private static float Excitement(RideKind ride, float speed)
    {
        var (calm, fast) = ride switch
        {
            RideKind.RoadBike => (9f, 18f),    // 32 → 65 km/h
            RideKind.Skis => (9f, 22f),        // 32 → 80 km/h
            _ when CarCatalog.IsCar(ride) => (15f, 40f),   // 54 → 144 km/h
            _ when MotorbikeCatalog.IsMotorbike(ride) => (15f, 45f),   // 54 → 162 km/h
            _ when Boat.IsBoat(ride) => (9f, 20f),   // 32 → 72 km/h: fast on the water
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
        // helicopter: spool is the rotor speed; climbing loads the blades (blade slap)
        bool heli = ride == RideKind.Helicopter;
        _rotor.Set(flight.Spool, flight.Spool, Mathf.Clamp(0.35f + flight.Velocity.Y / 8f, 0f, 1f),
            heli ? 0.3f + 0.6f * flight.Spool : 0f);
        // plane: Control is the throttle lever, spool the rpm it has wound up to
        bool plane = ride == RideKind.Plane;
        _engine.Set(Mathf.Clamp((flight.Spool - 0.15f) / 0.85f, 0f, 1f), flight.Control, flight.Control,
            plane && flight.Spool > 0.02f ? 0.35f + 0.45f * flight.Spool : 0f);

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
        var car = _player.Vehicle as Car;
        bool motor = _player.Vehicle is IEngined;
        SetLoop(_tyre, bike && grounded ? Mathf.Clamp(speed / 14f, 0f, 1f) * 0.55f
            : motor && grounded ? Mathf.Clamp(speed / 30f, 0f, 1f) * 0.5f : 0f,
            0.55f + speed / (motor ? 40f : 22f));
        UpdateCarAudio(car, grounded, speed);

        // freewheel: the pawls tick when the wheel turns and the legs do not
        if (bike && grounded && speed > 1.5f && input.Throttle < 0.05f)
        {
            _tickAccum += Mathf.Min(speed * 5f, 55f) * dt;
            if (_tickAccum >= 1f)
            {
                _tickAccum -= Mathf.Floor(_tickAccum);
                Play(SfxSynth.TickBank, 0.18f, 0.9f + (float)_rng.NextDouble() * 0.2f);
            }
        }

        // skis: a glide hiss with speed, and the edge biting as the carve deepens
        bool skis = ride == RideKind.Skis;
        float edge = Mathf.Abs(motion.Bank);
        float hiss = skis && grounded
            ? Mathf.Clamp(speed / 20f, 0f, 1f) * 0.3f + edge * Mathf.Clamp(speed / 10f, 0f, 1f) * 0.7f
            : 0f;
        // powder is darker, ice brighter and harsher: the one baked hiss is coloured by pitch and level
        float tone = 1f, hissGain = 1f;
        if (skis && grounded)
        {
            var (lowPass, _, gain) = Surfaces.SkiHiss(SurfaceUnderfoot());
            tone = Mathf.Pow(lowPass / 4000f, 0.35f);
            hissGain = gain;
        }
        SetLoop(_hiss, Mathf.Min(hiss * hissGain, 1f), (0.8f + edge * 0.35f + speed / 60f) * tone);

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
                // wading (#380): a slosh a stride instead of the ground's step, louder deeper
                if (_player.WadeDepth > 0.06f)
                    Play(SfxSynth.WadeBank, (0.2f + 0.3f * run) * Wading.StrideVolume(_player.WadeDepth) + 0.08f,
                        1.05f - 0.15f * Mathf.Clamp(_player.WadeDepth, 0f, 1f) + (float)_rng.NextDouble() * 0.1f);
                else
                    Play(Surfaces.Steps(SurfaceUnderfoot()), 0.22f + 0.45f * run,
                        0.9f + (float)_rng.NextDouble() * 0.2f);
            }
        }
        else _stepAccum = 0.6f;   // the first step after stopping lands promptly
    }

    /// <summary>The car's engine from its rpm and pedal, and the squeal from how hard the tyres slide.</summary>
    private void UpdateCarAudio(Car? car, bool grounded, float speed)
    {
        // the engine: any car or motorbike, from its rpm and throttle
        if (_player.Vehicle is IEngined engine)
        {
            if (_carEngine == null || _carEngine.Profile != engine.Sound)
            {
                _carEngine?.QueueFree();
                _carEngine = new EngineSynth(engine.Sound, spatial: false, seed: 3);
                AddChild(_carEngine);
            }
            _carEngine.Set(engine.Rpm01, engine.Throttle, Mathf.Clamp(engine.Throttle * 0.8f + 0.2f * engine.Rpm01, 0f, 1f),
                // half what it was: at 0.75 a car at redline drowned every other sound in the game
                !_player.EngineOn ? 0f
                // a steam engine at STOP is silent (#303)
                : engine is Steamer ? (engine.Rpm01 > 0.02f ? 0.18f + 0.25f * engine.Rpm01 : 0f)
                : 0.15f + 0.22f * engine.Rpm01);
        }
        else _carEngine?.Set(0, 0, 0, 0);

        if (_player.Vehicle is Truck truck)
        {
            float heavySlide = grounded ? Mathf.SmoothStep(0.15f, 0.9f, truck.TyreSlide) : 0f;
            SetLoop(_squeal, heavySlide * Mathf.Clamp(speed / 8f, 0f, 1f) * 0.3f, 0.6f + 0.25f * truck.TyreSlide);
            // the reversing alarm every truck and bus here has, a beep a second
            if (truck.Reversing && _player.EngineOn)
            {
                _beep -= GetProcessDeltaTime();
                if (_beep <= 0) { Play(SfxSynth.Chime, 0.22f, 2.6f); _beep = 0.9; }
            }
            else _beep = 0;
            // the air: a hiss when the parking brake goes on or off
            if (truck.Box.AirPuffs != _puffs) { _puffs = truck.Box.AirPuffs; Play(SfxSynth.Hiss, 0.35f, 1.7f); }
            return;
        }
        if (car == null)
        {
            SetLoop(_squeal, 0, 1);
            return;
        }

        // a squeal is a note, not a hiss: it appears past a threshold and climbs with the slide
        float slide = grounded ? Mathf.SmoothStep(0.15f, 0.9f, car.TyreSlide) : 0f;
        SetLoop(_squeal, slide * Mathf.Clamp(speed / 10f, 0f, 1f) * 0.35f, 0.8f + 0.3f * car.TyreSlide + speed / 90f);
    }

    /// <summary>
    /// Drift score: hold a slip angle above 0.26 rad at speed and points pile up as angle x speed x
    /// time, with the multiplier climbing every 1.5 s. It ends after 0.6 s under the threshold (the
    /// score is banked through the ordinary announce path) or instantly on a crash (see Impacted).
    /// </summary>
    private void UpdateDrift(float dt, bool grounded, float speed)
    {
        if (_player.Vehicle is not Car)
        {
            _drifting = false;
            _driftLabel.Visible = false;
            return;
        }
        float slip = Mathf.Abs(MathX.WrapAngle(_player.Motion.Slip));
        bool valid = grounded && speed > DriftMinSpeed && slip > DriftMinSlip && slip < DriftMaxSlip;
        if (valid)
        {
            if (!_drifting) { _drifting = true; _driftScore = 0; _driftTime = 0; }
            _driftGrace = 0;
            _driftTime += dt;
            _driftScore += slip * speed * dt * DriftPointsPerRadMetre * DriftMultiplier;
        }
        else if (_drifting)
        {
            _driftGrace += dt;
            if (_driftGrace > DriftEndGrace)
            {
                _drifting = false;
                if (_driftScore >= 100f) _player.Announce($"NICE DRIFT  {_driftScore.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}", true);
            }
        }
        _driftLabel.Visible = _drifting;
        if (_drifting)
            _driftLabel.Text = $"DRIFT  {_driftScore.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}" + (DriftMultiplier > 1 ? $"   x{DriftMultiplier}" : "");
    }

    private int DriftMultiplier => 1 + (int)(_driftTime / DriftComboSeconds);

    private AudioStreamPlayer Loop(AudioStream stream)
    {
        var p = new AudioStreamPlayer { Stream = stream, VolumeDb = -80f, Autoplay = true, Bus = SfxBus.Name };
        AddChild(p);
        return p;
    }

    /// <summary>Eases a loop toward a linear volume and a pitch, so nothing jumps between frames.</summary>
    private static void SetLoop(AudioStreamPlayer p, float volume, float pitch)
    {
        float target = volume;   // the slider is on the Sfx bus
        float now = Mathf.DbToLinear(p.VolumeDb);
        float eased = Mathf.Lerp(now, target, 0.12f);
        p.VolumeDb = eased < 0.001f ? -80f : Mathf.LinearToDb(eased);
        p.PitchScale = Mathf.Lerp(p.PitchScale, Mathf.Clamp(pitch, 0.3f, 3f), 0.12f);
        if (!p.Playing) p.Play();
    }

    // a major-pentatonic climb: a streak of clean tricks plays a rising phrase, not one ding
    private static readonly float[] ChimeSteps = [1f, 9f / 8f, 5f / 4f, 3f / 2f, 5f / 3f, 2f];
    private int _chimeStep;
    private double _lastChime = -10;

    private void PlayChime()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        _chimeStep = now - _lastChime < 4.0 ? Math.Min(_chimeStep + 1, ChimeSteps.Length - 1) : 0;
        _lastChime = now;
        Play(SfxSynth.ChimeBank, 0.45f, ChimeSteps[_chimeStep]);
    }

    private Surface SurfaceUnderfoot() => _player.Terrain is { } chunks
        ? Surfaces.At(chunks, _player.GlobalPosition, _player.Indoors)
        : Surface.Grass;

    /// <summary>Plays the next variant of a bank, with its per-play pitch and volume jitter on top.</summary>
    private void Play(SfxBank bank, float volume, float pitch)
    {
        var (stream, jitterPitch, jitterDb) = bank.Pick(_rng);
        Play(stream, volume * Mathf.DbToLinear(jitterDb), pitch * jitterPitch);
    }

    private void Play(AudioStream stream, float volume, float pitch)
    {
        float v = volume;   // the slider is on the Sfx bus
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
        Play(Surfaces.Landing(SurfaceUnderfoot()), 0.25f + 0.75f * hard, 1.15f - 0.35f * hard);
        SteeringWheel.Knock(hard * 0.7f);
        AddTrauma(hard * 0.65f);

        // the FOV dips and springs back: FootPlayer eases its FOV every frame, so a nudge here
        // is undone on its own
        if (_player.Camera is { } cam) cam.Fov -= 6f * hard;

        if (fall > 4f) Puff(_player.Ride == RideKind.Skis ? Snow : Dirt, (int)(10 + 30 * hard));
    }

    private void UpdateAir(float dt, bool grounded)
    {
        // on foot only: time spent flying a plane is not a jump
        // and neither is being thrown through a windscreen (#214)
        // nor is swimming (#301)
        if (_player.Ride != RideKind.OnFoot || _player.Ragdolled || _player.IsSwimming) _airTime = 0;
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

        // white smoke off the rear tyres once they are properly sliding
        float tyreSlide = _player.Vehicle is Car car ? car.TyreSlide : 0f;
        _smoke.Emitting = grounded && tyreSlide > 0.35f;
        if (_smoke.Emitting) _smoke.AmountRatio = Mathf.Clamp((tyreSlide - 0.3f) / 0.7f, 0.15f, 1f);

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

        _rpmBar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = false,
            CustomMinimumSize = new Vector2(220, 8),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _rpmBar.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _rpmBar.OffsetLeft = -110; _rpmBar.OffsetRight = 110;
        _rpmBar.OffsetTop = -14; _rpmBar.OffsetBottom = -6;
        _rpmFill = new StyleBoxFlat { BgColor = new Color(1f, 0.85f, 0.25f) };
        _rpmBar.AddThemeStyleboxOverride("fill", _rpmFill);
        _rpmBar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.5f) });
        _screen.AddChild(_rpmBar);

        _driftLabel = HudLabel(30);
        _driftLabel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _driftLabel.GrowHorizontal = Control.GrowDirection.Both;
        _driftLabel.OffsetTop = 150;
        _driftLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.9f, 1f));
        _driftLabel.Visible = false;
        _screen.AddChild(_driftLabel);

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

        // the air reserve (#301), just above health: only while it is not full
        _airBar = new ProgressBar
        {
            MinValue = 0, MaxValue = FootPlayer.AirMax, Step = 0.01, ShowPercentage = false,
            MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
        };
        _airBar.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _airBar.OffsetLeft = 18; _airBar.OffsetRight = 218;
        _airBar.OffsetTop = -44; _airBar.OffsetBottom = -34;
        _airFill = new StyleBoxFlat { BgColor = new Color(0.35f, 0.75f, 1f) };
        _airBar.AddThemeStyleboxOverride("fill", _airFill);
        _airBar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.5f) });
        _screen.AddChild(_airBar);

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

    private readonly System.Text.StringBuilder _wear = new(), _speedText = new(), _engineText = new();
    private string _speedShown = "", _engineShown = "";

    /// <summary>Pushes <paramref name="sb"/> to the label only when it differs from what it shows.</summary>
    private static void SetText(Label label, System.Text.StringBuilder sb, ref string shown)
    {
        if (sb.Equals(shown.AsSpan())) return;
        shown = sb.ToString();
        label.Text = shown;
    }

    private static void AppendRetarder(System.Text.StringBuilder sb, Truck t)
    {
        int level = t.Box.RetarderLevel;
        if (level == 1) sb.Append("    EXH");
        else if (level > 1) sb.Append("    RET ").Append(level - 1);
    }

    private void UpdateHud(float dt, RideKind ride, float speed)
    {
        // mounted only: on foot the pace is the walk, and a number would be clutter; in the
        // cockpit the dashboard shows it, unless the setting wants it here too
        bool dash = _player.InCockpit && !Core.GameSettings.Current.CockpitHud;
        // Built into reused builders and turned into a string only when the text changed (#221):
        // the readouts are rounded, so most frames print the same thing as the last one.
        var wear = _wear.Clear();
        if (_player.Vehicle is Car worn)
        {
            if (Core.GameSettings.Current.TyreWear)
                wear.Append($"    tyres F {(1f - worn.TyreWearFront) * 100:0}% R {(1f - worn.TyreWearRear) * 100:0}%");
            if (Core.GameSettings.Current.BrakeWear)
                wear.Append($"    brakes {worn.BrakeTemp:0}°C").Append(worn.BrakeFactor < 0.95f ? " FADE" : "");
        }
        else if (_player.Vehicle is Truck heavy)
        {
            // a truck's dash has its air gauge and lamps, but no stage number, hold or weight
            AppendRetarder(wear, heavy);
            if (!heavy.Box.SpringBrakes && heavy.HillHold) wear.Append("    HOLD");
            if (heavy.Box.ClutchPedal > 0.5f) wear.Append("    CLUTCH");
            wear.Append($"    {heavy.Train.Mass / 1000f:0.0} t");
        }
        // a passenger (#158): the vehicle's speed, and the wheel when nobody holds it
        var carrier = _player.Host;
        bool aboard = carrier != null || _player.RollingDriverless;
        // the dashboard has no tyre or brake gauges: those stay on the HUD
        _speedLabel.Visible = ride != RideKind.OnFoot && (!dash || wear.Length > 0) || aboard;
        if (_speedLabel.Visible)
        {
            var sb = _speedText.Clear();
            if (aboard)
                sb.Append($"{(carrier?.WorldVelocity.Length() ?? speed) * 3.6f:0} km/h    ")
                  .Append(carrier is { SeatIndex: 0 } ? "passenger" : Core.InputHints.Format("nobody at the wheel: {take_wheel} takes it"));
            else if (dash)
            {
                int lead = 0;
                while (lead < wear.Length && char.IsWhiteSpace(wear[lead])) lead++;
                sb.Append(wear, lead, wear.Length - lead);
            }
            else if (_player.IsFlying)
            {
                sb.Append($"{speed * 3.6f:0} km/h    {_player.Clearance:0} m");
                if (ride == RideKind.Plane) sb.Append($"    {_player.Flight.Control * 100:0}%");
            }
            else if (_player.Vehicle is Truck t)
            {
                sb.Append($"{speed * 3.6f:0} km/h    {t.GearLabel}    {t.Rpm:0} rpm");
                AppendRetarder(sb, t);
                sb.Append($"    AIR {t.Box.AirTank:0.0} bar").Append(t.Box.AirTank < HeavyDriveline.AirLow ? " LOW" : "");
                sb.Append(t.Box.SpringBrakes ? "    PARK" : t.HillHold ? "    HOLD" : "");
                if (t.Box.ClutchPedal > 0.5f) sb.Append("    CLUTCH");
                sb.Append($"    {t.Train.Mass / 1000f:0.0} t");
            }
            else if (_player.Vehicle is Car c)
            {
                sb.Append($"{speed * 3.6f:0} km/h    ");
                if (c.Gear < 0) sb.Append('R'); else sb.Append(c.Gear);
                sb.Append($"    {c.Rpm:0} rpm").Append(wear);
            }
            else if (_player.Vehicle is Steamer steamer)
            {
                // the bridge: the log in knots, the telegraph's order, the shaft
                var s = steamer.State;
                sb.Append($"{speed / 0.5144f:0.0} kn  {speed * 3.6f:0} km/h    ").Append(Telegraph.Name(steamer.Order));
                sb.Append($"    {steamer.Rpm:0} rpm");
                if (s.Shaft < -0.01f) sb.Append(" ASTERN");
                if (s.Grounded) sb.Append("    AGROUND");
                if (steamer.DoorsOpen != 0) sb.Append("    GANGWAY OPEN");
            }
            else if (_player.Vehicle is Boat boat)
            {
                // a boat's log reads knots; km/h beside it, the engine, and what the hull is doing
                var s = boat.State;
                sb.Append($"{speed / 0.5144f:0} kn  {speed * 3.6f:0} km/h    {boat.Rpm:0} rpm");
                if (s.Gear < 0 && boat.Throttle > 0.02f) sb.Append("    ASTERN");
                if (s.Airborne > 0.15f) sb.Append("    AIR");
                else if (s.Grounded) sb.Append("    AGROUND");
                else if (boat.Spec.LiftShare > 0f && boat.Spec.Planing(s.WaterSpeed) > 0.8f) sb.Append("    PLANING");
            }
            else if (_player.Vehicle is IEngined e)
                sb.Append($"{speed * 3.6f:0} km/h    {e.Gear}    {e.Rpm:0} rpm");
            else
                sb.Append($"{speed * 3.6f:0} km/h");
            SetText(_speedLabel, sb, ref _speedShown);
        }

        // the rev counter, amber turning red toward the limit
        _rpmBar.Visible = _player.Vehicle is IEngined && !dash;
        if (_player.Vehicle is IEngined rev)
        {
            _rpmBar.Value = rev.Rpm01;
            _rpmFill.BgColor = new Color(1f, 0.85f, 0.25f).Lerp(new Color(1f, 0.2f, 0.15f), Mathf.Clamp((rev.Rpm01 - 0.8f) / 0.15f, 0f, 1f));
        }

        // health only when it is not full: a bar that is always full is clutter
        _healthBar.Visible = _player.Health < FootPlayer.MaxHealth - 0.5f;
        _healthBar.Value = _player.Health;
        // air: shown while it is not full, blinking once it runs low
        float air = _player.Air;
        _airBar.Visible = air < FootPlayer.AirMax - 0.05f;
        if (_airBar.Visible)
        {
            _airBar.Value = air;
            bool low = air < FootPlayer.AirMax * 0.25f;
            var colour = low && Mathf.PosMod(_time * 3f, 1f) < 0.5f ? new Color(1f, 0.35f, 0.25f) : new Color(0.35f, 0.75f, 1f);
            if (_airFill.BgColor != colour) _airFill.BgColor = colour;
        }
        _hurtFlash.Color = _hurtFlash.Color with { A = Mathf.MoveToward(_hurtFlash.Color.A, 0f, 1.2f * dt) };

        var vehicle = _player.Vehicle;
        _engineLabel.Visible = vehicle is { IsVehicle: true };
        if (_engineLabel.Visible)
        {
            var sb = _engineText.Clear();
            if (vehicle!.HasEngine)
                sb.Append(_player.EngineOn ? "ENGINE ON  " : "ENGINE OFF  ").Append('[').Append(Core.InputHints.Label(Core.PlayerInput.EngineToggle)).Append("]\n");
            sb.Append($"DAMAGE {100f - _player.VehicleHealth / vehicle.MaxHealth * 100f:0}%    [{Core.InputHints.Label(Core.PlayerInput.InteractMount)}] get out");
            SetText(_engineLabel, sb, ref _engineShown);
        }

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
            case RideKind.OnFoot when _player.IsSwimming:
                // the first moments in the water (#301): how to go down and up
                if (_player.SwimTime < 5f) text = InputHints.Format("{crouch_slide}  dive     {jump}  up · climb out");
                break;
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
                    + $"\n{(pad ? "left stick" : "W / S")} dive · flare     {(pad ? "left stick" : "A / D")} turn · {(pad ? "look" : "mouse")} leans";
                break;
            case RideKind.Parachute:
                text = $"{(pad ? "left stick" : "A / D")} steer · {(pad ? "look" : "mouse")} leans     {(pad ? "pull back" : "S")} brake — hold it to flare the landing";
                break;
            case var _ when _player.Heavy is { } truck && truck.Trailer == null && _player.GroundSpeed < 1.5f
                && _player.CoupleCandidate(truck) != null:
                // the hitch is under a trailer's pivot: say so, in the device's own key
                text = InputHints.Format("{couple}  COUPLE the trailer");
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
