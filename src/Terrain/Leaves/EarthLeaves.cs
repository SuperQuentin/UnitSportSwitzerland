using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// Every leaf the earth world stands on (#1510, G2 C1 PR 3): the region's built ones (<see cref="Real"/>) and what the
/// game produces round them, as the flat world's source chain does with LV95 tiles:
/// <list type="bullet">
/// <item>a fixture course's leaves (<c>FixtureLeaves</c>), which are then all there is;</item>
/// <item>the generated world and the fill (<see cref="GeneratedLeaves"/>, its roads through
/// <see cref="GeneratedLeafRoads"/>) on every leaf of the fill's domain (<see cref="LeafFillDomain"/>: discs round the
/// start, the places reached and the real leaves) that the region does not hold, bent into the real ground by
/// <see cref="LeafBlend"/>; and merged into the real leaves the region holds only in part.</item>
/// </list>
/// A produced leaf is made on the first ask (on a worker: ~0.5 s, ~2 s with roads) and the last <see cref="Kept"/> are
/// held. Thread-safe; Godot-free.
/// </summary>
public sealed class EarthLeaves : ILeafSource
{
    /// <summary>Produced leaves held (a few MB each).</summary>
    public const int Kept = 64;

    /// <summary>The region's built leaves.</summary>
    public LeafStore Real { get; }

    /// <summary>The blend of the fill into the real leaves; null without a fill.</summary>
    public LeafBlend? Blend { get; }

    /// <summary>Told what failed while a leaf was produced (the leaf is then left out).</summary>
    public Action<string>? Log { get; set; }

    private readonly GeneratedLeaves? _generated;
    private readonly GeneratedLeafRoads? _roads;
    private readonly Func<bool> _fillOn;
    private readonly Func<CubeKey, bool>? _fixtureHas;
    private readonly Func<CubeKey, CubeLeafContent?>? _fixture;
    private LeafFillDomain? _domain;
    private readonly ConcurrentDictionary<CubeKey, Lazy<CubeLeafContent?>> _made = new();
    private readonly ConcurrentQueue<CubeKey> _order = new();

    private EarthLeaves(LeafStore real, GeneratedLeaves? generated, GeneratedLeafRoads? roads, LeafFillDomain? domain, Func<bool>? fillOn,
        Func<CubeKey, bool>? fixtureHas, Func<CubeKey, CubeLeafContent?>? fixture)
    {
        Real = real;
        _generated = generated;
        _roads = roads;
        _domain = domain;
        _fillOn = fillOn ?? (() => true);
        _fixtureHas = fixtureHas;
        _fixture = fixture;
        if (generated != null) Blend = new LeafBlend(real.IsReal, real.HeightsOf, pyramid: generated.Pyramid);
    }

    /// <summary>The region's leaves alone.</summary>
    public static EarthLeaves Region(LeafStore real) => new(real, null, null, null, null, null, null);

    /// <summary>A fixture course's leaves, all there is.</summary>
    public static EarthLeaves Fixture(Func<CubeKey, bool> has, Func<CubeKey, CubeLeafContent?> build) =>
        new(LeafStore.Empty, null, null, null, null, has, build);

    /// <summary>
    /// The server's leaves arrived or changed (#1655, <see cref="LeafStore.UseRemote"/>): every leaf made from the old view
    /// (the fill where a real leaf now stands, a blend against what was not real) and the blend's summaries are dropped, and
    /// the fill's domain takes in the new real leaves. Whoever holds leaves reads them again (<c>EarthWorld.Refresh</c>).
    /// </summary>
    public void Refresh(IReadOnlyCollection<CubeKey> changed)
    {
        if (changed.Count == 0) return;
        _made.Clear();
        _heightsMade.Clear();
        Blend?.Forget();
        if (_domain is { } d)
        {
            var grown = d.WithReal(changed.Where(Real.Has));
            Interlocked.CompareExchange(ref _domain, grown, d);
        }
    }

