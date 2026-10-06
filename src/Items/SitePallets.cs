using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Items;

/// <summary>
/// One pallet standing in a site's yard (#583 phase 3): its building and its slot along the row
/// (which name it, <see cref="Pallets.YardId"/>), LV95 and the ground it stands on, its yaw (its
/// runners' heading: along the facade) and its load byte.
/// </summary>
public readonly record struct YardPallet(string Building, int Slot, double E, double N, double Height, float Yaw, byte Load,
    bool Site = false)
{
    /// <summary>A yard stack's id, or with <see cref="Site"/> a building site's materials pallet's (#615).</summary>
    public string Id => Site ? Pallets.SiteId(Building, Slot) : Pallets.YardId(Building, Slot);
}

/// <summary>
/// The pallets an industrial site keeps out on its apron (#583 phase 3), as a <b>pure function of
/// the yard</b>, like <see cref="DormantSlots.ForSite"/> beside it: every peer works out the same
/// stacks from the same tile bytes, and nothing about them is ever sent — only which have been
/// taken (<see cref="PalletService"/>). Godot maths only, tier 0 (<c>SitePalletTests</c>).
///
/// <para>
/// One row along the facade, <see cref="Out"/> from it: inside the apron the dormant fleet keeps
/// clear (<c>SiteYards.Apron</c>), so a pallet never stands in a lorry's place, and never within
/// <see cref="DoorClear"/> of a door, so a bay stays a bay. By trade: a warehouse's rows run along
/// its dock, a works keeps its raw stock at one end of its front, a haulier's depot has a few,
/// and a body shop or a dealership none — a forecourt is for cars.
/// </para>
///
/// <para>
/// A slot is a place along the row, numbered from one end, before anything is skipped: a door or
/// a dropped slot leaves a gap and never renumbers the rest, because the number names the pallet.
/// </para>
/// </summary>
public static class SitePallets
{
    /// <summary>From the facade to a pallet's centre, m: room behind it to walk, and in front for the forks.</summary>
    public const float Out = 2.2f;

    /// <summary>From one pallet's centre to the next along the row, m: a 1.2 m deck and a gap.</summary>
    public const float Pitch = 1.7f;

    /// <summary>No pallet's centre within this of a door's edge, m: a bay is reversed into.</summary>
    public const float DoorClear = 1.5f;

    /// <summary>How much of the facade a works keeps its stock along, from one end.</summary>
    public const float WorksEnd = 0.34f;

    /// <summary>The site types this file tells apart, as <see cref="SiteYard.SiteType"/> carries them (<c>Interiors.BuildingType</c>).</summary>
    private const int Warehouse = 3, Factory = 4, Depot = 5;

    /// <summary>How likely a free place on the row holds a pallet, by trade; 0 for none at all.</summary>
    public static float FillFor(int siteType) => siteType switch
    {
        Warehouse => 0.75f,
        Factory => 0.9f,
        Depot => 0.3f,
        _ => 0f,
    };

    /// <summary>
    /// The pallets on a site's apron, appended to <paramref name="into"/>, standing at the yard's
    /// own ground (the Godot half sets each on its own, <c>SiteYards.Pallets</c>).
    /// </summary>
    /// <param name="front">The yard, the facade's span along it and the doors in it.</param>
    /// <param name="apron">How far the yard's strip stands off the facade (<c>SiteYards.Apron</c>).</param>
    public static void ForSite(TileId id, SiteFront front, float apron, List<YardPallet> into)
    {
        var yard = front.Yard;
        float fill = FillFor(yard.SiteType);
        if (fill <= 0f) return;
        // the row along the facade itself, a little in from its corners; the yard is centred on
        // the main door, not on the facade
        float span = front.To - front.From;
        int places = (int)((span - 0.4f) / Pitch);
        if (places < 1) return;
        float first = (front.From + front.To) / 2 - (places - 1) * Pitch / 2;
        var doors = front.Doors;

        ulong yh = DormantSlots.Hash(DormantSlots.Key((long)(yard.X * 100), (long)(yard.Z * 100)), 0xBA11E7);
        // a works' stock is at one end of its front, which end the site's own roll
        bool fromLeft = (yh & 1) == 0;
        // v is out from the yard's middle; the facade is the yard's near edge less the apron
        float v = -(yard.Depth / 2 + apron) + Out;
        float cos = MathF.Cos(yard.Heading), sin = MathF.Sin(yard.Heading);

        for (int slot = 0; slot < places; slot++)
        {
            float u = first + slot * Pitch;
            if (yard.SiteType == Factory)
            {
                float t = (u - front.From) / span;   // 0 at one end of the facade, 1 at the other
                if (fromLeft ? t > WorksEnd : t < 1 - WorksEnd) continue;
            }
            bool atDoor = false;
            foreach (var d in doors)
                if (MathF.Abs(u - d.Along) < d.Half + DoorClear) { atDoor = true; break; }
            if (atDoor) continue;

            // the place's own roll, off its position, so a neighbour never changes it
            float x = yard.X + u * cos - v * sin;
            float z = yard.Z - u * sin - v * cos;
            ulong h = DormantSlots.Hash(DormantSlots.Key((long)(x * 100), (long)(z * 100)), 0x9A11E7);
            if ((h & 0xFFFF) / 65535.0 > fill) continue;

            // what is on it, and whether it is the square deck of an aisle (one in five)
            float roll = (h >> 20 & 0xFFFF) / 65536f;
            float depth = (h >> 40 & 7) < 2 ? Pallets.SquareDepth : Pallets.NarrowDepth;
            // runners along the facade: a node's +X at yaw y is (cos y, -sin y), the yard's along
            into.Add(new YardPallet(yard.Owner, slot, id.MinE + x, id.MaxN - z, yard.Y,
                DormantSlots.Wrap(yard.Heading), Pallets.LoadOf(roll, depth)));
        }
    }
}
