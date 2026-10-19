using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>
/// Puts tonight's concert into each stadium (#885): when the tile holding a stadium's centre spot is
/// furnished (its buildings drawn, <see cref="ChunkManager.TileFurnished"/>), a <see cref="ConcertRig"/>
/// for the day's <see cref="ConcertEvent"/> is hung on the tile's node, turned to the stadium's frame
/// and set on the pitch; it goes with the buildings. Client only, like every other tile dressing:
/// the event is a pure function of the stadium and the date, so every peer builds the same one.
/// <c>--concert N</c> plays the show of seed N at any hour, <c>--concert off</c> puts on none.
/// </summary>
public partial class ConcertDirector : Node
{
    private const string NodeName = "Concert";
    private readonly ChunkManager _chunks;

    public ConcertDirector(ChunkManager chunks)
    {
        Name = "ConcertDirector";
        _chunks = chunks;
    }

    public ConcertDirector() : this(null!) { }

    /// <summary>The seed forced on the command line, if any; <c>off</c> gives -1.</summary>
    public static int? ForcedSeed => CmdArgs.Value("--concert") is { } v
        ? v == "off" ? -1 : int.TryParse(v, out int s) ? s : null
        : null;

    /// <summary>
    /// The event in a stadium: the set an operator hosts (<see cref="ConcertService"/>), else the one
    /// forced on the command line, else tonight's; null when concerts are off.
    /// </summary>
    public static ConcertEvent? EventFor(StadiumSpec s, int index) =>
        ConcertService.Instance?.SeedFor(index) is { } hosted ? ConcertPlanner.Plan(s, hosted)
        : ForcedSeed switch
        {
            -1 => null,
            { } seed => ConcertPlanner.Plan(s, seed),
            _ => ConcertPlanner.Tonight(s, DateTime.Now.Date),
        };

    /// <summary>The tile nodes holding each stadium's rig, so a hosted set can replace it.</summary>
    private readonly Dictionary<int, (TileId Id, ChunkNode Node)> _tiles = new();

    public override void _Ready()
    {
        if (_chunks == null) return;
        _chunks.TileFurnished += OnFurnished;
        _chunks.TileUnfurnished += OnUnfurnished;
    }

    public override void _ExitTree()
    {
        if (_chunks == null) return;
        _chunks.TileFurnished -= OnFurnished;
        _chunks.TileUnfurnished -= OnUnfurnished;
    }

    private void OnUnfurnished(TileId id, ChunkNode node)
    {
        node.GetNodeOrNull(NodeName)?.QueueFree();
        foreach (var key in _tiles.Where(kv => kv.Value.Id == id).Select(kv => kv.Key).ToList()) _tiles.Remove(key);
    }

    /// <summary>A set hosted or released at a stadium: its rig goes, the new event's is hung in its place.</summary>
    public void Rebuild(int index)
    {
        if (!_tiles.TryGetValue(index, out var t) || !IsInstanceValid(t.Node)) return;
        if (t.Node.GetNodeOrNull(NodeName) is { } old)
        {
            old.Name = NodeName + "Old";
            old.QueueFree();
        }
        Hang(t.Id, t.Node, index);
    }

    private void OnFurnished(TileId id, ChunkNode node, Interiors.DoorSpot[] doors)
    {
        for (int i = 0; i < StadiumCatalog.All.Length; i++)
            if (StadiumCatalog.Owner(StadiumCatalog.All[i]) == id)
            {
                _tiles[i] = (id, node);
                // the bowl hides the city round it (occlusion culling), show or no show
                if (node.GetNodeOrNull(OccluderName) == null && Frame(id, node, StadiumCatalog.All[i]) is { } frame)
                {
                    var occ = StadiumOccluder.Make(StadiumCatalog.All[i]);
                    occ.Name = OccluderName;
                    occ.Transform = frame;
                    node.AddChild(occ);
                }
                // a rebuild of the tile (a detail change) furnishes it again: one show is enough
                if (node.GetNodeOrNull(NodeName) == null) Hang(id, node, i);
            }
    }

    /// <summary>The stadium frame on the tile's node: the centre spot on the ground, turned to the pitch's azimuth (u, y, v).</summary>
    private Transform3D? Frame(TileId id, ChunkNode node, StadiumSpec s)
    {
        var local = new Vector3((float)(s.E - id.MinE), 0, (float)(id.MaxN - s.N));
        var world = node.ToGlobal(local);
        if (!_chunks.TryGetSurface(world, out float ground)) return null;
        local.Y = node.ToLocal(world with { Y = ground }).Y;
        float a = Mathf.DegToRad(s.AzimuthDeg);
        var basis = new Basis(new Vector3(MathF.Cos(a), 0, MathF.Sin(a)), Vector3.Up, new Vector3(-MathF.Sin(a), 0, MathF.Cos(a)));
        return new Transform3D(basis, local);
    }

    private const string OccluderName = "StadiumOccluder";

    private void Hang(TileId id, ChunkNode node, int index)
    {
        var s = StadiumCatalog.All[index];
        if (EventFor(s, index) is not { } e || Frame(id, node, s) is not { } frame) return;
        // a hosted set plays whatever the hour: its operator runs it
        bool hosted = ConcertService.Instance?.SeedFor(index) != null;
        var rig = new ConcertRig(e) { Name = NodeName, Transform = frame, ForceLive = hosted || ForcedSeed is >= 0 };
        node.AddChild(rig);
        ConcertService.Instance?.ApplyCues(index, rig);
    }
}
