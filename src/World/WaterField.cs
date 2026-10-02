using Godot;
using UnitSport.Terrain;

namespace UnitSport.World;

/// <summary>
/// The water, for everything that needs it (#299): swimmers, boats, birds, audio, loot, vehicles.
/// Static, one per process, no node: it reads the loaded tiles' still water
/// (<see cref="ChunkManager.TryGetWater"/>) and adds the deterministic waves of
/// <see cref="WaveSpectrum"/> on the synchronized server clock, so every peer, headless ones
/// included, gets the same surface with nothing replicated but <see cref="SeaState"/>. The shader
/// draws the same function from the globals <see cref="PushGlobals"/> writes.
///
/// <para>
/// World positions go in and out in the current frame: the waves are computed from the LV95
/// position (modulo 9.6 km), so the floating origin moving changes nothing.
/// </para>
///
/// <para>
/// API contract for #301 (swimming) and #302 (boats): <see cref="TryLevelAt"/>,
/// <see cref="IsUnderwater"/>, <see cref="Height"/>, <see cref="Normal"/>, <see cref="Velocity"/>,
/// <see cref="SeaState"/>, <see cref="Now"/>. Where there is no water (or its tile is not loaded)
/// the queries say so (false, 0, up, zero).
/// </para>
/// </summary>
public static class WaterField
{
    private static ChunkManager? _chunks;
    private static float _seaState;
    private static readonly float[] Amp = new float[WaveSpectrum.Count];

    /// <summary>Fired on the main thread when <see cref="SeaState"/> changes.</summary>
    public static event Action<float>? SeaStateChanged;

    static WaterField() => WaveSpectrum.Amplitudes(0f, Amp);

    /// <summary>
    /// 0 calm (a few centimetres of ripple) .. 0.35 chop .. 0.7 storm .. 1 gamey (a 1.5-2 m swell on
    /// a big lake). Server state: set by <c>/seastate</c> or <c>--sea-state</c> and replicated on join
    /// and on change (<c>Net/ChatManager</c>); later the wind (#304) drives it.
    /// </summary>
    public static float SeaState => _seaState;

    /// <summary>Sets the sea state here (the server's replication calls this on every peer).</summary>
    public static void SetSeaState(float state)
    {
        state = Math.Clamp(float.IsFinite(state) ? state : 0f, 0f, 1f);
        if (state == _seaState) return;
        _seaState = state;
        WaveSpectrum.Amplitudes(state, Amp);
        PushGlobals();
        SeaStateChanged?.Invoke(state);
    }

    /// <summary>The tiles whose water is queried; null when there are none (a flat test world, teardown).</summary>
    public static void Bind(ChunkManager? chunks) => _chunks = chunks;

    /// <summary>The wave clock now: the synchronized server time modulo <see cref="WaveSpectrum.LoopS"/>.</summary>
    public static double Now => WaveSpectrum.WaveTime(Net.ClockSync.ServerNow);

    /// <summary>The current amplitudes of the waves (read-only view, for probes).</summary>
    public static ReadOnlySpan<float> Amplitudes => Amp;

    // ---- the still water -------------------------------------------------------------------

    /// <summary>The still level and wave scale (0..1) at a point; false where there is no water.</summary>
    public static bool TryGetStill(Vector3 world, out float still, out float scale)
    {
        still = 0f;
        scale = 0f;
        return _chunks != null && _chunks.TryGetWater(world, out still, out scale);
    }

    // ---- the surface -----------------------------------------------------------------------

    /// <summary>
    /// The water surface's altitude above (x, z) now: the still level plus the waves. False where
    /// there is no water. This is what a hull floats on and a swimmer's head clears.
    /// </summary>
    public static bool TryLevelAt(Vector3 world, out float level)
    {
        if (!TryGetStill(world, out level, out float scale)) return false;
        level += (float)HeightOver(world.X, world.Z, Now, scale);
        return true;
    }

    /// <summary>Whether a point is under the water surface (waves included).</summary>
    public static bool IsUnderwater(Vector3 world) => TryLevelAt(world, out float level) && world.Y < level;

    /// <summary>
    /// How far a point is under the surface, metres (negative above it); <see cref="float.NaN"/>
    /// where there is no water.
    /// </summary>
    public static float Submersion(Vector3 world) =>
        TryLevelAt(world, out float level) ? level - world.Y : float.NaN;

    /// <summary>The waves' height above the still level over world (x, z) at wave time t; 0 with no water.</summary>
    public static float Height(float x, float z, double t)
    {
        var p = new Vector3(x, 0, z);
        return TryGetStill(p, out _, out float scale) ? (float)HeightOver(x, z, t, scale) : 0f;
    }

