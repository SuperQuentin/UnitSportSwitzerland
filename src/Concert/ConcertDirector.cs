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
/// The stage stands from the morning's build to the show's end (<see cref="ConcertEvent.Standing"/>):
/// then it is struck, and the stadium stands empty until the next day's (#956).
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

    /// <summary>The seed forced on the command line, if any (<c>off</c> gives -1), else the world's own (<see cref="WorldSeed"/>).</summary>
    public static int? ForcedSeed => CmdArgs.Value("--concert") is { } v
        ? v == "off" ? -1 : int.TryParse(v, out int s) ? s : null
        : WorldSeed;

    /// <summary>A world whose stage always plays (#1396, the fun world's arena): its set's seed; null in the open world.</summary>
    public static int? WorldSeed { get; set; }

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
            _ => ConcertPlanner.Open(s, Core.Calendar.Now.Date) ? ConcertPlanner.Tonight(s, Core.Calendar.Now.Date) : null,
        };

    /// <summary>The tile nodes holding each stadium's rig, so a hosted set can replace it.</summary>
    private readonly Dictionary<int, (TileId Id, ChunkNode Node)> _tiles = new();

    public override void _Ready()
    {
        if (_chunks == null) return;
        _chunks.TileFurnished += OnFurnished;
        _chunks.TileUnfurnished += OnUnfurnished;
        // the crowd's loops are 0.7 s of DSP: made now off the main thread, not in the frame the first stage is hung
        if (DisplayServer.GetName() != "headless") System.Threading.Tasks.Task.Run(ConcertSound.Prewarm);
    }

    public override void _ExitTree()
    {
        if (_chunks == null) return;
        _chunks.TileFurnished -= OnFurnished;
        _chunks.TileUnfurnished -= OnUnfurnished;
    }

    private double _nextCheck;

    /// <summary>Every few seconds: a show over is struck, a day's stage is built (#956). Allocates nothing while nothing changes.</summary>
    public override void _Process(double delta)
    {
        double now = Core.RealClock.Now;
        if (now < _nextCheck) return;
        _nextCheck = now + 2.0;
        foreach (var (index, t) in _tiles)
        {
            if (!IsInstanceValid(t.Node)) continue;
            bool want = Wanted(index), has = t.Node.GetNodeOrNull(NodeName) != null;
            if (want == has) continue;
            if (has) Strike(t.Node);
            else Hang(t.Id, t.Node, index);
        }
    }

    /// <summary>Whether a stadium has its stage now: a hosted or forced show always, tonight's from its build to its end.</summary>
    private static bool Wanted(int index)
    {
        if (ConcertService.Instance?.SeedFor(index) != null || ForcedSeed is >= 0) return true;
        if (ForcedSeed == -1) return false;
        var venue = StadiumCatalog.All[index];
        return ConcertPlanner.Open(venue, Core.Calendar.Now.Date) && ConcertPlanner.Tonight(venue, Core.Calendar.Now.Date).Standing(World.WorldClock.CurrentHour);
    }

    /// <summary>The stage, its rig, its floor and its crowd taken away.</summary>
    private static void Strike(ChunkNode node)
    {
        if (node.GetNodeOrNull(NodeName) is not { } old) return;
        old.Name = NodeName + "Old";
        old.QueueFree();
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
        Strike(t.Node);
        if (Wanted(index)) Hang(t.Id, t.Node, index);
    }

    private void OnFurnished(TileId id, ChunkNode node, Interiors.DoorSpot[] doors)
    {
        for (int i = 0; i < StadiumCatalog.All.Length; i++)
            if (StadiumCatalog.Owner(StadiumCatalog.All[i]) == id)
            {
                _tiles[i] = (id, node);
                // the bowl hides the city round it (occlusion culling), show or no show
                if (node.GetNodeOrNull(OccluderName) == null && Frame(id, node, StadiumCatalog.All[i]) is { } frame
                    && StadiumOccluder.Make(StadiumCatalog.All[i]) is { } occ)
                {
                    occ.Name = OccluderName;
                    occ.Transform = frame;
                    node.AddChild(occ);
                }
                // a rebuild of the tile (a detail change) furnishes it again: one show is enough
                if (node.GetNodeOrNull(NodeName) == null && Wanted(i)) Hang(id, node, i);
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

    /// <summary>
    /// A stadium's protection floor's top (#956): a few centimetres over the highest of the terrain
    /// under its pitch, sampled every few metres, so no bump shows through the boards. Kept off the
    /// seating front, where the ground may already climb under a stand, and never over a step's height.
    /// </summary>
    private float FloorTop(ChunkNode node, Transform3D frame, StadiumSpec s)
    {
        float high = 0f;
        for (float u = -s.FrontHalfU + 3f; u <= s.FrontHalfU - 3f; u += 4f)
            for (float v = -s.FrontHalfV + 3f; v <= s.FrontHalfV - 3f; v += 4f)
            {
                var world = node.ToGlobal(frame * new Vector3(u, 0, v));
                if (_chunks.TryGetSurface(world, out float y)) high = MathF.Max(high, (frame.AffineInverse() * node.ToLocal(world with { Y = y })).Y);
            }
        return Math.Min(high, 1.2f) + 0.08f;
    }

    private const string OccluderName = "StadiumOccluder";

    private void Hang(TileId id, ChunkNode node, int index)
    {
        var s = StadiumCatalog.All[index];
        if (EventFor(s, index) is not { } e || Frame(id, node, s) is not { } frame) return;
        // a hosted set plays whatever the hour: its operator runs it
        bool hosted = ConcertService.Instance?.SeedFor(index) != null;
        // an open air's rig stands on the ground where its stage is built, its crowd on the field as it
        // lies (Gurten, the Sittertobel slope); an arena's crowd on the floor over the pitch (#948)
        Func<Vector2, float>? ground = null;
        if (s.Kind == VenueKind.OpenAir)
        {
            var stageAt = s.StageAt switch
            {
                StadiumSide.North => new Vector2(0, -s.FrontHalfV + 8f),
                StadiumSide.South => new Vector2(0, s.FrontHalfV - 8f),
                StadiumSide.East => new Vector2(s.FrontHalfU - 8f, 0),
                StadiumSide.West => new Vector2(-s.FrontHalfU + 8f, 0),
                _ => Vector2.Zero,
            };
            float GroundIn(Transform3D f, Vector2 uv)
            {
                var world = node.ToGlobal(f * new Vector3(uv.X, 0, uv.Y));
                return _chunks.TryGetSurface(world, out float y) ? (f.AffineInverse() * node.ToLocal(world with { Y = y })).Y : 0f;
            }
            frame = frame with { Origin = frame.Origin + frame.Basis.Y * GroundIn(frame, stageAt) };
            var f1 = frame;
            ground = uv => GroundIn(f1, uv);
        }
        else if (s.Kind == VenueKind.Arena) ground = _ => StadiumBuilder.FloorLift;
        float floorTop = s.Kind == VenueKind.Stadium ? FloorTop(node, frame, s) : 0f;
        var rig = new ConcertRig(e)
        {
            Name = NodeName, Transform = frame, ForceLive = hosted || ForcedSeed is >= 0, Ground = ground, FloorTop = floorTop,
            Imported = hosted ? ConcertService.Instance?.RigFor(index) : null,
        };
        node.AddChild(rig);
        ConcertService.Instance?.ApplyCues(index, rig);
    }
}
