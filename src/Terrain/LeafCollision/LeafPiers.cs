using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// The piers, landings and jetty decks on earth leaves (#1736): the region's <see cref="LandingIndex"/> (LV95, as
/// <c>landings.json</c> holds it) cut by leaf and moved into each leaf's stand-in frame, so <see cref="PierMeshBuilder"/>
/// builds a leaf's decks exactly as it builds a tile's: the mesh into the leaf's furnishing (the prop material), the faces
/// into its collision with the ground. A ribbon belongs to the leaf its middle is on (as a tile's: one leaf builds it), a
/// bollard to the leaf it stands on. A point with an Earth copy (<see cref="PierRibbon.GeoPoints"/>, a cube region's file)
/// is placed by it, else by its LV95 notation. The cut is made once per landings index (they change when a client gets the
/// server's). Any thread.
/// </summary>
public sealed class LeafPiers(Func<LandingIndex?> landings)
{
    private readonly object _gate = new();
    private LandingIndex? _of;
    private Dictionary<CubeKey, LandingIndex>? _byLeaf;

    /// <summary>The leaf's decks: their mesh and collision faces in its stand-in's frame, as <see cref="PierMeshBuilder.Build"/>; null with none.</summary>
    /// <param name="grid">The stand-in's grid, for the piles' feet on the bed.</param>
    public (PierMeshBuilder.MeshData? Mesh, Vector3[] Faces)? Build(CubeKey leaf, ChunkGrid? grid, bool mesh, bool collision) =>
        Of(leaf) is { } index ? PierMeshBuilder.Build(index, LeafStandIn.Id(leaf), grid, mesh, collision) : null;

    /// <summary>The leaves holding a deck or a bollard of <paramref name="index"/>.</summary>
    public static IEnumerable<CubeKey> Leaves(LandingIndex index) => Cut(index).Keys;

    /// <summary>The landings on one leaf, in its stand-in's frame; null with none.</summary>
    public LandingIndex? Of(CubeKey leaf)
    {
        var index = landings();
        if (index == null) return null;
        Dictionary<CubeKey, LandingIndex> byLeaf;
        lock (_gate)
        {
            if (!ReferenceEquals(index, _of) || _byLeaf == null) (_of, _byLeaf) = (index, Cut(index));
            byLeaf = _byLeaf;
        }
        return byLeaf.GetValueOrDefault(leaf);
    }

    /// <summary>Every ribbon and bollard by its leaf, moved into that leaf's stand-in frame (one landing per leaf it reaches).</summary>
    public static Dictionary<CubeKey, LandingIndex> Cut(LandingIndex index)
    {
        var byLeaf = new Dictionary<CubeKey, LandingIndex>();
        Landing Into(CubeKey leaf, string name)
        {
            if (!byLeaf.TryGetValue(leaf, out var at)) byLeaf[leaf] = at = new LandingIndex();
            var l = at.Landings.FirstOrDefault(x => x.Name == name);
            if (l == null) at.Landings.Add(l = new Landing { Name = name });
            return l;
        }
        foreach (var landing in index.Landings)
        {
            foreach (var r in landing.Ribbons)
                if (Moved(r) is var (leaf, ribbon)) Into(leaf, landing.Name).Ribbons.Add(ribbon);
            foreach (var b in landing.Bollards)
            {
                var place = GlobalPos.FromLv95(b[0], b[1], b[2]);
                var leaf = LeafOf(place);
                var (e, n, h) = place.InTileEn(LeafStandIn.Id(leaf));
                Into(leaf, landing.Name).Bollards.Add([e, n, h]);
            }
        }
        foreach (var jetty in index.Jetties)
            if (Moved(jetty.Ribbon) is var (leaf, ribbon))
            {
                if (!byLeaf.TryGetValue(leaf, out var at)) byLeaf[leaf] = at = new LandingIndex();
                at.Jetties.Add(new Jetty { Id = jetty.Id, Ribbon = ribbon });
            }
        return byLeaf;
    }

    /// <summary>The leaf a place is on.</summary>
    private static CubeKey LeafOf(GlobalPos p) => CubeKey.FromEcef(p.ToEcef(), LeafFrames.LeafLevel);

    /// <summary>A ribbon's leaf (its middle's) and the ribbon in that leaf's stand-in frame.</summary>
    private static (CubeKey Leaf, PierRibbon Ribbon)? Moved(PierRibbon r)
    {
        if (r.Points.Count < 2) return null;
        var places = new GlobalPos[r.Points.Count];
        for (int i = 0; i < places.Length; i++)
            places[i] = r.GeoPoints is { } geo && geo.Count == places.Length
                ? GlobalPos.FromEcef(geo[i].ToEcef())
                : GlobalPos.FromLv95(r.Points[i][0], r.Points[i][1], r.Points[i][2]);
        var (me, mn) = r.Middle;
        var leaf = LeafOf(GlobalPos.FromLv95(me, mn, r.Points[0][2]));
        var id = LeafStandIn.Id(leaf);
        var points = new List<double[]>(places.Length);
        foreach (var p in places)
        {
            var (e, n, h) = p.InTileEn(id);
            points.Add([e, n, h]);
        }
        return (leaf, new PierRibbon { Kind = r.Kind, Width = r.Width, Rails = r.Rails, Points = points });
    }
}
