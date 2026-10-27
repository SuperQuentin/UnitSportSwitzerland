using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A neon sign over the main door of every venue (#1232, <see cref="DoorSpot.Venue"/>): its OSM name
/// and an icon of what it is (<see cref="NeonSign"/>). Client only, on <see cref="ChunkManager.TileFurnished"/>
/// like <see cref="BankSigns"/> (which leaves a venue's door to this one); parented to the tile's
/// node, so the signs unload with it.
/// </summary>
public partial class VenueSigns : Node
{
    private const string NodeName = "VenueSigns";

    /// <summary>A hall's big sign: its letters, and how far over the name sign it hangs (clear of the name's icon).</summary>
    private const float BigLetters = 1.2f, BigAbove = 2.4f;

    private readonly ChunkManager _chunks;

    public VenueSigns(ChunkManager chunks)
    {
        Name = "VenueSigns";
        _chunks = chunks;
    }

    public VenueSigns() : this(null!) { }

    public override void _Ready()
    {
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

    private void OnFurnished(TileId id, ChunkNode node, DoorSpot[] doors)
    {
        node.GetNodeOrNull(NodeName)?.QueueFree();
        Node3D? root = null;
        foreach (var d in doors)
        {
            if (d.Venue <= 0 || d.Width <= 0) continue;
            if (Place(d) is not { } at) continue;
            var v = Venues.At(d.Venue - 1);
            var sign = NeonSign.Make(Venues.NameOf(v), v.Type, v.Cuisine,
                letterHeight: Venues.IsHall(v.Type) ? 0.6f : 0.42f, maxWidth: Math.Max(3.2f, d.Width * 3.5f));
            sign.Transform = at;
            root ??= new Node3D { Name = NodeName };
            root.AddChild(sign);
            // its music, quiet, as the street hears it through the door (#1244)
            if (VenueMusic.AtDoor(d, v) is { } music) root.AddChild(music);
            // a hall's own word, big, high over its door (#1235): what tells a bowling centre from a
            // warehouse across the car park, the name smaller underneath
            if (!Venues.IsHall(v.Type)) continue;
            var big = NeonSign.Make(Venues.GenericName(v.Type).ToUpperInvariant(), v.Type, v.Cuisine,
                letterHeight: BigLetters, maxWidth: 14f, flicker: false);
            big.Transform = at.Translated(Vector3.Up * BigAbove);
            root.AddChild(big);
        }
        if (root != null) node.AddChild(root);
    }

    /// <summary>Where a door's sign hangs: centred over it, just off the wall, +Z facing the street. Null for a door with no outward.</summary>
    public static Transform3D? Place(DoorSpot d)
    {
        var outward = d.Outward with { Y = 0 };
        if (outward.LengthSquared() < 1e-4f) return null;
        outward = outward.Normalized();
        var basis = new Basis(Vector3.Up, Mathf.Atan2(outward.X, outward.Z));
        return new Transform3D(basis, d.Position + outward * 0.10f + Vector3.Up * (Math.Max(2.1f, d.Height) + 0.75f));
    }
}
