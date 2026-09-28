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

    /// <summary>Tile builds allowed in flight at once; 0 picks by whether a server is involved.</summary>
    public int MaxConcurrentBuilds { get; set; }
    public const int MaxBuildsCap = 32;

    /// <summary>Main-thread milliseconds per frame spent turning built tiles into Godot meshes.</summary>
    public double CommitBudgetMs { get; set; } = 4;

    /// <summary>
    /// Viewport 3D scale, the biggest single fidelity/performance knob. The game lays out at a
    /// fixed <see cref="BaseWidth"/>x<see cref="BaseHeight"/> (<c>stretch/mode = "viewport"</c>)
    /// and the 3D is drawn at this fraction of it, so this IS the 3D render resolution; above 1
    /// it supersamples. The UI is untouched, which is why resolution is not changed through the
    /// root's content scale size: that would resize every HUD element with it.
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

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RideProfile RideProfile { get; set; } = RideProfile.Game;

    // --- feel ---
    /// <summary>Sound effects volume, 0..1.</summary>
    public float SfxVolume { get; set; } = 0.8f;

    /// <summary>Camera shake strength, 0 (off) .. 1.</summary>
    public float ScreenShake { get; set; } = 1f;

    /// <summary>Radial streaks at the screen edge at speed.</summary>
    public bool SpeedLines { get; set; } = true;

    /// <summary>Over-the-shoulder view on foot and a chase view mounted; V / R3 toggles it in game.</summary>
    public bool ThirdPerson { get; set; } = true;

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
                loaded = JsonSerializer.Deserialize<GameSettings>(file.GetAsText(), JsonOptions) ?? loaded;
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"[settings] could not read {File}: {e.Message}");
        }

        loaded.Clamp();
        loaded.ApplyCommandLine(OS.GetCmdlineUserArgs());
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
            using var file = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Write);
            file.StoreString(JsonSerializer.Serialize(this, JsonOptions));
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
        StickSensitivity = Math.Clamp(StickSensitivity, 0.2f, 3f);
        SfxVolume = Math.Clamp(SfxVolume, 0f, 1f);
        ScreenShake = Math.Clamp(ScreenShake, 0f, 1f);
        StickDeadzone = Math.Clamp(StickDeadzone, 0.05f, 0.5f);
        WindowWidth = Math.Clamp(WindowWidth, 0, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 0, 4320);
    }

    /// <summary>
    /// "--rings N", "--horizon km", "--fog on|off", "--detail low|medium|high" — for
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
                case "--detail" when Enum.TryParse<DetailPreset>(v, true, out var d): Detail = d; break;
                case "--builds" when int.TryParse(v, out int b): MaxConcurrentBuilds = b; break;
                case "--commit" when double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double c):
                    CommitBudgetMs = c; break;
                case "--profile" when Enum.TryParse<RideProfile>(v, true, out var rp): RideProfile = rp; break;
                case "--view": ThirdPerson = v != "first" && v != "1st"; break;
                case "--perf":
                    PerfOverlay = v switch { "full" or "detailed" => PerfOverlayMode.Detailed,
                        "fps" => PerfOverlayMode.Fps, _ => PerfOverlayMode.Off };
                    break;
            }
        }
        Clamp();
    }
}
