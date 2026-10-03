using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace UnitSport.Core;

/// <summary>How finely the inner LOD rings and their roads/buildings are drawn.</summary>
public enum DetailPreset
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>How the game window occupies the screen.</summary>
public enum WindowMode
{
    Windowed = 0,
    Borderless = 1,
    Fullscreen = 2,
}

/// <summary>What the on-screen performance overlay shows (F3 cycles it).</summary>
public enum PerfOverlayMode
{
    Off = 0,
    Fps = 1,
    Detailed = 2,
}

/// <summary>
/// Player-tunable rendering and streaming settings, persisted to <c>user://settings.json</c>.
///
/// <para>
/// One static instance, because the things it drives — the chunk manager's ring table, every
/// world material's fog uniforms, the cameras' far plane — are themselves singletons of the
/// running world, and each subscribes to <see cref="Changed"/> rather than being handed a copy
/// at construction. Command-line overrides (<c>--rings</c>, <c>--horizon</c>, <c>--fog</c>,
/// <c>--detail</c>) apply on top of the file for one session and are never written back, so a
/// verification run cannot quietly change what the player sees next time.
/// </para>
/// </summary>
/// <summary>
/// How vehicles and running are tuned. Game is the arcade layer on top of the physics; Sim is the
/// untouched real-world model, kept because a home trainer's watts only mean anything in it.
/// </summary>
public enum RideProfile
{
    Game,
    Sim,
}

public sealed class GameSettings
{
    private const string File = "user://settings.json";

    public static GameSettings Current { get; private set; } = new();

    /// <summary>Raised after any live change so the world can re-apply itself.</summary>
    public static event Action? Changed;

    /// <summary>Tile rings of real terrain around each anchor (Chebyshev radius in km).</summary>
    public int RenderDistanceRings { get; set; } = 15;
    public const int MinRings = 6, MaxRings = 40;

    /// <summary>How far the 100 m horizon lattice is drawn, in km. 0 turns it off.</summary>
    public int HorizonKm { get; set; } = 60;
    public const int MaxHorizonKm = 150;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DetailPreset Detail { get; set; } = DetailPreset.Medium;

    /// <summary>Distance fog in the world shaders. Off by default: the horizon is the point.</summary>
    public bool Fog { get; set; }

    /// <summary>
    /// Generate the terrain there is no data for, blended into the real tiles beside it
    /// (<see cref="Terrain.FallbackChunkSource"/>). Off leaves void past the real region, and
    /// no world at all on a copy with no terrain.
    /// </summary>
    public bool GeneratedFill { get; set; } = true;

    /// <summary>Tile builds allowed in flight at once; 0 picks by whether a server is involved.</summary>
    public int MaxConcurrentBuilds { get; set; }
    public const int MaxBuildsCap = 32;

    /// <summary>Main-thread milliseconds per frame spent turning built tiles into Godot meshes.</summary>
    public double CommitBudgetMs { get; set; } = 4;

    /// <summary>
    /// Viewport 3D scale, the biggest single fidelity/performance knob: the 3D is drawn at this
    /// fraction of the window's real pixels (<c>stretch/mode = "canvas_items"</c>), so 1 is native
    /// and above 1 supersamples; in PS1, a fraction of the UI canvas instead, so its low resolution
    /// holds on any screen (<see cref="DisplaySettings.EffectiveScale"/>). The UI lays out at a fixed
    /// <see cref="BaseWidth"/>x<see cref="BaseHeight"/> content scale and is untouched by it.
    /// </summary>
    public float RenderScale { get; set; } = 0.75f;
    public const int BaseWidth = 1152, BaseHeight = 648;
    public const float MinRenderScale = 0.25f, MaxRenderScale = 2f;

