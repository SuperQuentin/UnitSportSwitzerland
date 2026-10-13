using Godot;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// A door of a site office (#822), in the site frame: <see cref="Sill"/> is the middle of the
/// doorway on its sill, on the facade plane (the front of the container's trim);
/// <see cref="Outward"/> the horizontal way out of the container. <see cref="Slot"/> is the site
/// building's door slot (<c>DoorKey</c>): the container's place across the office, 0 first.
/// </summary>
public readonly record struct SiteOfficeDoor(int Slot, Vector3 Sill, Vector2 Outward, float Width, float Height);

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

    /// <summary>One door per ground-floor container, slot 0 first.</summary>
    public IReadOnlyList<SiteOfficeDoor> Doors { get; init; } = Array.Empty<SiteOfficeDoor>();

    /// <summary>
    /// The rooms, one per ground-floor container in slot order, as their extent in the interior's X.
    /// Two side by side meet at the middle of the gap between the containers, so they share a wall.
    /// </summary>
    public IReadOnlyList<(float X0, float X1)> Rooms { get; init; } = Array.Empty<(float, float)>();

    /// <summary>Where the windows on a container's long faces are, as interior Z (their middles).</summary>
    public IReadOnlyList<float> WindowZ { get; init; } = Array.Empty<float>();

    /// <summary>A site-frame point in the interior frame: (x across, z in from the door end).</summary>
    public Vector2 ToInterior(Vector2 p)
    {
        var d = p - Center;
        return new Vector2(d.Dot(Across), -d.Dot(Outward));
    }
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

    /// <summary>
    /// The site's office and the boxes it is drawn with, or null when the planner found no room
    /// for one (2 of the 569 real sites).
    /// </summary>
    /// <param name="ground">The ground's height at a point of the site frame (x, z).</param>
    public static (SiteOffice Office, List<ShellBox> Boxes)? Plan(ConstructionSite site, Func<float, float, float> ground)
    {
        SiteZone? zone = null;
        foreach (var z in site.Zones)
            if (z.Kind == SiteZoneKind.Office) { zone = z; break; }
        if (zone == null) return null;

        // the zone's extent in the site frame: square to it, so its corners' bounds are the zone
        float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
        foreach (var c in zone.Rect.Corners())
        {
            var d = c - site.Box.Center;
            float lx = d.Dot(site.Box.AxisU), lz = d.Dot(site.Box.AxisV);
            x0 = Math.Min(x0, lx); x1 = Math.Max(x1, lx);
            z0 = Math.Min(z0, lz); z1 = Math.Max(z1, lz);
        }
        float g = ground((x0 + x1) / 2, (z0 + z1) / 2);
        bool longX = x1 - x0 >= z1 - z0;
        float length = longX ? x1 - x0 : z1 - z0, width = longX ? z1 - z0 : x1 - x0;
        // a point and a box in the zone's own terms: u along its long side, w across, from its low corner
        Vector2 At(float u, float w) => longX ? new Vector2(x0 + u, z0 + w) : new Vector2(x0 + w, z0 + u);
        var boxes = new List<ShellBox>();
        void ZoneBox(float u0, float y0, float w0, float u1, float y1, float w1, ShellPart part, bool solid)
        {
            var a = At(u0, w0);
            var b = At(u1, w1);
            boxes.Add(new ShellBox(new Vector3(Math.Min(a.X, b.X), Math.Min(y0, y1), Math.Min(a.Y, b.Y)),
                new Vector3(Math.Max(a.X, b.X), Math.Max(y0, y1), Math.Max(a.Y, b.Y)), part, solid));
        }

        // containers two high, side by side across the zone, their doors in the +u end
        int across = Math.Max(1, (int)(width / (ContainerWidth + Gap)));
        float cu0 = (length - ContainerLength) / 2, cu1 = cu0 + ContainerLength;
        var doors = new List<SiteOfficeDoor>();
        var outward = longX ? new Vector2(1, 0) : new Vector2(0, 1);
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
                var sill = At(cu1 + Trim, w0 + DoorFrom + DoorWidth / 2);
                doors.Add(new SiteOfficeDoor(i, new Vector3(sill.X, g, sill.Y), outward, DoorWidth, DoorHeight));
            }

        float rowW = across * ContainerWidth + (across - 1) * Gap;
        var center = At((cu0 + cu1) / 2, Margin + rowW / 2);
        var office = new SiteOffice { Ground = g, Center = center, Outward = outward, Width = rowW, Depth = ContainerLength };
        // each container's room across the interior, meeting its neighbour in the gap's middle
        var rooms = new List<(float, float)>();
        for (int i = 0; i < across; i++)
        {
            float wa = Margin + i * (ContainerWidth + Gap) - (i > 0 ? Gap / 2 : 0f);
            float wb = Margin + i * (ContainerWidth + Gap) + ContainerWidth + (i < across - 1 ? Gap / 2 : 0f);
            float xa = office.ToInterior(At(cu0, wa)).X, xb = office.ToInterior(At(cu0, wb)).X;
            rooms.Add((Math.Min(xa, xb), Math.Max(xa, xb)));
        }
        var windows = new List<float>();
        for (int wnd = 0; wnd < 2; wnd++)
            windows.Add(office.ToInterior(At(cu0 + WindowFrom + wnd * WindowStep + WindowLength / 2, Margin)).Y);
        return (new SiteOffice
        {
            Ground = g, Center = center, Outward = outward, Width = rowW, Depth = ContainerLength,
            Doors = doors, Rooms = rooms, WindowZ = windows,
        }, boxes);
    }
}
