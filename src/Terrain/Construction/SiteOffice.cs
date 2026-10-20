using Godot;
using UnitSport.Interiors;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// A door of a site office (#822), in the site frame: <see cref="Sill"/> is the middle of the
/// doorway on its sill, on the facade plane (the front of the container's trim);
/// <see cref="Outward"/> the horizontal way out of the container. <see cref="Slot"/> is the site
/// building's door slot (<c>DoorKey</c>): the container's place across the office, 0 first.
/// <see cref="Level"/> is the storey it opens onto (#827): 1 for an upper container's, off the gallery.
/// </summary>
public readonly record struct SiteOfficeDoor(int Slot, Vector3 Sill, Vector2 Outward, float Width, float Height, int Level = 0);

/// <summary>
/// The site office of one building site (#822): the containers <see cref="SiteDressings"/> stacks
/// by the gate (#609), as the boxes it draws and as the building's doors and interior. The
/// interior's plan box is the ground row of containers, not the building's: its front wall
/// (interior −Z) is the containers' door end, so a door's doorway lines up with the container's
/// door and the <c>DoorLink</c> between them is a plain drop. All in the site frame (x along the
/// plan box's <c>AxisU</c>, z along its <c>AxisV</c>, from its centre; y the tile's height).
/// </summary>
public sealed class SiteOffice
{
    /// <summary>The ground the containers stand on: the drawn ground under the zone's middle.</summary>
    public float Ground { get; init; }

    /// <summary>The middle of the ground row's footprint, site frame (x, z).</summary>
    public Vector2 Center { get; init; }

    /// <summary>The way every door faces: along the containers' length, out of their door end.</summary>
    public Vector2 Outward { get; init; }

    /// <summary>Across the row of containers, as the interior's +X: so that its +Z (into it) is −<see cref="Outward"/>.</summary>
    public Vector2 Across => new(-Outward.Y, Outward.X);

    /// <summary>The ground row's extent across (<see cref="Across"/>) and along (the containers' length).</summary>
    public float Width { get; init; }
    public float Depth { get; init; }

    /// <summary>
    /// Every door, in slot order: one per ground-floor container, then the toilet cabins'
    /// (<see cref="Toilets"/>), then one per upper container where the office has its gallery (#827).
    /// </summary>
    public IReadOnlyList<SiteOfficeDoor> Doors { get; init; } = Array.Empty<SiteOfficeDoor>();

    /// <summary>
    /// The rooms, one per ground-floor container in slot order, as their extent in the interior's X.
    /// Two side by side meet at the middle of the gap between the containers, so they share a wall.
    /// The upper containers' rooms (#827) stand over them, the same extents.
    /// </summary>
    public IReadOnlyList<(float X0, float X1)> Rooms { get; init; } = Array.Empty<(float, float)>();

    /// <summary>Where the windows on a container's long faces are, as interior Z (their middles).</summary>
    public IReadOnlyList<float> WindowZ { get; init; } = Array.Empty<float>();

    /// <summary>
    /// The yard's toilet cabins (#822), each a door of the site after the containers' (its
    /// <see cref="SiteToilet.Door"/> is in <see cref="Doors"/> too) and a room of the same plan,
    /// standing where the cabin stands relative to the containers.
    /// </summary>
    public IReadOnlyList<SiteToilet> Toilets { get; init; } = Array.Empty<SiteToilet>();

    /// <summary>How many of <see cref="Doors"/> are the upper containers' (#827): the last ones; 0 with no gallery.</summary>
    public int UpperDoors { get; init; }

    /// <summary>The outside stair and the walkway along the upper row (#827), or null where the planner found no room for them.</summary>
    public SiteGallery? Gallery { get; init; }

    /// <summary>How many of <see cref="Doors"/> are the ground containers': the first ones.</summary>
    public int ContainerDoors => Doors.Count - Toilets.Count - UpperDoors;

    /// <summary>Whether door <paramref name="i"/> (its place in <see cref="Doors"/>) is a toilet cabin's.</summary>
    public bool IsCabin(int i) => i >= ContainerDoors && i < ContainerDoors + Toilets.Count;

    /// <summary>A site-frame point in the interior frame: (x across, z in from the door end).</summary>
    public Vector2 ToInterior(Vector2 p)
    {
        var d = p - Center;
        return new Vector2(d.Dot(Across), -d.Dot(Outward));
    }
}

