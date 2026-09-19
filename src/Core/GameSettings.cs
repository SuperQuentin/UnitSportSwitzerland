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

    /// <summary>Viewport 3D scale, the biggest single fidelity/performance knob.</summary>
    public float RenderScale { get; set; } = 0.75f;

    public bool VSync { get; set; } = true;

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
            + $"commit={loaded.CommitBudgetMs}ms scale={loaded.RenderScale} vsync={loaded.VSync}");
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
        RenderScale = Math.Clamp(RenderScale, 0.35f, 1f);
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
            }
        }
        Clamp();
    }
}
