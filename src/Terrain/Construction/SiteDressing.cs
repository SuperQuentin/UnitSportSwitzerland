using Godot;
using UnitSport.Core;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// A low-poly heap in the site frame (#609): an elliptic mound <see cref="RadiusX"/> by
/// <see cref="RadiusZ"/> across and <see cref="Height"/> high, its foot on the ground under its
/// centre. Walkable: its sides are well under a body's 52°.
/// </summary>
public readonly record struct ShellMound(Vector2 Center, float RadiusX, float RadiusZ, float Height, float Ground, ShellPart Part);

/// <summary>
/// A pallet of bricks or cement in a site's materials (#615): a pallet a machine with tines can lift
/// (<c>Items/Pallets</c>), not part of the dressing's mesh. <see cref="Slot"/> is its place in the
/// zone's row, counted before anything is skipped, so it names the pallet
/// (<c>Pallets.SiteId</c>); <see cref="Centre"/> is the middle of its underside in the site frame;
/// its runners run along the site's X, or else its Z.
/// </summary>
public readonly record struct SitePalletSpot(int Slot, Vector3 Centre, bool AlongX, byte Load);

/// <summary>
/// One searchable piece of a site's yard (#627): a skip, the rebar bundle, or a piece of the
/// materials' row (a pallet, a formwork stack, the concrete rings), as the axis-aligned box it stands
/// in, in the site frame. <see cref="Slot"/> names it (<c>SiteLoot</c>): a row piece's is its place in
/// the row, the same as its <see cref="SitePalletSpot.Slot"/> when it is a pallet a machine lifts.
/// </summary>
public readonly record struct SiteStockSpot(int Slot, SiteStockKind Kind, Vector3 Min, Vector3 Max);

/// <summary>What a site's yard holds, in the site frame: boxes, heaps, and the pallets a machine can lift.</summary>
public sealed class SiteDressingPlan
{
    public List<ShellBox> Boxes { get; } = new();
    public List<ShellMound> Mounds { get; } = new();
    public List<SitePalletSpot> Pallets { get; } = new();

    /// <summary>The site office (#822): its doors and the rooms behind them; null on a site with none.</summary>
    public SiteOffice? Office { get; set; }

    /// <summary>
    /// Which of <see cref="Boxes"/> are the office's containers: the solid ones are the walls a
    /// body is let through at an open office door, so they collide with the buildings, not the yard.
    /// </summary>
    public (int Start, int Count) OfficeBoxes { get; set; }

    /// <summary>Which of <see cref="Boxes"/> are the toilet cabins (#822): their walls collide with the buildings too.</summary>
    public (int Start, int Count) ToiletBoxes { get; set; }

    /// <summary>Which of <see cref="Boxes"/> are the office's gallery (#827): the walkway and the stair, the last ones.</summary>
    public (int Start, int Count) GalleryBoxes { get; set; }

    /// <summary>What a body stands on going up a stair of the yard (#827: the gallery's), collision only, as a shell's flights.</summary>
    public List<ShellRamp> Ramps { get; } = new();

    /// <summary>Whether box <paramref name="i"/> is a wall of a container or cabin with a door: the office's or a toilet's.</summary>
    public bool WithDoor(int i) =>
        i >= OfficeBoxes.Start && i < OfficeBoxes.Start + OfficeBoxes.Count
        || i >= ToiletBoxes.Start && i < ToiletBoxes.Start + ToiletBoxes.Count;

    /// <summary>What can be searched (#627): the skips and the materials, each a box (<c>SiteLoot</c>).</summary>
    public List<SiteStockSpot> Stock { get; } = new();
}

/// <summary>
/// Dresses a building site's yard (#609): the hoarding round it, the site office's containers,
/// the toilets, the skips, the materials and the soil heap, each standing in the zone
/// <see cref="ConstructionSites.Plan"/> gave it, on the ground under it. A pure function of the
/// site, so every peer dresses it alike. Every zone, run and slot the planner lays out is square
/// to the plan box, so everything here is an axis-aligned box in the site frame, like the shell's.
/// </summary>
public static class SiteDressings
{
    public const float FenceHeight = 2.0f;

    /// <summary>The builder's board: this far along the front past the gate's side, this far in from it, this wide by half.</summary>
    public const float BoardAlong = 2.2f, BoardIn = 0.6f, BoardHalfWidth = 1.5f;

    /// <summary>
    /// Where the builder's board stands, tile frame, with room round it (#827): the planner keeps the
    /// office gallery off it, as the dressing puts it by the gate after the planner has run.
    /// </summary>
    public static SiteRect BoardRect(Vector2 gate, Vector2 front, float gateWidth)
    {
        var along = new Vector2(-front.Y, front.X);
        return new SiteRect(gate + along * (gateWidth / 2 + BoardAlong) - front * BoardIn, along, 2 * BoardHalfWidth + 0.4f, 0.5f);
    }
    public const float ContainerLength = 6.06f, ContainerWidth = 2.44f, ContainerHeight = 2.6f;

