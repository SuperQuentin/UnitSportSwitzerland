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

    /// <summary>Tonight's event in a stadium, or the one forced; null when concerts are off.</summary>
    public static ConcertEvent? EventFor(StadiumSpec s) => ForcedSeed switch
    {
        -1 => null,
        { } seed => ConcertPlanner.Plan(s, seed),
        _ => ConcertPlanner.Tonight(s, DateTime.Now.Date),
    };

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

    private static void OnUnfurnished(TileId id, ChunkNode node) => node.GetNodeOrNull(NodeName)?.QueueFree();

    private void OnFurnished(TileId id, ChunkNode node, Interiors.DoorSpot[] doors)
    {
        foreach (var s in StadiumCatalog.OwnedBy(id))
        {
            // a rebuild of the tile (a detail change) furnishes it again: one show is enough
            if (node.GetNodeOrNull(NodeName) != null) return;
            if (EventFor(s) is not { } e) return;
            var local = new Vector3((float)(s.E - id.MinE), 0, (float)(id.MaxN - s.N));
            var world = node.ToGlobal(local);
            if (!_chunks.TryGetSurface(world, out float ground)) continue;
            local.Y = node.ToLocal(world with { Y = ground }).Y;
            float a = Mathf.DegToRad(s.AzimuthDeg);
            var basis = new Basis(new Vector3(MathF.Cos(a), 0, MathF.Sin(a)), Vector3.Up, new Vector3(-MathF.Sin(a), 0, MathF.Cos(a)));
            var rig = new ConcertRig(e) { Name = NodeName, Transform = new Transform3D(basis, local), ForceLive = ForcedSeed is >= 0 };
            node.AddChild(rig);
        }
    }
}