/// <summary>
/// A toilet cabin of the yard (#822): its door, its footprint (site frame, x and z), the ground it
/// stands on and how high it is.
/// </summary>
public readonly record struct SiteToilet(SiteOfficeDoor Door, Vector2 Min, Vector2 Max, float Ground, float Height);

/// <summary>
/// A site office's gallery (#827): the steel walkway along the upper row's door end, its deck's top
/// the upper doors' sill, and the stair up to it along the outer long face of the row's end
/// container. Site frame. <see cref="Boxes"/> and <see cref="Ramps"/> are yard parts (they collide
/// with the yard, never with the building body an open office door lets a body through), drawn and
/// walked on by every peer and the server alike.
/// </summary>
public sealed class SiteGallery
{
    /// <summary>The row's side the stair stands on: −1 its low w end, +1 its high w end (the office zone's own terms).</summary>
    public int Side { get; init; }

    /// <summary>The deck's top: the upper containers' floor and their doors' sill.</summary>
    public float Deck { get; init; }

    /// <summary>How many steps the stair has (a riser each, the last onto the deck).</summary>
    public int Steps { get; init; }

    public List<ShellBox> Boxes { get; } = new();

    /// <summary>What a body stands on going up and down the stair: one ramp under the nosings, as a shell's flights.</summary>
    public List<ShellRamp> Ramps { get; } = new();

    /// <summary>On the ground just short of the stair's first step, in the middle of its lane: where a walker starts up.</summary>
    public Vector3 Foot { get; init; }

    /// <summary>The landing at the stair's top, on the deck.</summary>
    public Vector3 Top { get; init; }

    /// <summary>On the deck half a metre in front of each upper door, in the order of the upper doors.</summary>
    public IReadOnlyList<Vector3> Doorsteps { get; init; } = Array.Empty<Vector3>();
}

/// <summary>
/// Lays out the site office (#822): the containers <see cref="SiteDressings.Plan"/> draws, their
/// doors and the rooms behind them, from the office zone the planner placed (#606). A pure function
/// of the site and the ground, so the drawn containers, the doors every peer indexes and the
/// interior the server plans are the same containers.
/// </summary>
public static class SiteOffices
{
    public const float ContainerLength = SiteDressings.ContainerLength, ContainerWidth = SiteDressings.ContainerWidth,
        ContainerHeight = SiteDressings.ContainerHeight;

    /// <summary>Containers side by side stand this far apart.</summary>
    public const float Gap = 0.3f;
    /// <summary>The first container stands this far in from the zone's edge.</summary>
    public const float Margin = 0.15f;
    /// <summary>The door in a container's end: from this far across it, this wide and this tall from the ground.</summary>
    public const float DoorFrom = 0.7f, DoorWidth = 0.9f, DoorHeight = 2.1f;
    /// <summary>The drawn door panel stands from this high, over the container's bottom rail.</summary>
    public const float DoorPanelFrom = 0.1f;
    /// <summary>The frame rails stand this far proud of the container's skin: the facade plane is their face.</summary>
    public const float Trim = 0.02f;
    /// <summary>The drawn door panel stands this far either side of the container's end.</summary>
    public const float DoorPanel = 0.03f;
    /// <summary>The windows on a container's long faces: from this far along it, this long, and their sills and heads.</summary>
    public const float WindowFrom = 1.2f, WindowStep = 2.6f, WindowLength = 1.1f, WindowBottom = 1.0f, WindowTop = 2.0f;
    /// <summary>Containers two high.</summary>
    public const int Levels = 2;

    /// <summary>A toilet cabin: this wide and deep, this high, the first this far along the zone and the next this much further, this far in across it.</summary>
    public const float CabinSide = 1.1f, CabinHeight = 2.3f, CabinFrom = 0.2f, CabinStep = 1.25f, CabinW0 = 0.25f;
    /// <summary>A cabin's door, in the long face towards the office: from this far along it, this wide, this tall.</summary>
    public const float CabinDoorFrom = 0.15f, CabinDoorWidth = 0.8f, CabinDoorHeight = 2.0f;
    /// <summary>A cabin's doorway plane stands this far out from its face, in front of the drawn door (3 cm proud).</summary>
    public const float CabinPlane = 0.02f;
    /// <summary>Two cabins: a blue and a green one.</summary>
    public const int Cabins = 2;