    /// <param name="ground">The ground's height at a point of the site frame (x, z).</param>
    public static SiteDressingPlan Plan(ConstructionSite site, Func<float, float, float> ground)
    {
        var plan = new SiteDressingPlan();
        double R(string q) => Fnv.Unit(site.Key + "|dress|" + q);
        void Box(float x0, float y0, float z0, float x1, float y1, float z1, ShellPart part, bool solid) =>
            plan.Boxes.Add(new ShellBox(new Vector3(Math.Min(x0, x1), Math.Min(y0, y1), Math.Min(z0, z1)),
                new Vector3(Math.Max(x0, x1), Math.Max(y0, y1), Math.Max(z0, z1)), part, solid));
        Vector2 Local(Vector2 p)
        {
            var d = p - site.Box.Center;
            return new Vector2(d.Dot(site.Box.AxisU), d.Dot(site.Box.AxisV));
        }
        // a zone's extent in the site frame: square to it, so its corners' bounds are the zone
        (float X0, float Z0, float X1, float Z1) Extent(SiteRect r)
        {
            var c = r.Corners().Select(Local).ToArray();
            return (c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y));
        }

        // ---- the hoarding: mesh panels on concrete feet, a banner on some ------------------
        int panel = 0;
        foreach (var run in site.Hoarding)
        {
            var a = Local(run.A);
            var b = Local(run.B);
            float len = a.DistanceTo(b);
            int n = Math.Max(1, (int)MathF.Round(len / ConstructionSites.Panel));
            bool alongX = MathF.Abs(b.X - a.X) > MathF.Abs(b.Y - a.Y);
            for (int k = 0; k < n; k++, panel++)
            {
                var p0 = a.Lerp(b, (float)k / n);
                var p1 = a.Lerp(b, (float)(k + 1) / n);
                var mid = (p0 + p1) / 2;
                float g = ground(mid.X, mid.Y);
                // the panel, thin across the run: drawn as mesh, solid so nobody walks through
                if (alongX)
                {
                    Box(Math.Min(p0.X, p1.X), g + 0.12f, mid.Y - 0.02f, Math.Max(p0.X, p1.X), g + FenceHeight, mid.Y + 0.02f, ShellPart.Fence, true);
                    if (R($"banner{panel}") < 0.3)
                        Box(Math.Min(p0.X, p1.X) + 0.1f, g + 0.4f, mid.Y - 0.035f, Math.Max(p0.X, p1.X) - 0.1f, g + 1.8f, mid.Y + 0.035f, ShellPart.Banner, false);
                }
                else
                {
                    Box(mid.X - 0.02f, g + 0.12f, Math.Min(p0.Y, p1.Y), mid.X + 0.02f, g + FenceHeight, Math.Max(p0.Y, p1.Y), ShellPart.Fence, true);
                    if (R($"banner{panel}") < 0.3)
                        Box(mid.X - 0.035f, g + 0.4f, Math.Min(p0.Y, p1.Y) + 0.1f, mid.X + 0.035f, g + 1.8f, Math.Max(p0.Y, p1.Y) - 0.1f, ShellPart.Banner, false);
                }
                // a foot and a post at the panel's start (and its end, on the run's last)
                foreach (var post in k == n - 1 ? new[] { p0, p1 } : new[] { p0 })
                {
                    float gp = ground(post.X, post.Y);
                    float fx = alongX ? 0.3f : 0.1f, fz = alongX ? 0.1f : 0.3f;
                    Box(post.X - fx, gp - 0.05f, post.Y - fz, post.X + fx, gp + 0.15f, post.Y + fz, ShellPart.Concrete, true);
                    Box(post.X - 0.03f, gp + 0.1f, post.Y - 0.03f, post.X + 0.03f, gp + FenceHeight + 0.05f, post.Y + 0.03f, ShellPart.Tube, false);
                }
            }
            // a warning lamp on each end of a run
            foreach (var end in new[] { a, b })
            {
                float ge = ground(end.X, end.Y);
                Box(end.X - 0.09f, ge + FenceHeight + 0.05f, end.Y - 0.09f, end.X + 0.09f, ge + FenceHeight + 0.25f, end.Y + 0.09f, ShellPart.Lamp, false);
            }
        }