    /// <summary>The region's leaves and the fill round them, its domain a disc round the start and round the region.</summary>
    /// <param name="fillOn">The fill's setting, asked whenever a leaf is: off, only real leaves are served.</param>
    /// <param name="roads">Whether the fill's leaves carry roads (RoadGen's stage, ~2 s a leaf, one at a time).</param>
    /// <param name="pyramid">The globe's pyramid the fill is built on (#1678, <see cref="PyramidFill"/>); null, the
    /// generator's own ground, blended over the 3 km band.</param>
    public static EarthLeaves WithFill(LeafStore real, ProceduralWorld world, double startLatDeg, double startLonDeg,
        Func<bool>? fillOn = null, bool roads = true, PyramidBase? pyramid = null) =>
        new(real, new GeneratedLeaves(world, pyramid: pyramid), roads ? new GeneratedLeafRoads(world) : null,
            LeafFillDomain.At(startLatDeg, startLonDeg).WithReal(real.Keys), fillOn, null, null);

    /// <summary>The fill's domain now; null without a fill.</summary>
    public LeafFillDomain? Domain => _domain;

    /// <summary>The fill's domain grown so it holds a place (a camera, a body, a teleport's target).</summary>
    public void Reach(double latDeg, double lonDeg)
    {
        if (_domain is not { } d || d.Contains(latDeg, lonDeg)) return;
        var grown = d.Reach(latDeg, lonDeg);
        Interlocked.CompareExchange(ref _domain, grown, d);
    }

    /// <summary>The fill's domain grown so it holds a leaf.</summary>
    public void Reach(CubeKey leaf)
    {
        if (_domain is null) return;
        var c = leaf.Centre;
        Reach(c.LatDeg, c.LonDeg);
    }

    /// <summary>A fixture course's world (its leaves are all there is).</summary>
    public bool IsFixture => _fixtureHas != null;

    /// <summary>Whether the fill answers for a leaf (the region may hold part of it).</summary>
    public bool Filled(CubeKey key) => _generated != null && _domain is { } d && _fillOn() && d.Contains(key);

    public bool Has(CubeKey key) => Real.Has(key) || _fixtureHas?.Invoke(key) == true || Filled(key);

    public bool IsReal(CubeKey key) => Real.Has(key);

    public CubeLeafContent? Get(CubeKey key)
    {
        if (Real.Has(key))
        {
            // a leaf the region holds whole is the region's; one it holds in part is merged with the fill
            if (!Filled(key)) return Real.Get(key);
            // a streamed leaf that did not come yet (#1655) is asked again later, never merged as if it had no real cell
            if (Real.Get(key) is not { } real) return null;
            if (Blend!.Summary(key) is { Full: true }) return real;
            return Made(key, k => Blend.Apply(Generated(k, roads: false), real));
        }
        if (_fixtureHas?.Invoke(key) == true) return Made(key, _fixture!);
        if (Filled(key)) return Made(key, k => Blend!.Apply(Generated(k, roads: true)));
        return null;
    }

    /// <summary>
    /// A leaf's roads for the traffic's graphs (#1636), without decoding or keeping its other sections where the region holds
    /// it (a leaf merged with the fill keeps the region's roads); a fixture course's from its built leaf; a fill leaf's only
    /// once it is made (the ground near a view makes it): a generated leaf costs a RoadGen run, too much for a few km of graph.
    /// </summary>
    public RoadTile? RoadsOf(CubeKey key)
    {
        if (Real.Has(key)) return Real.RoadsOf(key);
        if (_fixtureHas?.Invoke(key) == true) return Get(key)?.Roads;
        return Filled(key) ? Cached(key)?.Roads : null;
    }

    public CubeLeafContent? Cached(CubeKey key)
    {
        if (_made.TryGetValue(key, out var lazy)) return lazy.IsValueCreated ? lazy.Value : null;
        return Real.Cached(key);
    }