    public bool VSync { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PerfOverlayMode PerfOverlay { get; set; } = PerfOverlayMode.Off;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WindowMode WindowMode { get; set; } = WindowMode.Windowed;

    // --- controls ---
    /// <summary>Right-stick look speed multiplier; 1 turns at <see cref="PlayerInput.StickTurnRate"/>.</summary>
    public float StickSensitivity { get; set; } = 1f;
    public bool InvertY { get; set; }

    /// <summary>Stick travel ignored around centre. Worn pads drift, so it is a setting.</summary>
    public float StickDeadzone { get; set; } = 0.18f;

    /// <summary>Controller rumble on landings, impacts and speed.</summary>
    public bool Vibration { get; set; } = true;

    /// <summary>Steering wheel, pedals and their bindings (<see cref="SteeringWheel"/>).</summary>
    public WheelSettings Wheel { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RideProfile RideProfile { get; set; } = RideProfile.Game;
    /// <summary>Cars' tyres wear with the sliding they do and lose grip (off by default).</summary>
    public bool TyreWear { get; set; }
    /// <summary>Cars' brakes heat up and fade, and their pads wear (off by default).</summary>
    public bool BrakeWear { get; set; }
    /// <summary>
    /// E from outside a parked walkable vehicle (a bus, a coach, the steamer: anything with decks)
    /// boards it and puts you at its wheel (on, the default, #384). Off: E does nothing there; you walk
    /// aboard by a door, a gangway or a ladder and take the wheel from inside. Other vehicles keep E.
    /// </summary>
    public bool BoardWalkableFromOutside { get; set; } = true;
    /// <summary>
    /// How airliners fly (#414, #415): Arcade (protections on every type, wings level when the stick is
    /// let go, ready to taxi) or Light sim (engine start, autopilot, trim, fuel; conventional types can stall).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Player.AirlinerHandling Airliner { get; set; } = Player.AirlinerHandling.Arcade;
    /// <summary>How trucks and buses are shifted (#70): automatic, sequential, with the clutch, H-pattern.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Player.HeavyShift HeavyGearbox { get; set; } = Player.HeavyShift.Automatic;

    // --- world ---
    /// <summary>Real minutes for a whole day; 0 stops the clock at <see cref="StartHour"/>.</summary>
    public float DayLengthMinutes { get; set; } = 24f;
    public float StartHour { get; set; } = 10f;

    /// <summary>Cars around the player in daytime (about half at night); 0 turns traffic off.</summary>
    public int TrafficCars { get; set; } = 35;
    public bool Trains { get; set; } = true;

    /// <summary>
    /// Per occasion id: follow the calendar, hide its look, or force it (<see cref="Occasions.OccasionManager"/>).
    /// Absent means Auto.
    /// </summary>
    public Dictionary<string, UnitSport.Occasions.OccasionPreference> OccasionPreferences { get; set; } = new();

    // --- feel ---
    /// <summary>Everything the game plays, 0..1 — the Master bus (see <see cref="Audio.SfxBus.ApplyVolumes"/>).</summary>
    public float MasterVolume { get; set; } = 0.5f;
    /// <summary>Sound effects volume, 0..1 — the Sfx bus. Sliders are perceptual, not linear.</summary>
    public float SfxVolume { get; set; } = 0.5f;
    /// <summary>Ambience volume, 0..1.</summary>
    public float AmbienceVolume { get; set; } = 0.7f;
    /// <summary>Music volume, 0..1 — the Music bus: radios, car stereos, live stations (#261).</summary>
    public float MusicVolume { get; set; } = 0.7f;

    /// <summary>Which sound chip the engines are rendered as (<see cref="Audio.EngineSynth"/>).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Audio.EngineVoice EngineVoice { get; set; } = Audio.EngineVoice.Ps1;

    /// <summary>How the world looks (<see cref="Styles.StyleKit"/>). Client-only, never replicated.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Styles.VisualStyle VisualStyle { get; set; } = Styles.VisualStyle.Ps1;

    /// <summary>Camera shake strength, 0 (off) .. 1.</summary>
    public float ScreenShake { get; set; } = 1f;

    /// <summary>Radial streaks at the screen edge at speed.</summary>
    public bool SpeedLines { get; set; } = true;

    /// <summary>Over-the-shoulder view on foot and a chase view mounted; V / R3 toggles it in game.</summary>
    public bool ThirdPerson { get; set; } = true;

    // --- network ---
    /// <summary>List the dedicated servers found on the LAN over mDNS in the main menu (<see cref="Net.LanDiscovery"/>).</summary>
    public bool LanDiscovery { get; set; } = true;

    /// <summary>The server last joined from the menu, so the field is not reset to localhost every launch.</summary>
    public string LastHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// The name asked for when joining a server, set the first time the Multiplayer screen opens.
    /// Empty until then; <c>--name</c> overrides it for one run without saving.
    /// </summary>
    public string PlayerName { get; set; } = "";

    /// <summary>
    /// The player's figure (#394): <see cref="Avatar.Appearance.Pack"/>ed, 0 until one is chosen in
    /// the inventory's Body row (till then the figure comes from the player's network id).
    /// </summary>
    public int AppearanceBits { get; set; }

    /// <summary>GPX files replayed recently, newest first (the Play solo track picker lists them).</summary>
    public List<string> RecentGpx { get; set; } = new();

    /// <summary>
    /// Play in a VR headset (#186, OpenXR, a Quest over Link). OpenXR only starts with the engine,
    /// so turning this on or off relaunches the game (<see cref="XR.XrSession.Relaunch"/>), and a
    /// launch from the title with it on relaunches itself into VR.
    /// </summary>
    public bool VrMode { get; set; }

    /// <summary>What the monitor shows while in VR (<see cref="XR.XrMonitor"/>); F7 cycles it.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public XR.MonitorView VrMonitor { get; set; } = XR.MonitorView.FirstPerson;

    // --- the headset's picture (#244): what a streamed headset (Air Link) needs ---
    /// <summary>
    /// Headset MSAA samples: 0, 2, 4 or 8. Aliased edges shimmer, and shimmer is what the Link
    /// video encoder turns into blocks.
    /// </summary>
    public int VrMsaa { get; set; } = 4;
    /// <summary>The headset's 3D resolution, relative to the eye size the runtime asks for.</summary>
    public float VrRenderScale { get; set; } = 1f;
    public const float MinVrRenderScale = 0.5f, MaxVrRenderScale = 1.5f;
    /// <summary>Foveated rendering: coarser shading towards the edge of each eye (variable rate shading).</summary>
    public bool VrFoveation { get; set; } = true;

    // --- cockpit: first person at the wheel of a car (#69) ---
    /// <summary>Your own arms and legs at the wheel. V cycles chase → cockpit with them → cockpit without.</summary>
    public bool CockpitBody { get; set; } = true;
    /// <summary>Working rear-view and door mirrors: each a small extra render of the world.</summary>
    public bool CockpitMirrors { get; set; } = true;
    /// <summary>Speed, gear and rpm on the HUD in the cockpit too; off, the dashboard shows them.</summary>
    public bool CockpitHud { get; set; }
    /// <summary>Vertical field of view from the driver's seat, degrees.</summary>
    public float CockpitFov { get; set; } = 70f;
    /// <summary>The eye moved up (+) or down, and forward (+) or back, from where the seat puts it, m.</summary>
    public float SeatHeight { get; set; }
    public float SeatForward { get; set; }
    /// <summary>The head sways with the car's accelerations: back under power, forward braking, out in a bend.</summary>
    public bool CockpitHeadMotion { get; set; } = true;

    /// <summary>Window size when windowed; 0 leaves whatever size the window already has.</summary>
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    /// <summary>Camera far plane that keeps the whole horizon in view, with room to spare.</summary>
    [JsonIgnore]
    public float CameraFar => Math.Max(20_000f, (HorizonKm + 10) * 1000f);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Reads the file (if any), then the command line. Call once at boot.</summary>
    public static void Load()
    {
        var loaded = new GameSettings();
        try
        {
            if (Godot.FileAccess.FileExists(File))
            {
                using var file = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Read);
                string text = file.GetAsText();
                loaded = JsonSerializer.Deserialize<GameSettings>(text, JsonOptions) ?? loaded;
                // before #261 the radios' volume lived in radio.cfg: carried over once
                if (!text.Contains("\"musicVolume\"", StringComparison.OrdinalIgnoreCase) && OldRadioVolume() is float old)
                    loaded.MusicVolume = old;
            }
            else if (OldRadioVolume() is float old) loaded.MusicVolume = old;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[settings] could not read {File}: {e.Message}");
        }

        loaded.Clamp();
        loaded.ApplyCommandLine(CmdArgs.All);
        Current = loaded;
        GD.Print($"[settings] rings={loaded.RenderDistanceRings} horizon={loaded.HorizonKm}km "
            + $"detail={loaded.Detail} fog={loaded.Fog} builds={loaded.MaxConcurrentBuilds} "
            + $"commit={loaded.CommitBudgetMs}ms scale={loaded.RenderScale} vsync={loaded.VSync} "
            + $"window={loaded.WindowMode} perf={loaded.PerfOverlay}");
    }

    public void Save()
    {
        try
        {
            JsonStore.Save(File, this, JsonOptions);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[settings] could not write {File}: {e.Message}");
        }
    }

    /// <summary>The radio panel's volume as it was kept before the Music bus (#261), if that file is there.</summary>
    private static float? OldRadioVolume()
    {
        var cfg = new ConfigFile();
        return cfg.Load("user://radio.cfg") == Error.Ok ? Math.Clamp(cfg.GetValue("radio", "volume", 0.7f).AsSingle(), 0f, 1f) : null;
    }

    /// <summary>
    /// Writes one setting into the file without the rest of this run's values (a command-line
    /// <c>--view</c> or <c>--traffic</c> must not become the saved choice): the radio panel's
    /// volume slider, saved as it is dragged.
    /// </summary>
    public static void SaveOnly(string key, float value)
    {
        try
        {
            var root = Godot.FileAccess.FileExists(File)
                ? System.Text.Json.Nodes.JsonNode.Parse(Godot.FileAccess.GetFileAsString(File)) as System.Text.Json.Nodes.JsonObject
                : null;
            root ??= System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new GameSettings(), JsonOptions)) as System.Text.Json.Nodes.JsonObject;
            if (root == null) return;
            string name = JsonOptions.PropertyNamingPolicy?.ConvertName(key) ?? key;
            foreach (var existing in root.Select(kv => kv.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
                root.Remove(existing);
            root[name] = value;
            JsonStore.Save(File, root, JsonOptions);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[settings] could not write {File}: {e.Message}");
        }
    }

    /// <summary>Applies a change made in the UI: clamps, notifies the world, persists.</summary>
    public void Commit()
    {
        Clamp();
        Changed?.Invoke();
        Save();
    }

    private void Clamp()
    {
        RenderDistanceRings = Math.Clamp(RenderDistanceRings, MinRings, MaxRings);
        HorizonKm = Math.Clamp(HorizonKm, 0, MaxHorizonKm);
        MaxConcurrentBuilds = Math.Clamp(MaxConcurrentBuilds, 0, MaxBuildsCap);
        CommitBudgetMs = Math.Clamp(CommitBudgetMs, 1, 16);
        RenderScale = Math.Clamp(RenderScale, MinRenderScale, MaxRenderScale);
        VrMsaa = VrMsaa switch { <= 0 => 0, <= 2 => 2, <= 4 => 4, _ => 8 };
        VrRenderScale = Math.Clamp(VrRenderScale, MinVrRenderScale, MaxVrRenderScale);
        StickSensitivity = Math.Clamp(StickSensitivity, 0.2f, 3f);
        MasterVolume = Math.Clamp(MasterVolume, 0f, 1f);
        SfxVolume = Math.Clamp(SfxVolume, 0f, 1f);
        AmbienceVolume = Math.Clamp(AmbienceVolume, 0f, 1f);
        MusicVolume = Math.Clamp(MusicVolume, 0f, 1f);
        DayLengthMinutes = Math.Clamp(DayLengthMinutes, 0f, 240f);
        StartHour = Math.Clamp(StartHour, 0f, 23.99f);
        TrafficCars = Math.Clamp(TrafficCars, 0, 150);
        ScreenShake = Math.Clamp(ScreenShake, 0f, 1f);
        StickDeadzone = Math.Clamp(StickDeadzone, 0.05f, 0.5f);
        CockpitFov = Math.Clamp(CockpitFov, 50f, 100f);
        SeatHeight = Math.Clamp(SeatHeight, -0.1f, 0.1f);
        SeatForward = Math.Clamp(SeatForward, -0.15f, 0.15f);
        WindowWidth = Math.Clamp(WindowWidth, 0, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 0, 4320);
        OccasionPreferences ??= new();
        RecentGpx ??= new();
        PlayerName ??= "";
        Wheel ??= new();
        Wheel.Clamp();
    }

    /// <summary>
    /// "--rings N", "--horizon km", "--fog on|off", "--detail low|medium|high",
    /// "--generated on|off", "--style ps1|cartoon|real-|real+" — for
    /// screenshotting one configuration against another without touching the saved file.
    /// </summary>
    private void ApplyCommandLine(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            string v = args[i + 1];
            switch (args[i])
            {
                case "--rings" when int.TryParse(v, out int r): RenderDistanceRings = r; break;
                case "--horizon" when int.TryParse(v, out int h): HorizonKm = h; break;
                case "--fog": Fog = v != "off" && v != "0" && v != "false"; break;
                case "--generated": GeneratedFill = v != "off" && v != "0" && v != "false"; break;
                case "--detail" when Enum.TryParse<DetailPreset>(v, true, out var d): Detail = d; break;
                case "--builds" when int.TryParse(v, out int b): MaxConcurrentBuilds = b; break;
                case "--commit" when double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double c):
                    CommitBudgetMs = c; break;
                case "--profile" when Enum.TryParse<RideProfile>(v, true, out var rp): RideProfile = rp; break;
                case "--tyrewear": TyreWear = v is "on" or "1" or "true"; break;
                case "--brakewear": BrakeWear = v is "on" or "1" or "true"; break;
                case "--boardwalkable": BoardWalkableFromOutside = v is "on" or "1" or "true"; break;
                case "--airliner" when Enum.TryParse<Player.AirlinerHandling>(v, true, out var ah): Airliner = ah; break;
                case "--gearbox":
                    HeavyGearbox = v.ToLowerInvariant() switch
                    {
                        "seq" => Player.HeavyShift.Sequential, "seqclutch" => Player.HeavyShift.SequentialClutch,
                        "hsplit" => Player.HeavyShift.HPatternSplitter, "h" => Player.HeavyShift.HPattern,
                        _ => Player.HeavyShift.Automatic,
                    };
                    break;
                case "--wheel": Wheel.Enabled = v is "on" or "1" or "true"; break;
                case "--wheelrange" when float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float deg):
                    Wheel.RangeDeg = deg; break;
                case "--voice":
                    EngineVoice = v.ToLowerInvariant() switch
                    {
                        "ps1" => Audio.EngineVoice.Ps1, "nes" => Audio.EngineVoice.Nes,
                        "sid" => Audio.EngineVoice.Sid, "genesis" => Audio.EngineVoice.Genesis,
                        _ => Audio.EngineVoice.Realistic,
                    };
                    break;
                case "--style":
                    Styles.StyleKit.TryParse(v, out var style);
                    VisualStyle = style;
                    break;
                // a fixed time of day, for screenshots: --time 21.5 is half past nine at night
                case "--time" when float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float hour):
                    StartHour = hour; DayLengthMinutes = 0; break;
                case "--traffic" when int.TryParse(v, out int cars): TrafficCars = cars; break;
                // first | third, or in a car's cockpit with (body) or without (bare) your own figure
                case "--view":
                    ThirdPerson = v is not ("first" or "1st" or "body" or "bare");
                    if (v is "body" or "bare") CockpitBody = v == "body";
                    break;
                case "--mirrors": CockpitMirrors = v is "on" or "1" or "true"; break;
                // what the monitor shows in VR (#186): off | first | eyes | third
                case "--vrmonitor":
                    VrMonitor = v switch { "off" => XR.MonitorView.Off, "eyes" => XR.MonitorView.BothEyes,
                        "third" => XR.MonitorView.ThirdPerson, _ => XR.MonitorView.FirstPerson };
                    break;
                case "--vsync": VSync = v is "on" or "1" or "true"; break;
                case "--perf":
                    PerfOverlay = v switch { "full" or "detailed" => PerfOverlayMode.Detailed,
                        "fps" => PerfOverlayMode.Fps, _ => PerfOverlayMode.Off };
                    break;
            }
        }
        Clamp();
    }
}