        // ---- the zones -------------------------------------------------------------------
        foreach (var zone in site.Zones)
        {
            var (x0, z0, x1, z1) = Extent(zone.Rect);
            float cx = (x0 + x1) / 2, cz = (z0 + z1) / 2;
            float g = ground(cx, cz);
            bool longX = x1 - x0 >= z1 - z0;
            float length = longX ? x1 - x0 : z1 - z0, width = longX ? z1 - z0 : x1 - x0;
            // the materials keep their low side (the rebar) toward the building and face the yard, where
            // a machine comes at their pallets from (#619): w then runs from the zone's side nearer the
            // building, whichever of its sides that is (the building's middle is the frame's origin)
            bool flip = zone.Kind == SiteZoneKind.Materials && (longX ? cz : cx) < 0;
            float W(float w) => flip ? width - w : w;
            // a box in the zone's own terms: u along its long side, w across, from its low corner
            void ZoneBox(float u0, float y0, float w0, float u1, float y1, float w1, ShellPart part, bool solid)
            {
                (w0, w1) = (Math.Min(W(w0), W(w1)), Math.Max(W(w0), W(w1)));
                if (longX) Box(x0 + u0, y0, z0 + w0, x0 + u1, y1, z0 + w1, part, solid);
                else Box(x0 + w0, y0, z0 + u0, x0 + w1, y1, z0 + u1, part, solid);
            }
            // a searchable piece in the same terms (#627), turned with the materials as their boxes are
            void Stock(int slot, SiteStockKind kind, float u0, float y0, float w0, float u1, float y1, float w1)
            {
                (w0, w1) = (Math.Min(W(w0), W(w1)), Math.Max(W(w0), W(w1)));
                plan.Stock.Add(longX
                    ? new SiteStockSpot(slot, kind, new Vector3(x0 + u0, y0, z0 + w0), new Vector3(x0 + u1, y1, z0 + w1))
                    : new SiteStockSpot(slot, kind, new Vector3(x0 + w0, y0, z0 + u0), new Vector3(x0 + w1, y1, z0 + u1)));
            }
            switch (zone.Kind)
            {
                case SiteZoneKind.Office:
                    // the containers, their doors and the rooms behind them (#822): SiteOffices
                    if (plan.Office != null || SiteOffices.Plan(site, ground) is not { } office) break;
                    plan.Office = office.Office;
                    plan.OfficeBoxes = (plan.Boxes.Count, office.Boxes.Count);
                    plan.Boxes.AddRange(office.Boxes);
                    break;
                case SiteZoneKind.Toilets:
                    // a blue and a green cabin, each with a door into the site's plan (#822): SiteOffices
                    if (SiteOffices.Toilets(site, ground, 0) is not { } cabins) break;
                    plan.ToiletBoxes = (plan.Boxes.Count, cabins.Boxes.Count);
                    plan.Boxes.AddRange(cabins.Boxes);
                    break;
                case SiteZoneKind.Skips:
                {
                    int skips = Math.Max(1, (int)(length / 4.5f));
                    for (int i = 0; i < skips; i++)
                    {
                        float u = 0.2f + i * 4.5f;
                        ZoneBox(u, g, 0.35f, u + 4.0f, g + 1.4f, 2.25f, ShellPart.Skip, true);
                        ZoneBox(u + 0.15f, g + 1.4f, 0.5f, u + 3.85f, g + 1.6f, 2.1f, ShellPart.Debris, false);
                        Stock(SiteLoot.FirstSkipSlot + i, SiteStockKind.Skip, u, g, 0.35f, u + 4.0f, g + 1.6f, 2.25f);
                    }
                    break;
                }
                case SiteZoneKind.Materials:
                {
                    // a bundle of rebar the length of the zone, then a row of pallets and stacks
                    ZoneBox(0.2f, g, 0.2f, length - 0.2f, g + 0.35f, 0.8f, ShellPart.Rebar, true);
                    Stock(SiteLoot.RebarSlot, SiteStockKind.Rebar, 0.2f, g, 0.2f, length - 0.2f, g + 0.35f, 0.8f);
                    float u = 0.2f;
                    for (int i = 0; u + 1.4f < length; i++)
                    {
                        double roll = R($"mat{i}");
                        float w0 = 1.2f, w1 = Math.Min(width - 0.2f, w0 + 1.2f);
                        if (w1 - w0 < 0.6f) break;
                        if (roll < 0.55)
                        {
                            // a pallet of bricks (shrink-wrapped) or of cement bags (sacks): a real
                            // pallet a machine with tines lifts (#615) where its deck fits, else drawn
                            bool bricks = roll < 0.3;
                            float depth = w1 - w0;
                            if (depth >= Items.Pallets.NarrowDepth - 0.01f)
                            {
                                float deck = depth >= Items.Pallets.SquareDepth - 0.01f ? Items.Pallets.SquareDepth : Items.Pallets.NarrowDepth;
                                // runners along the zone's long side, the deck centred in the slot
                                float mu = u + Items.Pallets.Length / 2, mw = (w0 + w1) / 2;
                                var centre = longX ? new Vector3(x0 + mu, g, z0 + W(mw)) : new Vector3(x0 + W(mw), g, z0 + mu);
                                plan.Pallets.Add(new SitePalletSpot(i, centre, longX, Items.Pallets.LoadOf(bricks ? 0.95f : 0.8f, deck)));
                                Stock(i, bricks ? SiteStockKind.Bricks : SiteStockKind.Cement, u, g, mw - deck / 2, u + Items.Pallets.Length, g + 1.0f, mw + deck / 2);
                            }
                            else
                            {
                                ZoneBox(u, g, w0, u + 1.2f, g + 0.15f, w1, ShellPart.Deck, true);
                                ZoneBox(u + 0.05f, g + 0.15f, w0 + 0.05f, u + 1.15f, g + (bricks ? 1.0f : 0.95f), w1 - 0.05f,
                                    bricks ? ShellPart.Brick : ShellPart.Cement, true);
                                Stock(i, bricks ? SiteStockKind.Bricks : SiteStockKind.Cement, u, g, w0, u + 1.2f, g + 1.0f, w1);
                            }
                            u += 1.6f;
                        }
                        else if (roll < 0.8)
                        {
                            // a stack of formwork panels
                            float l = Math.Min(2.7f, length - u - 0.2f);
                            ZoneBox(u, g, w0, u + l, g + 0.9f, w1, ShellPart.Formwork, true);
                            Stock(i, SiteStockKind.Formwork, u, g, w0, u + l, g + 0.9f, w1);
                            u += l + 0.4f;
                        }
                        else
                        {
                            // concrete rings, two high
                            ZoneBox(u, g, w0, u + 1.2f, g + 2.0f, w1, ShellPart.FreshConcrete, true);
                            Stock(i, SiteStockKind.Rings, u, g, w0, u + 1.2f, g + 2.0f, w1);
                            u += 1.6f;
                        }
                    }
                    break;
                }
                case SiteZoneKind.SoilHeap:
                {
                    float rx = (x1 - x0) / 2 * 0.95f, rz = (z1 - z0) / 2 * 0.95f;
                    plan.Mounds.Add(new ShellMound(new Vector2(cx, cz), rx, rz, Math.Min(rx, rz) * 0.5f, g, ShellPart.Soil));
                    break;
                }
            }
        }