    // ---- the gallery (#827) -------------------------------------------------------------------

    /// <summary>The walkway's clear depth out from the trim's face, and its deck's thickness under the sill.</summary>
    public const float GalleryDepth = 0.8f, DeckThickness = 0.1f;
    /// <summary>The railing's height over the deck (and the stair's handrail over a tread), its mid rail and toe board.</summary>
    public const float RailHeight = 1.1f, HandrailHeight = 1.0f, MidRail = 0.55f, ToeBoard = 0.12f;
    /// <summary>A rail or post's thickness.</summary>
    public const float RailThickness = 0.05f;
    /// <summary>The stair: this far off the containers' trim, this wide between its stringers.</summary>
    public const float StairGap = 0.05f, StairWidth = 0.9f;
    /// <summary>A step's rise at most and its going: the stair takes as many as its rise needs, between these.</summary>
    public const float StepRise = 0.2f, StepGoing = 0.23f;
    public const int MinSteps = 11, MaxSteps = 16;

    /// <summary>The stair's strip across the row's end: the gap, the lane and its handrail.</summary>
    public const float StairStrip = StairGap + StairWidth + RailThickness;

    /// <summary>A zone's extent in the site frame and its own terms: u along its long side, w across, from its low corner.</summary>
    private readonly record struct ZoneFrame(float X0, float Z0, float X1, float Z1)
    {
        public bool LongX => X1 - X0 >= Z1 - Z0;
        public float Length => LongX ? X1 - X0 : Z1 - Z0;
        public float Width => LongX ? Z1 - Z0 : X1 - X0;
        public Vector2 Centre => new((X0 + X1) / 2, (Z0 + Z1) / 2);
        public Vector2 U => LongX ? new Vector2(1, 0) : new Vector2(0, 1);
        public Vector2 W => LongX ? new Vector2(0, 1) : new Vector2(1, 0);
        public Vector2 At(float u, float w) => LongX ? new Vector2(X0 + u, Z0 + w) : new Vector2(X0 + w, Z0 + u);

        public void Box(List<ShellBox> boxes, float u0, float y0, float w0, float u1, float y1, float w1, ShellPart part, bool solid)
        {
            var a = At(u0, w0);
            var b = At(u1, w1);
            boxes.Add(new ShellBox(new Vector3(Math.Min(a.X, b.X), Math.Min(y0, y1), Math.Min(a.Y, b.Y)),
                new Vector3(Math.Max(a.X, b.X), Math.Max(y0, y1), Math.Max(a.Y, b.Y)), part, solid));
        }

        /// <summary>A zone's rectangle in the site frame (square to the site), from its corners.</summary>
        public static ZoneFrame Of(SiteRect rect, PlanBox box)
        {
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            foreach (var c in rect.Corners())
            {
                var d = c - box.Center;
                float lx = d.Dot(box.AxisU), lz = d.Dot(box.AxisV);
                x0 = Math.Min(x0, lx); x1 = Math.Max(x1, lx);
                z0 = Math.Min(z0, lz); z1 = Math.Max(z1, lz);
            }
            return new ZoneFrame(x0, z0, x1, z1);
        }
    }

    /// <summary>The site's zone of a kind, framed: square to the site, so its corners' bounds are the zone.</summary>
    private static ZoneFrame? FrameOf(ConstructionSite site, SiteZoneKind kind)
    {
        foreach (var zone in site.Zones)
            if (zone.Kind == kind) return ZoneFrame.Of(zone.Rect, site.Box);
        return null;
    }

    /// <summary>How the containers stand in an office zone: how many across, and where their length starts and ends.</summary>
    private static (int Across, float Cu0, float Cu1) Row(ZoneFrame f)
    {
        int across = Math.Max(1, (int)(f.Width / (ContainerWidth + Gap)));
        float cu0 = (f.Length - ContainerLength) / 2;
        return (across, cu0, cu0 + ContainerLength);
    }

    /// <summary>The width of a row of <paramref name="across"/> containers.</summary>
    private static float RowWidth(int across) => across * ContainerWidth + (across - 1) * Gap;

