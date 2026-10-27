using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// The sign over the main door of every venue (#1232, <see cref="DoorSpot.Venue"/>), by what it is
/// (#1250): bowling centres, cinemas, arcades, bars and clubs a neon sign, their OSM name and an icon
/// (<see cref="NeonSign"/>); restaurants, cafés, pubs, fast food and beer gardens a painted wooden board
/// (<see cref="PlankSign"/>), or nothing here when their name is on their awning's valance
/// (<see cref="VenueTerraces.HasAwning"/>). A hall that takes its whole building gets its glazed front
/// and canopy (<see cref="HallFront"/>), its name over the canopy and its big word near the roof.
/// Client only, on <see cref="ChunkManager.TileFurnished"/> like <see cref="BankSigns"/> (which leaves a
/// venue's door to this one); parented to the tile's node, so the signs unload with it.
/// </summary>
public partial class VenueSigns : Node
{
    private const string NodeName = "VenueSigns";

    /// <summary>A hall's big sign: its letters, and how far over the name sign it hangs (clear of the name's icon).</summary>
    private const float BigLetters = 1.2f, BigAbove = 2.4f;

    private readonly ChunkManager _chunks;
    private ShaderMaterial _material = null!;

    public VenueSigns(ChunkManager chunks)
    {
        Name = "VenueSigns";
        _chunks = chunks;
    }

    public VenueSigns() : this(null!) { }

    public override void _Ready()
    {
        _material = Styles.StyleKit.Material(Styles.MaterialRole.Prop);
        _material.SetShaderParameter("flicker", 0f);
        FogUniforms.Apply(_material);
        GameSettings.Changed += ApplyFog;
        _chunks.TileFurnished += OnFurnished;
        _chunks.TileUnfurnished += OnUnfurnished;
    }

    private void ApplyFog() => FogUniforms.Apply(_material);

    public override void _ExitTree()
    {
        GameSettings.Changed -= ApplyFog;
        if (_chunks == null) return;
        _chunks.TileFurnished -= OnFurnished;
        _chunks.TileUnfurnished -= OnUnfurnished;
    }

    private static void OnUnfurnished(TileId id, ChunkNode node) => node.GetNodeOrNull(NodeName)?.QueueFree();

    /// <summary>Whether a venue's sign is neon (#1250): the halls, bars and clubs; the places to eat are painted.</summary>
    public static bool Neon(VenueType t) => Venues.IsHall(t) || t is VenueType.Bar or VenueType.Nightclub;

    private void OnFurnished(TileId id, ChunkNode node, DoorSpot[] doors)
    {
        node.GetNodeOrNull(NodeName)?.QueueFree();
        Node3D? root = null;
        foreach (var d in doors)
        {
            if (d.Venue <= 0 || d.Width <= 0 || Frame(d) is null) continue;
            var v = Venues.At(d.Venue - 1);
            root ??= new Node3D { Name = NodeName };
            // its music, quiet, as the street hears it through the door (#1244)
            if (VenueMusic.AtDoor(d, v, new BuildingKey(id.E, id.N, d.Index)) is { } music) root.AddChild(music);
            if (Dress(d, v, _material) is { } dress) root.AddChild(dress);
        }
        if (root != null) node.AddChild(root);
    }

    /// <summary>
    /// What a venue's main door wears, in the tile's frame: its sign by kind, a whole hall's front, the
    /// hall's big word. Null for a door with no outward, or a place to eat whose name is on its awning.
    /// </summary>
    public static Node3D? Dress(DoorSpot d, VenueRecord v, Material prop)
    {
        if (Frame(d) is not { } frame) return null;
        float doorTop = Math.Max(2.1f, d.Height);
        if (!Neon(v.Type))
        {
            if (VenueTerraces.HasAwning(v)) return null; // lettered on the valance
            var plank = PlankSign.Make(Venues.NameOf(v), prop, maxWidth: Math.Max(2.4f, d.Width * 3f));
            plank.Transform = frame.Translated(frame.Basis * new Vector3(0, doorTop + 0.15f + PlankSign.BoardH / 2, 0.07f));
            return plank;
        }
        var root = new Node3D { Name = "VenueDress" };
        var front = HallFront.ShapeOf(d);
        if (front is { } shape)
        {
            var built = HallFront.Make(d, v, shape, prop);
            built.Transform = frame;
            root.AddChild(built);
        }
        float nameY = front?.NameY ?? doorTop + 0.75f;
        var sign = NeonSign.Make(Venues.NameOf(v), v.Type, v.Cuisine,
            letterHeight: Venues.IsHall(v.Type) ? 0.6f : 0.42f,
            maxWidth: front is { } f ? f.Width : Math.Max(3.2f, d.Width * 3.5f));
        sign.Transform = frame.Translated(frame.Basis * new Vector3(0, nameY, 0.10f));
        root.AddChild(sign);
        // a hall's own word, big (#1235): near the roof of a building that is all hall (#1250), else
        // high over its door. What tells a bowling centre from a warehouse across the car park.
        if (Venues.IsHall(v.Type) && BigY(d, nameY) is var (bigY, letters))
        {
            var big = NeonSign.Make(Venues.GenericName(v.Type).ToUpperInvariant(), v.Type, v.Cuisine,
                letterHeight: letters, maxWidth: 14f, flicker: false);
            big.Transform = frame.Translated(frame.Basis * new Vector3(0, bigY, 0.10f));
            root.AddChild(big);
        }
        return root;
    }

    /// <summary>
    /// Where a hall's big word stands (its centre over the door's foot) and its letters' height: under
    /// the roof of a whole hall, its letters smaller on a low one; null when it has no room over the name.
    /// Elsewhere (a hall in a mall) <see cref="BigAbove"/> over the name.
    /// </summary>
    public static (float Y, float Letters)? BigY(DoorSpot d, float nameY)
    {
        if (!d.WholeHall || d.Top <= d.Position.Y) return (nameY + BigAbove, BigLetters);
        float top = d.Top - d.Position.Y;
        foreach (float letters in new[] { BigLetters, 0.8f })
        {
            // the sign's icon stands 1.9 letters tall: its top half a metre under the roof's edge
            float y = top - 0.5f - letters * 0.95f;
            if (y - letters * 0.95f >= nameY + 0.7f) return (y, letters);
        }
        return null;
    }

    /// <summary>A door's frame: its foot, +Z out to the street, +X along the wall. Null for a door with no outward.</summary>
    public static Transform3D? Frame(DoorSpot d)
    {
        var outward = d.Outward with { Y = 0 };
        if (outward.LengthSquared() < 1e-4f) return null;
        outward = outward.Normalized();
        return new Transform3D(new Basis(Vector3.Up, Mathf.Atan2(outward.X, outward.Z)), d.Position);
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
