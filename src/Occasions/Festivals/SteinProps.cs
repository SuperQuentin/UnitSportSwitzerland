using Godot;
using UnitSport.Avatar;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// The stone put's ground (#1269 part 3): the stone itself, the sawdust lane it is thrown along and the stake that marks a landing.
/// All <see cref="PropKind"/>s, so the model viewer lists them (Occasions › Prop) and they are built once and dressed per tile with the
/// rest of the festival (<see cref="Decorate"/>, hashed per tile). The flying stone and the landing stakes are the same meshes on
/// nodes (<see cref="SteinGame"/>); nothing here is rebuilt per frame.
/// </summary>
public static class SteinProps
{
    private static readonly Color Granite = Matte(0.52f, 0.52f, 0.54f);
    private static readonly Color GraniteDark = Matte(0.40f, 0.40f, 0.43f);
    private static readonly Color Rope = Matte(0.72f, 0.60f, 0.38f);
    private static readonly Color Sawdust = Matte(0.82f, 0.68f, 0.44f);
    private static readonly Color Plank = Matte(0.50f, 0.38f, 0.24f);
    private static readonly Color Red = Matte(0.82f, 0.10f, 0.12f);
    private static readonly Color White = Matte(0.94f, 0.94f, 0.92f);

    /// <summary>
    /// The Unspunnen stone: a rough granite lump, 0.4 m across, a flat foot and a rope loop worn smooth by the hands. The stone of the
    /// three classes is one shape; the class is the weight on the Journal and the meter, not a second model.
    /// </summary>
    public static readonly PropKind Stone = new("FestivalSteinStone", () =>
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.17f, 0), new Vector3(0.42f, 0.34f, 0.40f), Granite, new Basis(Vector3.Up, 0.35f));
        s.Box(new Vector3(0.02f, 0.20f, 0), new Vector3(0.40f, 0.30f, 0.38f), GraniteDark, new Basis(Vector3.Up, 0.9f));
        s.Box(new Vector3(0, 0.36f, 0.02f), new Vector3(0.30f, 0.12f, 0.28f), Granite, new Basis(Vector3.Up, 0.1f));
        s.Tube(new Vector3(-0.13f, 0.43f, 0), new Vector3(0.13f, 0.43f, 0), 0.025f, Rope, 6);
        return s.Build();
    }, candle: false);

    /// <summary>An 8 m length of the lane the stone is thrown along: sawdust between two ropes, one stake in plank every 2 m. Four make the run-up and the throw.</summary>
    public static readonly PropKind Lane = new("FestivalSteinLane", () =>
    {
        var s = new MeshScratch();
        s.Box(new Vector3(4, 0.025f, 0), new Vector3(8f, 0.05f, 4f), Sawdust);
        for (int k = 0; k < 4; k++)
            foreach (float side in new[] { -2.05f, 2.05f })
            {
                s.Box(new Vector3(k * 2f + 0.1f, 0.3f, side), new Vector3(0.07f, 0.6f, 0.07f), Plank);
                s.Tube(new Vector3(k * 2f + 0.1f, 0.5f, side), new Vector3((k + 1) * 2f + 0.1f, 0.5f, side), 0.015f, Rope, 4);
            }
        return s.Build();
    }, candle: false);

    /// <summary>A stake at a landing: a red and white pennant on a metre of plank, pushed in where the stone fell.</summary>
    public static readonly PropKind Mark = new("FestivalSteinMark", () =>
    {
        var s = new MeshScratch();
        s.Box(new Vector3(0, 0.55f, 0), new Vector3(0.05f, 1.1f, 0.05f), Plank);
        s.Box(new Vector3(0.18f, 0.95f, 0), new Vector3(0.36f, 0.22f, 0.02f), Red);
        s.Box(new Vector3(0.18f, 0.95f, 0), new Vector3(0.2f, 0.06f, 0.05f), White);
        s.Box(new Vector3(0.18f, 0.95f, 0), new Vector3(0.06f, 0.2f, 0.056f), White);
        return s.Build();
    }, candle: false);

    /// <summary>Lengths of the lane, laid end to end from 8 m behind the stone to 24 m in front of it.</summary>
    private const double LaneFrom = -8, LaneStep = 8;
    private const int LaneParts = 4;

    /// <summary>Dresses the part of the put's ground that belongs to <paramref name="tile"/>: the stone, and the lane under it.</summary>
    public static void Decorate(ResolvedSite site, TileContext tile, DecorBuilder into)
    {
        var g = SteinGround.Of(site);
        float yaw = g.Yaw;
        if (TileId.FromLv95(g.E, g.N) == tile.Id && tile.HeightAt(tile.Local(g.E, g.N)) is { } h0)
        {
            var at = tile.Local(g.E, g.N);
            at.Y = h0;
            into.Add(Stone, new Transform3D(new Basis(Vector3.Up, yaw), at));
        }
        for (int k = 0; k < LaneParts; k++)
        {
            var (e, n) = g.Along(LaneFrom + k * LaneStep);
            if (TileId.FromLv95(e, n) != tile.Id) continue;
            var local = tile.Local(e, n);
            if (tile.HeightAt(local) is not { } h) continue;
            local.Y = h;
            into.Add(Lane, new Transform3D(new Basis(Vector3.Up, yaw), local));
        }
    }
}
