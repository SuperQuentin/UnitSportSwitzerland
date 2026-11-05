using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// The work frames and leaf transforms the game's own producers build leaves through (#1509, G2 B5): a leaf's
/// level-9 ancestor's <see cref="WorkFrame"/>, as the build stages use (<c>tools/cube-frames</c>), and the leaf's
/// <see cref="LeafTransform"/> (3.3 ms to make; the last <see cref="Kept"/> are held). Thread-safe.
/// </summary>
public sealed class LeafFrames(ISeaLevel seaLevel)
{
    /// <summary>The leaves' level (~610 m), the build's.</summary>
    public const int LeafLevel = 14;

    /// <summary>Transforms held before the cache starts over (~30 KB each).</summary>
    public const int Kept = 512;

    /// <summary>Switzerland's sea level (LHN95), what the generated world and the fixture courses stand above.</summary>
    public static readonly LeafFrames Swiss = new(SwissSeaLevel.Instance);

    private readonly ConcurrentDictionary<CubeKey, WorkFrame> _frames = new();
    private readonly ConcurrentDictionary<CubeKey, Lazy<LeafTransform>> _transforms = new();

    public ISeaLevel SeaLevel { get; } = seaLevel;

    public WorkFrame FrameOf(CubeKey leaf) =>
        _frames.GetOrAdd(leaf.AncestorAt(WorkFrame.Level), cell => new WorkFrame(cell, SeaLevel));

    public LeafTransform TransformOf(CubeKey leaf)
    {
        if (_transforms.Count > Kept) _transforms.Clear();
        return _transforms.GetOrAdd(leaf, k => new Lazy<LeafTransform>(() => new LeafTransform(FrameOf(k), k))).Value;
    }

    /// <summary>The leaf a latitude and longitude lie in (its normal's cube cell, whatever the height).</summary>
    public static CubeKey LeafAt(double latDeg, double lonDeg) => CubeKey.FromLatLon(latDeg, lonDeg, LeafLevel);
}
