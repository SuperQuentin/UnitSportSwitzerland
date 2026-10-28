using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// The glazed street front of a dining venue or a shop unit with a door of its own (#1292,
/// <see cref="DoorSpot.Unit"/>): 1.1 m bays across its room's width on the street, the door among them, a sill
/// and a top over the ground storey's window grid. The sizes are shared by the footprint (how wide a venue's
/// front is), the planner (the room behind it, its windows at the bays) and the mesh.
/// </summary>
public static class VenueFront
{
    /// <summary>A bay of glass, m: HallFront's.</summary>
    public const float Bay = 1.1f;

    /// <summary>The glass's sill and top over the pavement, m [owner, #1292]; the top is capped under the storey's ceiling.</summary>
    public const float Sill = 0.4f, Top = 2.6f;

    /// <summary>A front narrower than this (the door and a bay each side) is no front: the door stays a plain door.</summary>
    public const float MinSpan = 3.2f;

    /// <summary>
    /// How many bays a venue's front is when it is not a shop unit's (an office block, a school, a house; owner,
    /// #1292): a café, a kebab or a fast food 5, a restaurant, a pub or a bar 7, a club 9. The door is one of them.
    /// </summary>
    public static int BaysFor(VenueType t) => t switch
    {
        VenueType.Cafe or VenueType.FastFood => 5,
        VenueType.Nightclub => 9,
        _ => 7,
    };

    /// <summary>The span of a venue's front by its kind, its door <paramref name="doorWidth"/> wide.</summary>
    public static float SpanFor(VenueType t, float doorWidth) => doorWidth + (BaysFor(t) - 1) * Bay;
}
