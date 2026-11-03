using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>An inclusive tile rectangle.</summary>
public readonly record struct TileRect(int MinE, int MinN, int MaxE, int MaxN)
{
    public bool Contains(TileId id) => id.E >= MinE && id.E <= MaxE && id.N >= MinN && id.N <= MaxN;
    public TileRect Grow(int by) => new(MinE - by, MinN - by, MaxE + by, MaxN + by);
    public TileRect Union(TileRect o) =>
        new(Math.Min(MinE, o.MinE), Math.Min(MinN, o.MinN), Math.Max(MaxE, o.MaxE), Math.Max(MaxN, o.MaxN));
    /// <summary>The box <paramref name="radius"/> tiles round one tile.</summary>
    public static TileRect Around(TileId id, int radius) => new TileRect(id.E, id.N, id.E, id.N).Grow(radius);
}

/// <summary>
/// Where the generated fill may generate (<see cref="FallbackChunkSource"/>): the box round the spawn,
/// the boxes round the places it was asked to <see cref="Reach"/> since, and the real set's bounding box,
/// each grown by the fill radius. Immutable, it only ever grows. Pure, unit-tested (<c>FillDomainTests</c>).
///
/// <para>
/// A client fills round its own start. A dedicated server serves players who started anywhere (a client's
/// <c>--at</c>, a Battle Royale opened where its admin stands): it reaches round every body it holds, or a
/// bot dropped 170 km from the server's spawn found no ground under it and sank through the world (#937).
/// </para>
/// </summary>
public sealed class FillDomain
{
    private readonly TileRect[] _boxes;

    /// <summary>The box round the spawn, then every box <see cref="Reach"/> added, oldest first.</summary>
    public IReadOnlyList<TileRect> Boxes => _boxes;

    /// <summary>The real set's bounding box grown by the fill radius; null with no real tiles yet.</summary>
    public TileRect? RealBox { get; }

    private FillDomain(TileRect[] boxes, TileRect? realBox)
    {
        _boxes = boxes;
        RealBox = realBox;
    }

    /// <summary>The domain of a fill started at <paramref name="spawn"/>: <paramref name="radius"/> tiles round it.</summary>
    public static FillDomain At(TileId spawn, int radius) => new([TileRect.Around(spawn, radius)], null);

    public bool Contains(TileId id)
    {
        if (RealBox?.Contains(id) == true) return true;
        foreach (var box in _boxes)
            if (box.Contains(id)) return true;
        return false;
    }

    /// <summary>One rectangle holding the whole domain, for sizing the horizon's coverage texture.</summary>
    public TileRect Bounds
    {
        get
        {
            var all = _boxes[0];
            for (int i = 1; i < _boxes.Length; i++) all = all.Union(_boxes[i]);
            return RealBox is { } r ? all.Union(r) : all;
        }
    }

    /// <summary>The same domain with another real bounding box (never smaller: it is unioned with the last one).</summary>
    public FillDomain WithReal(TileRect? realBox)
    {
        if (RealBox is { } previous) realBox = realBox is { } b ? b.Union(previous) : previous;
        return new FillDomain(_boxes, realBox);
    }

    /// <summary>
    /// The domain grown so it holds <paramref name="at"/>: itself when it already does, else with a box
    /// <paramref name="radius"/> tiles round it, the same reach a fill started there has.
    /// </summary>
    public FillDomain Reach(TileId at, int radius) =>
        Contains(at) ? this : new FillDomain([.. _boxes, TileRect.Around(at, radius)], RealBox);
}