    /// <summary>Generator vertices a side between the globe's heights-only samples (it draws every 8th).</summary>
    public const int GlobeStride = 8;

    private readonly ConcurrentDictionary<CubeKey, Lazy<(CubeTile? Heights, CubeWater? Lakes)>> _heightsMade = new();
    private readonly ConcurrentQueue<CubeKey> _heightsOrder = new();

    public CubeTile? HeightsOf(CubeKey key)
    {
        if (Cached(key) is { Heights: { } held }) return held;
        float[]? realHeights = null;
        if (Real.Has(key))
        {
            // a streamed leaf whose heights did not come yet (#1655): asked again later, never blended as if it had none
            if (Real.HeightsOf(key) is not { } own) return null;
            if (!Filled(key) || Blend!.Summary(key) is { Full: true }) return own;
            realHeights = own.Heights;
        }
        if (_fixtureHas?.Invoke(key) == true) return Get(key)?.Heights;
        if (!Filled(key)) return null;
        return FarMade(key, realHeights).Heights;
    }

    /// <summary>
    /// A leaf's still water for the globe's far ground (#1678): the region's own on a real leaf, a fill leaf's when it is
    /// made, else the pyramid's lakes over it (made with its far heights, <see cref="HeightsOf"/>); null when dry.
    /// </summary>
    public CubeWater? WaterOf(CubeKey key)
    {
        if (Real.Has(key) && !Filled(key)) return Real.Sections(key, LeafKind.Water)?.Water;
        if (Cached(key) is { } made) return made.Water;
        if (Real.Has(key)) return Real.Sections(key, LeafKind.Water)?.Water;
        if (!Filled(key)) return null;
        return FarMade(key, null).Lakes;
    }

    private (CubeTile? Heights, CubeWater? Lakes) FarMade(CubeKey key, float[]? realHeights)
    {
        bool added = false;
        var lazy = _heightsMade.GetOrAdd(key, k =>
        {
            added = true;
            return new Lazy<(CubeTile?, CubeWater?)>(() =>
            {
                try
                {
                    var (g, lakes) = _generated!.Far(k, GlobeStride);
                    var r = Blend!.Heights(k, g.Heights, realHeights);
                    return (r is null ? g : new CubeTile { Key = k, Bits = g.Bits, Heights = r.Heights, Quantum = g.Quantum }, lakes);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log?.Invoke($"[earth] heights {k}: {e.GetType().Name}: {e.Message}");
                    return (null, null);
                }
            });
        });
        if (added)
        {
            _heightsOrder.Enqueue(key);
            while (_heightsMade.Count > 4 * Kept && _heightsOrder.TryDequeue(out var old)) _heightsMade.TryRemove(old, out _);
        }
        return lazy.Value;
    }

    private CubeLeafContent Generated(CubeKey key, bool roads)
    {
        var content = _generated!.Build(key);
        if (roads && _roads != null)
            try { content.Roads = _roads.Build(key); }
            catch (Exception e) when (e is not OperationCanceledException) { Log?.Invoke($"[earth] fill roads {key}: {e.Message}"); }
        // on the globe's pyramid (#1678), its roads moved with the ground
        return _generated.OnPyramid(content);
    }

    private CubeLeafContent? Made(CubeKey key, Func<CubeKey, CubeLeafContent?> make)
    {
        bool added = false;
        var lazy = _made.GetOrAdd(key, k =>
        {
            added = true;
            return new Lazy<CubeLeafContent?>(() =>
            {
                try { return make(k); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log?.Invoke($"[earth] {k}: {e.GetType().Name}: {e.Message}");
                    return null;
                }
            });
        });
        if (added)
        {
            _order.Enqueue(key);
            while (_made.Count > Kept && _order.TryDequeue(out var old)) _made.TryRemove(old, out _);
        }
        return lazy.Value;
    }
}