    /// <summary>
    /// The site office and the boxes its containers are drawn with, or null when the planner
    /// found no room for one (2 of the 569 real sites). Its doors are the containers' and then the
    /// toilet cabins' (<see cref="Toilets"/>): the cabins are rooms of the office's plan. Where the
    /// planner found room for the gallery (<see cref="ConstructionSite.GallerySide"/>, #827), the upper
    /// containers get their doors after the cabins', and the office its <see cref="SiteOffice.Gallery"/>.
    /// </summary>
    /// <param name="ground">The ground's height at a point of the site frame (x, z).</param>
    public static (SiteOffice Office, List<ShellBox> Boxes)? Plan(ConstructionSite site, Func<float, float, float> ground)
    {
        if (FrameOf(site, SiteZoneKind.Office) is not { } f) return null;
        float g = ground(f.Centre.X, f.Centre.Y);
        var boxes = new List<ShellBox>();
        void ZoneBox(float u0, float y0, float w0, float u1, float y1, float w1, ShellPart part, bool solid) =>
            f.Box(boxes, u0, y0, w0, u1, y1, w1, part, solid);

        // containers two high, side by side across the zone, their doors in the +u end
        var (across, cu0, cu1) = Row(f);
        var doors = new List<SiteOfficeDoor>();
        var outward = f.U;
        for (int i = 0; i < across; i++)
            for (int level = 0; level < Levels; level++)
            {
                float w0 = Margin + i * (ContainerWidth + Gap), y = g + level * ContainerHeight;
                ZoneBox(cu0, y, w0, cu1, y + ContainerHeight, w0 + ContainerWidth, ShellPart.Container, true);
                ZoneBox(cu0 - Trim, y, w0 - Trim, cu1 + Trim, y + 0.18f, w0 + ContainerWidth + Trim, ShellPart.ContainerTrim, false);
                ZoneBox(cu0 - Trim, y + ContainerHeight - 0.18f, w0 - Trim, cu1 + Trim, y + ContainerHeight, w0 + ContainerWidth + Trim, ShellPart.ContainerTrim, false);
                // two windows on each long face, and a door on the ground floor's end
                for (int wnd = 0; wnd < 2; wnd++)
                {
                    float wu = cu0 + WindowFrom + wnd * WindowStep;
                    ZoneBox(wu, y + WindowBottom, w0 - 0.03f, wu + WindowLength, y + WindowTop, w0 + ContainerWidth + 0.03f, ShellPart.Window, false);
                }
                if (level != 0) continue;
                ZoneBox(cu1 - DoorPanel, g + DoorPanelFrom, w0 + DoorFrom, cu1 + DoorPanel, g + DoorHeight, w0 + DoorFrom + DoorWidth,
                    ShellPart.ContainerTrim, false);
                // the doorway's plane is the trim's face, so the door's portal stands in front of
                // the drawn panel and covers it while the door is open
                var sill = f.At(cu1 + Trim, w0 + DoorFrom + DoorWidth / 2);
                doors.Add(new SiteOfficeDoor(i, new Vector3(sill.X, g, sill.Y), outward, DoorWidth, DoorHeight));
            }

        float rowW = RowWidth(across);
        var center = f.At((cu0 + cu1) / 2, Margin + rowW / 2);
        var office = new SiteOffice { Ground = g, Center = center, Outward = outward, Width = rowW, Depth = ContainerLength };
        // each container's room across the interior, meeting its neighbour in the gap's middle
        var rooms = new List<(float, float)>();
        for (int i = 0; i < across; i++)
        {
            float wa = Margin + i * (ContainerWidth + Gap) - (i > 0 ? Gap / 2 : 0f);
            float wb = Margin + i * (ContainerWidth + Gap) + ContainerWidth + (i < across - 1 ? Gap / 2 : 0f);
            float xa = office.ToInterior(f.At(cu0, wa)).X, xb = office.ToInterior(f.At(cu0, wb)).X;
            rooms.Add((Math.Min(xa, xb), Math.Max(xa, xb)));
        }
        var windows = new List<float>();
        for (int wnd = 0; wnd < 2; wnd++)
            windows.Add(office.ToInterior(f.At(cu0 + WindowFrom + wnd * WindowStep + WindowLength / 2, Margin)).Y);
        var toilets = Toilets(site, ground, across)?.Toilets ?? new List<SiteToilet>();
        foreach (var t in toilets) doors.Add(t.Door);

        // the gallery and the upper containers' doors (#827): their slots after the cabins', and
        // their drawn panels among the gallery's boxes, so the office is drawn box for box as before
        SiteGallery? gallery = null;
        int upper = 0;
        if (site.GallerySide != 0)
        {
            float y = g + ContainerHeight;
            var steps = new List<Vector3>();
            var panels = new List<ShellBox>();
            for (int i = 0; i < across; i++)
            {
                float w0 = Margin + i * (ContainerWidth + Gap);
                f.Box(panels, cu1 - DoorPanel, y + DoorPanelFrom, w0 + DoorFrom, cu1 + DoorPanel, y + DoorHeight, w0 + DoorFrom + DoorWidth,
                    ShellPart.ContainerTrim, false);
                var sill = f.At(cu1 + Trim, w0 + DoorFrom + DoorWidth / 2);
                doors.Add(new SiteOfficeDoor(across + toilets.Count + i, new Vector3(sill.X, y, sill.Y), outward, DoorWidth, DoorHeight, Level: 1));
                var step = f.At(cu1 + Trim + 0.5f, w0 + DoorFrom + DoorWidth / 2);
                steps.Add(new Vector3(step.X, y, step.Y));
                upper++;
            }
            gallery = Gallery(f, across, cu1, g, site.GallerySide, ground, steps);
            gallery.Boxes.AddRange(panels);
        }
        return (new SiteOffice
        {
            Ground = g, Center = center, Outward = outward, Width = rowW, Depth = ContainerLength,
            Doors = doors, Rooms = rooms, WindowZ = windows, Toilets = toilets, UpperDoors = upper, Gallery = gallery,
        }, boxes);
    }

