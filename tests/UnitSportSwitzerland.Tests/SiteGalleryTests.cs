using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Construction;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The site office's gallery (#827): the outside stair and the walkway to the upper containers'
/// doors. What matters: the planner fits it after the yard, so every site is laid out as it was;
/// the ground and cabin doors and every office box are exactly the gallery-less office's; each
/// upper door stands on its container's door one container up, on the deck's top; the stair climbs
/// to the deck in steps of 0.2 m at most, under 45°; and nothing of it stands where the planner did
/// not test.
/// </summary>
public partial class SiteOfficeTests
{
    private static (ConstructionSite Site, SiteOffice Office, List<ShellBox> Boxes) WithGallery(string key, float w, float d, float h, byte floors, float turn, Vector2 street)
    {
        var made = Make(key, w, d, h, floors, turn, street);
        Assert.True(made.Site.GallerySide != 0, $"{key}: the planner found no room for the gallery");
        return made;
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void The_gallery_moves_nothing_of_the_yard_or_the_office(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        var (site, office, boxes) = Make(key, w, d, h, floors, turn, new Vector2(se, sn));
        var without = site with { GallerySide = 0 };
        var (plain, plainBoxes) = SiteOffices.Plan(without, Slope)!.Value;
        Assert.Null(plain.Gallery);
        Assert.Equal(0, plain.UpperDoors);
        // the ground and cabin doors are the gallery-less office's, slot for slot, and its boxes come first
        Assert.Equal(plain.Doors, office.Doors.Take(plain.Doors.Count));
        Assert.Equal(plainBoxes, boxes.Take(plainBoxes.Count));
        Assert.Equal(plain.Rooms, office.Rooms);
        Assert.Equal(plain.ContainerDoors, office.ContainerDoors);
        // and the yard: every box before the gallery's, and the rest of the site, as without it
        var yard = SiteDressings.Plan(site, Slope);
        var plainYard = SiteDressings.Plan(without, Slope);
        Assert.Equal(plainYard.Boxes, yard.Boxes.Take(plainYard.Boxes.Count));
        Assert.Equal(plainYard.Pallets, yard.Pallets);
        Assert.Equal(plainYard.Stock, yard.Stock);
        Assert.Equal(plainYard.Office!.Doors, yard.Office!.Doors.Take(plainYard.Office.Doors.Count));
        if (office.Gallery == null) return;
        Assert.Equal((plainYard.Boxes.Count, office.Gallery.Boxes.Count), yard.GalleryBoxes);
        Assert.Equal(office.Gallery.Boxes, yard.Boxes.Skip(plainYard.Boxes.Count));
        Assert.Equal(office.Gallery.Ramps, yard.Ramps);
    }

    /// <summary>Sites the planner fits a gallery on (found by a sweep of plain boxes, nothing round them): both sides, one and two stacks, every phase.</summary>
    public static IEnumerable<object[]> GallerySites() => new[]
    {
        new object[] { "2582_1102_61", 52f, 30f, 18.4f, (byte)6, 2.4f, 500f, 440f },   // a big block: two stacks
        new object[] { "2583_1117_132", 12.613085f, 17.160833f, 15f, (byte)2, 5.9909234f, 533.14246f, 449.98422f },
        new object[] { "2588_1108_186", 32.24082f, 10.782469f, 15f, (byte)4, 5.0546694f, 469.4016f, 551.6114f },
        new object[] { "2588_1100_149", 38.27043f, 12.373431f, 6.3f, (byte)3, 0.17938955f, 557.2796f, 482.13812f },
        new object[] { "2580_1109_143", 48.568436f, 15.429373f, 1.2f, (byte)2, 0.5460324f, 554.8694f, 524.2765f },
        new object[] { "2588_1103_108", 56.603836f, 29.975021f, 6.3f, (byte)2, 0.41196477f, 450.89948f, 465.5161f },
    };

    [Theory]
    [MemberData(nameof(GallerySites))]
    public void The_gallery_moves_nothing_where_it_stands(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        WithGallery(key, w, d, h, floors, turn, new Vector2(se, sn));
        The_gallery_moves_nothing_of_the_yard_or_the_office(key, w, d, h, floors, turn, se, sn);
    }

    [Theory]
    [MemberData(nameof(GallerySites))]
    public void A_site_has_the_same_gallery_every_time(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        var a = WithGallery(key, w, d, h, floors, turn, new Vector2(se, sn));
        var b = Make(key, w, d, h, floors, turn, new Vector2(se, sn));
        Assert.Equal(a.Site.GallerySide, b.Site.GallerySide);
        Assert.Equal(a.Office.Doors, b.Office.Doors);
        Assert.Equal(a.Office.Gallery!.Boxes, b.Office.Gallery!.Boxes);
        Assert.Equal(a.Office.Gallery.Ramps, b.Office.Gallery.Ramps);
        Assert.Equal(a.Office.Gallery.Doorsteps, b.Office.Gallery.Doorsteps);
    }

    [Theory]
    [MemberData(nameof(GallerySites))]
    public void An_upper_door_stands_on_its_container_s_door_one_container_up_on_the_deck(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        var (_, office, boxes) = WithGallery(key, w, d, h, floors, turn, new Vector2(se, sn));
        var gallery = office.Gallery!;
        int c = office.ContainerDoors, t = office.Toilets.Count;
        Assert.Equal(c, office.UpperDoors);
        Assert.Equal(office.Ground + SiteOffices.ContainerHeight, gallery.Deck, 4);
        var deck = Assert.Single(gallery.Boxes, x => x.Part == ShellPart.Grating && x.Solid);
        Assert.Equal(gallery.Deck, deck.Max.Y, 4);
        var upperContainers = boxes.Where(x => x.Part == ShellPart.Container && x.Min.Y > office.Ground + 1f).ToList();
        for (int i = 0; i < c; i++)
        {
            var ground = office.Doors[i];
            var up = office.Doors[c + t + i];
            Assert.Equal(c + t + i, up.Slot);
            Assert.Equal(1, up.Level);
            Assert.Equal(0, ground.Level);
            Assert.Equal(ground.Outward, up.Outward);
            Assert.Equal(ground.Width, up.Width, 4);
            Assert.Equal(ground.Height, up.Height, 4);
            // straight over the ground door, its sill on the deck's top
            Assert.Equal(ground.Sill.X, up.Sill.X, 4);
            Assert.Equal(ground.Sill.Z, up.Sill.Z, 4);
            Assert.Equal(gallery.Deck, up.Sill.Y, 4);
            // in the end of an upper container, its drawn panel there, and the deck in front of it
            var o = new Vector3(up.Outward.X, 0, up.Outward.Y);
            Assert.Single(upperContainers, x => Contains(x, up.Sill - o * 0.3f + Vector3.Up, 0f));
            Assert.Single(gallery.Boxes, x => x.Part == ShellPart.ContainerTrim && Contains(x, up.Sill - o * 0.04f + Vector3.Up, 0.001f));
            Assert.True(Contains(deck, up.Sill + o * 0.5f + Vector3.Down * 0.05f, 0f), $"{key}: no deck in front of upper door {up.Slot}");
            Assert.True(Contains(deck, gallery.Doorsteps[i] + Vector3.Down * 0.05f, 0f), $"{key}: upper door {up.Slot}'s doorstep off the deck");
            // nothing solid of the gallery in its 0.6 m in front of it, but the deck under it
            foreach (float step in new[] { 0.1f, 0.35f, 0.6f })
                Assert.DoesNotContain(gallery.Boxes, x => x.Solid && Contains(x, up.Sill + o * step + Vector3.Up, 0f));
        }
    }

    [Theory]
    [MemberData(nameof(GallerySites))]
    public void The_stair_climbs_to_the_deck_in_steps_a_body_can_take(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        var (_, office, boxes) = WithGallery(key, w, d, h, floors, turn, new Vector2(se, sn));
        var gallery = office.Gallery!;
        Assert.InRange(gallery.Steps, SiteOffices.MinSteps, SiteOffices.MaxSteps);
        var ramp = Assert.Single(gallery.Ramps);
        float low = Math.Min(ramp.A.Y, ramp.B.Y), high = Math.Max(ramp.C.Y, ramp.D.Y);
        Assert.Equal(gallery.Deck, high, 4);
        Assert.True((high - low) / gallery.Steps <= SiteOffices.StepRise + 1e-4f, $"{key}: risers of {(high - low) / gallery.Steps:F3} m");
        // the ramp's run against its rise: under 45°, and a face that is a plane
        var a = new Vector2(ramp.A.X, ramp.A.Z);
        var dd = new Vector2(ramp.D.X, ramp.D.Z);
        float run = a.DistanceTo(dd);
        Assert.True(MathF.Atan2(high - low, run) < MathF.PI / 4, $"{key}: the stair is {MathF.Atan2(high - low, run) * 180 / MathF.PI:F0}°");
        Assert.Equal(SiteOffices.StairWidth, new Vector2(ramp.A.X - ramp.B.X, ramp.A.Z - ramp.B.Z).Length(), 3);
        // the top of the ramp meets the deck's back edge, the foot is on its lane in front of it
        var deck = Assert.Single(gallery.Boxes, x => x.Part == ShellPart.Grating && x.Solid);
        var top = (ramp.C + ramp.D) / 2;
        Assert.True(top.X >= deck.Min.X - 0.01f && top.X <= deck.Max.X + 0.01f && top.Z >= deck.Min.Z - 0.01f && top.Z <= deck.Max.Z + 0.01f, $"{key}: the stair's top {top} off the deck");
        Assert.True(Contains(deck, gallery.Top + Vector3.Down * 0.05f, 0f));
        // beside the end container, never in it: the stair's ramp is outside every container
        foreach (var p in new[] { ramp.A, ramp.B, ramp.C, ramp.D, (ramp.A + ramp.C) / 2 })
            Assert.DoesNotContain(boxes, x => x.Part == ShellPart.Container && Contains(x, p + Vector3.Up * 0.5f, -0.001f));
        // a handrail to stop a body on its open side, and the railing round the deck
        Assert.True(gallery.Boxes.Count(x => x.Part == ShellPart.Invisible && x.Solid) >= gallery.Steps + 3);
    }

    [Theory]
    [MemberData(nameof(GallerySites))]
    public void The_gallery_stands_where_the_planner_tested_and_clear_of_the_yard(string key, float w, float d, float h, byte floors, float turn, float se, float sn)
    {
        var (site, office, _) = WithGallery(key, w, d, h, floors, turn, new Vector2(se, sn));
        var zone = site.Zones.First(z => z.Kind == SiteZoneKind.Office);
        var (stair, walk) = SiteOffices.GalleryStrips(zone.Rect, site.Box, site.GallerySide);
        // every box of it, in the tile's plan, in one of the two strips the planner tested
        Vector2 ToTile(float x, float z) => site.Box.Center + site.Box.AxisU * x + site.Box.AxisV * z;
        // (but the upper doors' panels, which are on the containers' ends)
        foreach (var b in office.Gallery!.Boxes.Where(x => x.Part != ShellPart.ContainerTrim))
            foreach (var p in new[] { ToTile(b.Min.X, b.Min.Z), ToTile(b.Max.X, b.Max.Z), ToTile(b.Min.X, b.Max.Z), ToTile(b.Max.X, b.Min.Z) })
                Assert.True(stair.Contains(p, 0.01f) || walk.Contains(p, 0.01f), $"{key}: a {b.Part} box of the gallery at {p} outside the strips");
        // and the strips: off every other zone, machine, the building's scaffold and the hoarding line
        foreach (var other in site.Zones.Where(z => z.Kind != SiteZoneKind.Office))
        {
            Assert.False(stair.Overlaps(other.Rect), $"{key}: the stair on the {other.Kind}");
            Assert.False(walk.Overlaps(other.Rect), $"{key}: the walkway over the {other.Kind}");
        }
        foreach (var m in site.Machines)
        {
            var (mw, ml) = ConstructionSites.MachineSize(m.Role);
            var facing = new Vector2(-MathF.Sin(m.Yaw), -MathF.Cos(m.Yaw));
            var r = new SiteRect(m.At, new Vector2(facing.Y, -facing.X), mw, ml);
            Assert.False(stair.Overlaps(r) || walk.Overlaps(r), $"{key}: the gallery on the {m.Role}");
        }
        var keepOut = new SiteRect(site.Box.Center, site.Box.AxisU, site.Box.Width + 2 * ConstructionSites.Scaffold, site.Box.Depth + 2 * ConstructionSites.Scaffold);
        Assert.False(stair.Overlaps(keepOut) || walk.Overlaps(keepOut), $"{key}: the gallery in the scaffolding");
        var board = SiteDressings.BoardRect(site.Gate, site.Front, site.GateWidth);
        Assert.False(stair.Overlaps(board) || walk.Overlaps(board), $"{key}: the gallery on the builder's board");
        foreach (var c in stair.Corners().Concat(walk.Corners()))
            Assert.True(site.Area.Contains(c, -0.049f), $"{key}: the gallery at {c} over the hoarding line");
    }

    [Fact]
    public void A_site_with_no_room_for_it_has_no_gallery_and_no_upper_doors()
    {
        // everything but the office zone's own ground is blocked: the office is placed, the gallery not
        var b = Box(24f, 15f, 6.3f, 4, 1.1f);
        var box = PlanBox.Of(b)!.Value;
        var free = ConstructionSites.Plan("2586_1115_51", b, box, new Vector2(560, 500), _ => false)!;
        var office = free.Zones.First(z => z.Kind == SiteZoneKind.Office).Rect;
        var tight = ConstructionSites.Plan("2586_1115_51", b, box, new Vector2(560, 500), p => !office.Contains(p, 0.05f) && free.Zones.Any(z => z.Kind == SiteZoneKind.Office))!;
        if (tight.Zones.FirstOrDefault(z => z.Kind == SiteZoneKind.Office) is null) return;
        Assert.Equal(0, tight.GallerySide);
        var (o, _) = SiteOffices.Plan(tight, Slope)!.Value;
        Assert.Null(o.Gallery);
        Assert.Equal(0, o.UpperDoors);
        Assert.All(o.Doors, x => Assert.Equal(0, x.Level));
    }
}
