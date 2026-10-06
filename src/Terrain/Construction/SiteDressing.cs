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

/// <summary>What a site's yard holds, in the site frame: boxes and heaps.</summary>
public sealed class SiteDressingPlan
{
    public List<ShellBox> Boxes { get; } = new();
    public List<ShellMound> Mounds { get; } = new();
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
            // a box in the zone's own terms: u along its long side, w across, from its low corner
            void ZoneBox(float u0, float y0, float w0, float u1, float y1, float w1, ShellPart part, bool solid)
            {
                if (longX) Box(x0 + u0, y0, z0 + w0, x0 + u1, y1, z0 + w1, part, solid);
                else Box(x0 + w0, y0, z0 + u0, x0 + w1, y1, z0 + u1, part, solid);
            }
            float length = longX ? x1 - x0 : z1 - z0, width = longX ? z1 - z0 : x1 - x0;
            switch (zone.Kind)
            {
                case SiteZoneKind.Office:
                {
                    // containers two high, side by side across the zone
                    int across = Math.Max(1, (int)(width / (ContainerWidth + 0.3f)));
                    float u0 = (length - ContainerLength) / 2;
                    for (int i = 0; i < across; i++)
                        for (int level = 0; level < 2; level++)
                        {
                            float w0 = 0.15f + i * (ContainerWidth + 0.3f), y = g + level * ContainerHeight;
                            ZoneBox(u0, y, w0, u0 + ContainerLength, y + ContainerHeight, w0 + ContainerWidth, ShellPart.Container, true);
                            ZoneBox(u0 - 0.02f, y, w0 - 0.02f, u0 + ContainerLength + 0.02f, y + 0.18f, w0 + ContainerWidth + 0.02f, ShellPart.ContainerTrim, false);
                            ZoneBox(u0 - 0.02f, y + ContainerHeight - 0.18f, w0 - 0.02f, u0 + ContainerLength + 0.02f, y + ContainerHeight, w0 + ContainerWidth + 0.02f, ShellPart.ContainerTrim, false);
                            // two windows on each long face, and a door on the ground floor's street face
                            for (int wnd = 0; wnd < 2; wnd++)
                            {
                                float wu = u0 + 1.2f + wnd * 2.6f;
                                ZoneBox(wu, y + 1.0f, w0 - 0.03f, wu + 1.1f, y + 2.0f, w0 + ContainerWidth + 0.03f, ShellPart.Window, false);
                            }
                            if (level == 0)
                                ZoneBox(u0 + ContainerLength - 0.03f, g + 0.1f, w0 + 0.7f, u0 + ContainerLength + 0.03f, g + 2.1f, w0 + 1.6f, ShellPart.ContainerTrim, false);
                        }
                    break;
                }
                case SiteZoneKind.Toilets:
                    for (int i = 0; i < 2; i++)
                    {
                        float u = 0.2f + i * 1.25f;
                        ZoneBox(u, g, 0.25f, u + 1.1f, g + 2.3f, 1.35f, i == 0 ? ShellPart.Toilet : ShellPart.ToiletAlt, true);
                    }
                    break;
                case SiteZoneKind.Skips:
                {
                    int skips = Math.Max(1, (int)(length / 4.5f));
                    for (int i = 0; i < skips; i++)
                    {
                        float u = 0.2f + i * 4.5f;
                        ZoneBox(u, g, 0.35f, u + 4.0f, g + 1.4f, 2.25f, ShellPart.Skip, true);
                        ZoneBox(u + 0.15f, g + 1.4f, 0.5f, u + 3.85f, g + 1.6f, 2.1f, ShellPart.Debris, false);
                    }
                    break;
                }
                case SiteZoneKind.Materials:
                {
                    // a bundle of rebar the length of the zone, then a row of pallets and stacks
                    ZoneBox(0.2f, g, 0.2f, length - 0.2f, g + 0.35f, 0.8f, ShellPart.Rebar, true);
                    float u = 0.2f;
                    for (int i = 0; u + 1.4f < length; i++)
                    {
                        double roll = R($"mat{i}");
                        float w0 = 1.2f, w1 = Math.Min(width - 0.2f, w0 + 1.2f);
                        if (w1 - w0 < 0.6f) break;
                        if (roll < 0.3)
                        {
                            // a pallet of bricks
                            ZoneBox(u, g, w0, u + 1.2f, g + 0.15f, w1, ShellPart.Deck, true);
                            ZoneBox(u + 0.05f, g + 0.15f, w0 + 0.05f, u + 1.15f, g + 1.0f, w1 - 0.05f, ShellPart.Brick, true);
                            u += 1.6f;
                        }
                        else if (roll < 0.55)
                        {
                            // a pallet of cement bags
                            ZoneBox(u, g, w0, u + 1.2f, g + 0.15f, w1, ShellPart.Deck, true);
                            ZoneBox(u + 0.05f, g + 0.15f, w0 + 0.05f, u + 1.15f, g + 0.95f, w1 - 0.05f, ShellPart.Cement, true);
                            u += 1.6f;
                        }
                        else if (roll < 0.8)
                        {
                            // a stack of formwork panels
                            float l = Math.Min(2.7f, length - u - 0.2f);
                            ZoneBox(u, g, w0, u + l, g + 0.9f, w1, ShellPart.Formwork, true);
                            u += l + 0.4f;
                        }
                        else
                        {
                            // concrete rings, two high
                            ZoneBox(u, g, w0, u + 1.2f, g + 2.0f, w1, ShellPart.FreshConcrete, true);
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
            var at = gate + along * (site.GateWidth / 2 + 2.2f) - front * 0.6f;
            float g = ground(at.X, at.Y);
            bool alongX = MathF.Abs(along.X) > 0.5f;
            float hw = 1.5f;
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
        return plan;
    }
}
