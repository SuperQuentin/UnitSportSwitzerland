using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// One state of the world origin: the LV95 point that is world (0, 0, 0). Immutable, so a worker
/// thread that took one computes its whole result in one consistent frame, and the main thread
/// then maps that result into whatever the origin has become (<see cref="WorldOrigin.Since"/>).
/// </summary>
public sealed class OriginFrame
{
    public double E { get; }
    public double N { get; }

    /// <summary>Bumped by every move of the origin; two frames with the same epoch are the same.</summary>
    public int Epoch { get; }

    public OriginFrame(double e, double n, int epoch)
    {
        E = e;
        N = n;
        Epoch = epoch;
    }

    /// <summary>
    /// A frame that is no peer's origin: the anchor a list of points travels with on the network
    /// (a race road, a bird snapshot), each point a float offset from it, or the frame a server
    /// works one race in. The receiver maps the points with <see cref="WorldOrigin.Since"/>.
    /// Put near the points, so the floats stay small.
    /// </summary>
    public static OriginFrame Anchor(double e, double n) => new(e, n, -1);

    /// <summary>An anchor on the whole kilometre nearest <paramref name="p"/>.</summary>
    public static OriginFrame AnchorNear(GlobalPos p) =>
        Anchor(System.Math.Round(p.E / 1000) * 1000, System.Math.Round(p.N / 1000) * 1000);

    public Vector3 ToWorld(double lv95E, double lv95N, double altitude) =>
        new((float)(lv95E - E), (float)altitude, (float)-(lv95N - N));

    public Vector3 ToWorld(GlobalPos p) => ToWorld(p.E, p.N, p.Alt);

    public (double E, double N) ToLv95(Vector3 world) => (E + world.X, N - world.Z);

    public GlobalPos ToGlobal(Vector3 world) => new(E + world.X, N - world.Z, world.Y);

    public TileId TileAt(Vector3 world)
    {
        var (e, n) = ToLv95(world);
        return TileId.FromLv95(e, n);
    }

    /// <summary>What turns a position in <paramref name="earlier"/>'s world space into this one's.</summary>
    public OriginShift Since(OriginFrame earlier) => SinceAnchor(earlier.E, earlier.N);

    /// <summary>
    /// The same from an anchor given as LV95 (<see cref="Anchor"/> without the object): what turns a
    /// position relative to it into this frame. Nothing allocated, for code that runs every tick.
    /// </summary>
    public OriginShift SinceAnchor(double e, double n) =>
        new(new Transform3D(Basis.Identity, new Vector3((float)(e - E), 0, (float)-(n - N))));
}

/// <summary>
/// Configurable LV95 anchor mapping geodata coordinates to Godot world space:
/// x = east offset, y = altitude, z = -north offset. All terrain math stays in double
/// LV95; floats appear only here at the render/physics boundary.
///
/// <para>
/// The anchor moves while the game runs (#185): <see cref="OriginShifter"/> keeps it near the
/// camera so world coordinates stay small and float32 stays precise however big the world is.
/// A world-space <c>Vector3</c> therefore means a place only in the frame it was computed in.
/// Keep a <see cref="GlobalPos"/> across frames, or handle <see cref="Shifted"/>. A worker thread
/// takes <see cref="Frame"/> once and works in it; the main thread maps the result with
/// <see cref="Since"/>.
/// </para>
/// </summary>
public sealed class WorldOrigin
{
    private volatile OriginFrame _frame;

    public WorldOrigin(double e, double n)
    {
        _frame = new OriginFrame(e, n, 0);
    }

    /// <summary>The current frame: a consistent snapshot, safe to hand to another thread.</summary>
    public OriginFrame Frame => _frame;

    public double E => _frame.E;
    public double N => _frame.N;
    public int Epoch => _frame.Epoch;

    /// <summary>
    /// Main thread, once per move, after every node has been moved (<see cref="OriginShifter"/>):
    /// for whatever keeps world positions outside the scene tree.
    /// </summary>
    public event Action<OriginShift>? Shifted;

    /// <summary>
    /// Fallback anchor for a copy of the game with no terrain data at all — roughly the
    /// centre of Switzerland. Anything is better than LV95 0/0, which is 2.6 million metres
    /// away and would destroy float precision the moment real data arrived.
    /// </summary>
    public static WorldOrigin SwissDefault() => new(2660000, 1190000);

    /// <summary>
    /// <see cref="OriginShifter"/>'s first step: the new frame, and how world space moved. The
    /// caller then moves every node and calls <see cref="RaiseShifted"/>.
    /// </summary>
    internal OriginShift MoveTo(double e, double n)
    {
        var old = _frame;
        _frame = new OriginFrame(e, n, old.Epoch + 1);
        return _frame.Since(old);
    }

    internal void RaiseShifted(OriginShift shift) => Shifted?.Invoke(shift);

    /// <summary>What turns a position in <paramref name="earlier"/>'s world space into the current one's.</summary>
    public OriginShift Since(OriginFrame earlier) => _frame.Since(earlier);

    /// <summary>What turns a position relative to an LV95 anchor into the current world space (<see cref="OriginFrame.SinceAnchor"/>).</summary>
    public OriginShift SinceAnchor(double e, double n) => _frame.SinceAnchor(e, n);

    public Vector3 ToWorld(double lv95E, double lv95N, double altitude) => _frame.ToWorld(lv95E, lv95N, altitude);

    public Vector3 ToWorld(GlobalPos p) => _frame.ToWorld(p);

    public (double E, double N) ToLv95(Vector3 world) => _frame.ToLv95(world);

    public GlobalPos ToGlobal(Vector3 world) => _frame.ToGlobal(world);

    public TileId TileAt(Vector3 world) => _frame.TileAt(world);
}