    /// <summary>The surface normal over world (x, z) at wave time t (up with no water).</summary>
    public static Vector3 Normal(float x, float z, double t)
    {
        var p = new Vector3(x, 0, z);
        if (!TryGetStill(p, out _, out float scale) || !Pattern(p, out double px, out double pz)) return Vector3.Up;
        RestPoint(ref px, ref pz, t, scale);
        WaveSpectrum.Normal(px, pz, t, scale, Amp, out double nx, out double ny, out double nz);
        return new Vector3((float)nx, (float)ny, (float)nz);
    }

    /// <summary>The velocity of the water at the surface over world (x, z) at wave time t (zero with no water).</summary>
    public static Vector3 Velocity(float x, float z, double t)
    {
        var p = new Vector3(x, 0, z);
        if (!TryGetStill(p, out _, out float scale) || !Pattern(p, out double px, out double pz)) return Vector3.Zero;
        RestPoint(ref px, ref pz, t, scale);
        WaveSpectrum.Velocity(px, pz, t, scale, Amp, out double vx, out double vy, out double vz);
        return new Vector3((float)vx, (float)vy, (float)vz);
    }

    /// <summary>
    /// The displacement of the surface point at rest over world (x, z), with a given wave scale:
    /// exactly what the water shader adds to a vertex there (the parity probe compares the two).
    /// </summary>
    public static Vector3 Displacement(Vector3 restWorld, double t, float scale)
    {
        if (!Pattern(restWorld, out double px, out double pz)) return Vector3.Zero;
        WaveSpectrum.Displace(px, pz, t, scale, Amp, out double dx, out double dy, out double dz);
        return new Vector3((float)dx, (float)dy, (float)dz);
    }

    private static double HeightOver(float x, float z, double t, float scale)
    {
        if (scale <= 0f || !Pattern(new Vector3(x, 0, z), out double px, out double pz)) return 0;
        return WaveSpectrum.HeightAt(px, pz, t, scale, Amp);
    }

    /// <summary>Moves a displaced point back to the rest point the waves carried there.</summary>
    private static void RestPoint(ref double px, ref double pz, double t, float scale)
    {
        double x = px, z = pz;
        for (int i = 0; i < 4; i++)
        {
            WaveSpectrum.Displace(px, pz, t, scale, Amp, out double dx, out _, out double dz);
            px = x - dx;
            pz = z - dz;
        }
    }

    /// <summary>Pattern coordinates (LV95 modulo 9.6 km, see <see cref="WaveSpectrum"/>) of a world point.</summary>
    private static bool Pattern(Vector3 world, out double px, out double pz)
    {
        px = pz = 0;
        if (_chunks?.Origin is not { } origin) return false;
        var (e, n) = origin.ToLv95(world);
        px = WaveSpectrum.PatternX(e);
        pz = WaveSpectrum.PatternZ(n);
        return true;
    }

    // ---- the shader ------------------------------------------------------------------------

    private static readonly StringName[] WaveGlobals =
        { "water_wave_0", "water_wave_1", "water_wave_2", "water_wave_3", "water_wave_4", "water_wave_5" };
    private static readonly StringName AmpA = "water_amp_a", AmpB = "water_amp_b", TimeGlobal = "water_time";

    /// <summary>
    /// Writes the wave constants and the amplitudes of the current sea state to the shader globals
    /// (<c>shaders/common/waves.gdshaderinc</c>). At boot and on every sea-state change; the time is
    /// pushed each frame by <see cref="PushTime"/>.
    /// </summary>
    public static void PushGlobals()
    {
        for (int i = 0; i < WaveSpectrum.Count; i++)
        {
            var w = WaveSpectrum.Waves[i];
            RenderingServer.GlobalShaderParameterSet(WaveGlobals[i],
                new Vector4((float)w.Kx, (float)w.Kz, (float)w.Omega, w.Phase));
        }
        RenderingServer.GlobalShaderParameterSet(AmpA, new Vector4(Amp[0], Amp[1], Amp[2], Amp[3]));
        RenderingServer.GlobalShaderParameterSet(AmpB, new Vector4(Amp[4], Amp[5], WaveSpectrum.Chop, 0f));
    }

    /// <summary>The wave clock to the shader; once a frame (<c>World/WaterSurface</c>).</summary>
    public static void PushTime(double t) => RenderingServer.GlobalShaderParameterSet(TimeGlobal, (float)t);
}