    /// <summary>
    /// The gallery's extent in an office zone's own terms (#827): the stair's strip beside the row's
    /// end container on <paramref name="side"/> (−1 its low w end, +1 its high one), as far back as the
    /// longest stair, and the walkway's strip along the door end, the stair's landing included.
    /// (u0, w0, u1, w1) each.
    /// </summary>
    private static ((float U0, float W0, float U1, float W1) Stair, (float U0, float W0, float U1, float W1) Walk) Strips(ZoneFrame f, int side)
    {
        var (across, _, cu1) = Row(f);
        float r0 = Margin - Trim, r1 = Margin + RowWidth(across) + Trim;
        float s0 = side < 0 ? r0 - StairStrip : r1, s1 = side < 0 ? r0 : r1 + StairStrip;
        float face = cu1 + Trim;
        var stair = (face - (MaxSteps + 1) * StepGoing, s0, face, s1);
        var walk = (face, Math.Min(r0, s0), face + GalleryDepth + RailThickness, Math.Max(r1, s1));
        return (stair, walk);
    }

    /// <summary>
    /// What the gallery would stand on, tile frame (#827): the stair's strip and the walkway's, for
    /// the planner to test against everything else of the yard (<see cref="ConstructionSites.Plan"/>).
    /// </summary>
    public static (SiteRect Stair, SiteRect Walk) GalleryStrips(SiteRect officeZone, PlanBox box, int side)
    {
        var f = ZoneFrame.Of(officeZone, box);
        var (stair, walk) = Strips(f, side);
        SiteRect Rect((float U0, float W0, float U1, float W1) s)
        {
            var a = f.At(s.U0, s.W0);
            var b = f.At(s.U1, s.W1);
            var mid = (a + b) / 2;
            return new SiteRect(box.Center + box.AxisU * mid.X + box.AxisV * mid.Y, box.AxisU, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        }
        return (Rect(stair), Rect(walk));
    }

    /// <summary>The gallery's boxes, ramp and walking points (#827), on the side the planner chose.</summary>
    private static SiteGallery Gallery(ZoneFrame f, int across, float cu1, float g, int side, Func<float, float, float> ground, List<Vector3> doorsteps)
    {
        float deck = g + ContainerHeight, face = cu1 + Trim, outer = face + GalleryDepth;
        var (_, walk) = Strips(f, side);
        // the stair's lane, its inner edge off the end container's trim, and its handrail beyond
        float r0 = Margin - Trim, r1 = Margin + RowWidth(across) + Trim;
        float inner = side < 0 ? r0 - StairGap : r1 + StairGap;
        float laneOut = inner + side * StairWidth;
        float l0 = Math.Min(inner, laneOut), l1 = Math.Max(inner, laneOut);
        float railIn = side < 0 ? laneOut - RailThickness : laneOut, railOut = railIn + RailThickness;

        // as many steps as the rise from the ground at its foot needs, 0.2 m each at most
        float midW = (l0 + l1) / 2;
        var footAt = f.At(face - MinSteps * StepGoing, midW);
        float footGround = ground(footAt.X, footAt.Y);
        int steps = Math.Clamp((int)MathF.Ceiling((deck - footGround) / StepRise - 1e-3f), MinSteps, MaxSteps);
        float rise = Math.Min(StepRise, (deck - footGround) / steps);
        if (rise < 0.15f) rise = 0.15f;
        float start = face - steps * StepGoing;
        float bottom = deck - steps * rise;

        var gallery = new SiteGallery
        {
            Side = side, Deck = deck, Steps = steps,
            Foot = Point(start - 0.6f, midW, Math.Max(bottom, ground(f.At(start - 0.6f, midW).X, f.At(start - 0.6f, midW).Y))),
            Top = Point(face + GalleryDepth / 2, midW, deck),
            Doorsteps = doorsteps,
        };
        Vector3 Point(float u, float w, float y)
        {
            var p = f.At(u, w);
            return new Vector3(p.X, y, p.Y);
        }
        void Box(float u0, float y0, float w0, float u1, float y1, float w1, ShellPart part, bool solid) =>
            f.Box(gallery.Boxes, u0, y0, w0, u1, y1, w1, part, solid);

        // ---- the walkway: a galvanised grating deck on brackets out of the containers' ends -----
        float w0 = walk.W0, w1 = walk.W1;
        Box(face, deck - DeckThickness, w0, outer + RailThickness, deck, w1, ShellPart.Grating, true);
        // brackets under it at every container's edges, and under the landing's far edge
        var brackets = new List<float>();
        for (int i = 0; i < across; i++)
        {
            float c0 = Margin + i * (ContainerWidth + Gap);
            brackets.Add(c0 + 0.06f);
            brackets.Add(c0 + ContainerWidth - 0.06f);
        }
        brackets.Add(side < 0 ? w0 + 0.06f : w1 - 0.06f);
        foreach (float b in brackets)
            Box(face, deck - DeckThickness - 0.22f, b - 0.04f, outer + RailThickness, deck - DeckThickness, b + 0.04f, ShellPart.Frame, false);
        // the railing round its open sides: along the outer edge, and across both ends
        Railing(outer, w0, outer + RailThickness, w1, alongW: true);
        Railing(face, w0, outer, w0 + RailThickness, alongW: false);
        Railing(face, w1 - RailThickness, outer, w1, alongW: false);

        // ---- the stair: treads to look at over one ramp to stand on, a handrail on its open side ----
        for (int i = 0; i < steps; i++)
        {
            float a = start + i * StepGoing, b = a + StepGoing;
            float top = bottom + (i + 1) * rise;
            Box(a, top - 0.04f, l0, b, top, l1, ShellPart.Grating, false);
            // the stringers, stepped, under each tread's ends
            Box(a, top - 0.3f, l0 - RailThickness, b, top - 0.04f, l0, ShellPart.Frame, false);
            Box(a, top - 0.3f, l1, b, top - 0.04f, l1 + RailThickness, ShellPart.Frame, false);
            // the handrail and its stop: drawn a rail at hand height, solid from the tread up
            Box(a, top + HandrailHeight - RailThickness, railIn, b, top + HandrailHeight, railOut, ShellPart.Tube, false);
            Box(a, top, railIn, b, top + HandrailHeight, railOut, ShellPart.Invisible, true);
            if (i % 3 == 0) Box(a, top, railIn, a + RailThickness, top + HandrailHeight, railOut, ShellPart.Tube, false);
        }
        // the foot: a plinth down to the ground where it falls away under the first step
        float plinthGround = ground(f.At(start, midW).X, f.At(start, midW).Y);
        if (plinthGround < bottom - 0.03f)
            Box(start - StepGoing, Math.Max(plinthGround - 0.1f, bottom - 1.5f), l0, start + StepGoing, bottom, l1, ShellPart.Concrete, true);
        // the ramp runs from one going before the first step to the deck: at or just under the nosings
        var ra = Point(start - StepGoing, l0, bottom);
        var rb = Point(start - StepGoing, l1, bottom);
        var rc = Point(face, l1, deck);
        var rd = Point(face, l0, deck);
        gallery.Ramps.Add(new ShellRamp(ra, rb, rc, rd));
        return gallery;

        // a railing from (u0, w0) to (u1, w1) on the deck: posts, a top and a mid rail and a toe
        // board drawn; one solid slab to stop a body
        void Railing(float u0, float rw0, float u1, float rw1, bool alongW)
        {
            Box(u0, deck, rw0, u1, deck + RailHeight, rw1, ShellPart.Invisible, true);
            Box(u0, deck + RailHeight - RailThickness, rw0, u1, deck + RailHeight, rw1, ShellPart.Tube, false);
            Box(u0, deck + MidRail - RailThickness / 2, rw0, u1, deck + MidRail + RailThickness / 2, rw1, ShellPart.Tube, false);
            // the toe board 1 cm inside the rail's strip all round, so no post's face lies in one of its own
            Box(u0 + 0.01f, deck, rw0 + 0.01f, u1 - 0.01f, deck + ToeBoard, rw1 - 0.01f, ShellPart.Board, false);
            float length = alongW ? rw1 - rw0 : u1 - u0;
            int posts = Math.Max(1, (int)MathF.Ceiling(length / 2f));
            for (int k = 0; k <= posts; k++)
            {
                float t = Math.Min(length - RailThickness, k * length / posts);
                if (alongW) Box(u0, deck, rw0 + t, u1, deck + RailHeight, rw0 + t + RailThickness, ShellPart.Tube, false);
                else Box(u0 + t, deck, rw0, u0 + t + RailThickness, deck + RailHeight, rw1, ShellPart.Tube, false);
            }
        }
    }

    /// <summary>
    /// The yard's toilet cabins (#822, drawn since #609): a blue and a green cabin side by side along
    /// their zone, each with a door in the long face towards the office (towards the gate on a site
    /// without one), and the boxes they are drawn with. <paramref name="firstSlot"/> is the first
    /// cabin's door slot: the site's doors after the containers'. Null on a site with no toilets.
    /// </summary>
    public static (List<SiteToilet> Toilets, List<ShellBox> Boxes)? Toilets(ConstructionSite site, Func<float, float, float> ground, int firstSlot)
    {
        if (FrameOf(site, SiteZoneKind.Toilets) is not { } f) return null;
        float g = ground(f.Centre.X, f.Centre.Y);
        var boxes = new List<ShellBox>();
        var toilets = new List<SiteToilet>();
        var gate = site.Gate - site.Box.Center;
        var toward = FrameOf(site, SiteZoneKind.Office)?.Centre ?? new Vector2(gate.Dot(site.Box.AxisU), gate.Dot(site.Box.AxisV));
        for (int i = 0; i < Cabins; i++)
        {
            float u = CabinFrom + i * CabinStep;
            f.Box(boxes, u, g, CabinW0, u + CabinSide, g + CabinHeight, CabinW0 + CabinSide, i == 0 ? ShellPart.Toilet : ShellPart.ToiletAlt, true);
            // the door in the long face towards the office: the zone's side it stands on
            var middle = f.At(u + CabinSide / 2, CabinW0 + CabinSide / 2);
            bool high = (toward - middle).Dot(f.W) >= 0;
            float face = high ? CabinW0 + CabinSide : CabinW0, sign = high ? 1f : -1f;
            f.Box(boxes, u + CabinDoorFrom, g + DoorPanelFrom, face - DoorPanel, u + CabinDoorFrom + CabinDoorWidth, g + CabinDoorHeight, face + DoorPanel,
                ShellPart.ContainerTrim, false);
            var sill = f.At(u + CabinDoorFrom + CabinDoorWidth / 2, face + sign * CabinPlane);
            var a = f.At(u, CabinW0);
            var b = f.At(u + CabinSide, CabinW0 + CabinSide);
            toilets.Add(new SiteToilet(new SiteOfficeDoor(firstSlot + i, new Vector3(sill.X, g, sill.Y), f.W * sign, CabinDoorWidth, CabinDoorHeight),
                new Vector2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Vector2(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)), g, CabinHeight));
        }
        return (toilets, boxes);
    }
}
