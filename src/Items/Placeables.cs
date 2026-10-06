using Godot;

namespace UnitSport.Items;

/// <summary>
/// An item that Use puts in the world as a <see cref="PlacedObject"/> (#272): the flag, the campfire,
/// the field workbench. <see cref="FlagGhost"/> shows <see cref="Mesh"/> where it would go and
/// <see cref="ItemController"/> places it through <see cref="PlacedObjects.RequestPlace"/>.
/// </summary>
/// <param name="Verb">The ghost's hint: "{use_item}: <c>Verb</c>".</param>
/// <param name="MinNormalY">The flattest ground it needs (the up component of the surface normal).</param>
/// <param name="Raise">Lifted and stabbed down (the flag), rather than set down with a reach to the ground.</param>
public sealed record Placeable(ItemId Item, PlacedKind Kind, string Verb, string Done, string TooSteep,
    float MinNormalY, Func<ArrayMesh> Mesh, bool Raise);

public static class Placeables
{
    public static readonly Placeable[] All =
    {
        new(ItemId.SwissFlag, PlacedKind.Flag, "plant", "Flag planted.", "Too steep to plant a flag.", 0.6f, ItemDefs.PlantedFlagMesh, true),
        new(ItemId.Campfire, PlacedKind.Campfire, "light a fire", "Campfire lit: it burns 20 minutes.", "Too steep for a fire.",
            0.85f, Crafting.StationVisuals.CampfireMesh, false),
        new(ItemId.FieldWorkbench, PlacedKind.FieldWorkbench, "set it up", "Field workbench set up.", "Too steep for a bench.",
            0.85f, Crafting.StationVisuals.WorkbenchMesh, false),
    };

    public static Placeable? ForItem(ItemId id) => All.FirstOrDefault(p => p.Item == id);
    public static Placeable? ForKind(PlacedKind kind) => All.FirstOrDefault(p => p.Kind == kind);

    /// <summary>Taken back with an empty hand (a fire to put out, a bench to pack up); a flag is picked up holding a flag.</summary>
    public static bool TakenByHand(PlacedKind kind) => kind is PlacedKind.Campfire or PlacedKind.FieldWorkbench;

    /// <summary>What taking one back gives: the flag and the bench come back, a fire is spent.</summary>
    public static ItemId Refund(PlacedKind kind) => kind == PlacedKind.Campfire ? ItemId.None : ForKind(kind)?.Item ?? ItemId.None;

    /// <summary>The hint over something that can be taken back.</summary>
    public static string TakeVerb(PlacedObject o) => o.Kind switch
    {
        PlacedKind.Campfire => Crafting.CampfireClock.Burning(o.Payload, World.WorldClock.EnvNow) ? "put it out" : "clear the ashes",
        PlacedKind.FieldWorkbench => "pack it up",
        _ => "pick up",
    };
}