        // ---- the builder's board, by the gate, facing the street --------------------------
        {
            var gate = Local(site.Gate);
            var front = new Vector2(site.Front.Dot(site.Box.AxisU), site.Front.Dot(site.Box.AxisV));
            var along = new Vector2(-front.Y, front.X);
            var at = gate + along * (site.GateWidth / 2 + BoardAlong) - front * BoardIn;
            float g = ground(at.X, at.Y);
            bool alongX = MathF.Abs(along.X) > 0.5f;
            float hw = BoardHalfWidth;
            if (alongX)
            {
                Box(at.X - hw, g + 1.0f, at.Y - 0.04f, at.X + hw, g + 3.0f, at.Y + 0.04f, ShellPart.SignBoard, true);
                Box(at.X - hw + 0.1f, g + 2.4f, at.Y - 0.06f, at.X + hw - 0.1f, g + 2.8f, at.Y + 0.06f, ShellPart.Banner, false);
            }
            else
            {
                Box(at.X - 0.04f, g + 1.0f, at.Y - hw, at.X + 0.04f, g + 3.0f, at.Y + hw, ShellPart.SignBoard, true);
                Box(at.X - 0.06f, g + 2.4f, at.Y - hw + 0.1f, at.X + 0.06f, g + 2.8f, at.Y + hw - 0.1f, ShellPart.Banner, false);
            }
            foreach (float s in new[] { -hw + 0.2f, hw - 0.2f })
            {
                var p = alongX ? new Vector2(at.X + s, at.Y) : new Vector2(at.X, at.Y + s);
                Box(p.X - 0.05f, g, p.Y - 0.05f, p.X + 0.05f, g + 1.0f, p.Y + 0.05f, ShellPart.Tube, false);
            }
        }
        // the office's gallery (#827), last, so no box before it moves: yard boxes, walked on and
        // collided with the yard, never with the building body an open office door lets a body through
        if (plan.Office?.Gallery is { } gallery)
        {
            plan.GalleryBoxes = (plan.Boxes.Count, gallery.Boxes.Count);
            plan.Boxes.AddRange(gallery.Boxes);
            plan.Ramps.AddRange(gallery.Ramps);
        }
        return plan;
    }
}
