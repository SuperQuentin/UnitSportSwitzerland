using Godot;
using UnitSport.Player;
using static UnitSport.Avatar.HeavyMesh;

namespace UnitSport.Avatar;

/// <summary>
/// A low-poly full-size pickup (#463), the F-150 Raptor's SuperCrew at its real dimensions: the long
/// hood over its grille and amber marker lamps, flared arches over the 35-inch tyres, a four-door
/// cab hollow above its floor with a raked windscreen and the cockpit in it (the heavy one, #157),
/// a 5.5 ft bed with its tailgate, the receiver and the tow ball under the rear bumper. Authored
/// facing +Z with the origin on the ground under the centre of mass (z = cg − metres behind the
/// front), as the trucks; colours from <see cref="HeavyLook"/>. Wheels are the rig's.
/// </summary>
public static class PickupMeshBuilder
{
    /// <summary>Stations, metres behind the bumper: the windscreen's foot, the cab's back wall, the tailgate.</summary>
    private const float WsFoot = 1.75f, CabBack = 4.12f, Tailgate = 5.8f;
    /// <summary>Heights: the cab floor, the bonnet and window line, the roof.</summary>
    private const float Floor = 0.62f, Belt = 1.28f, Roof = 1.99f, Sill = 0.5f, BedFloor = 0.9f, BedRail = 1.3f;
    /// <summary>The B-pillar between the doors, metres behind the bumper; the side glass's top.</summary>
    private const float BPillar = 3.0f, GlassTop = 1.86f;

    /// <summary>The hull (<c>Truck.HullBoxes</c>): the body up to the bonnet and bed rails, then only the cab, from behind the windscreen's rake.</summary>
    public const float BodyTop = BedRail + 0.03f, CabFrom = WsFoot + 0.35f, CabTo = CabBack;

    /// <summary>
    /// The door bits (as a car's, <see cref="CarRig"/>): the driver's door is <see cref="CarRig.DriverDoor"/>
    /// on this left-hand-drive cab, so getting in and out opens the right one.
    /// </summary>
    private static int DoorIndex(bool left, bool rear) => (rear ? 2 : 0) + (left ? 1 : 0);

    public static HeavyParts Build(HeavySpec spec, int section, float load)
    {
        var s = spec.Sections[section];
        var look = spec.Look;
        float cg = Cg(s, load);
        float hw = s.Width * 0.5f;
        float bodyW = s.Width - 0.15f;           // the flares stand out past the body
        const float wall = 0.07f;
        float inner = bodyW * 0.5f - wall;
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var rev = new MeshScratch();
        float front = cg;
        float r = Tyre.Radius(s.Axles[0].Tyre);
        float Z(float at) => cg - at;

        // ---- the front: bumper, skid plate, grille, bonnet, the body cut round the wheels ----
        Along(m, cg, 0f, 0.2f, 0.4f, 0.74f, bodyW - 0.05f, look.Lower);
        Along(m, cg, 0.15f, 1.0f, 0.3f, 0.36f, 1.2f, Steel);
        Along(m, cg, 0.1f, 0.2f, 0.74f, 1.24f, bodyW - 0.3f, look.Accent);
        Skirt(m, s, cg, 0.12f, WsFoot, Sill, 0.95f, bodyW, look.Paint);
        Along(m, cg, 0.2f, WsFoot, 0.95f, Belt, bodyW, look.Paint);
        // the bonnet's raised middle and its vents
        Along(m, cg, 0.45f, WsFoot - 0.05f, Belt, Belt + 0.05f, 1.0f, look.Paint);
        foreach (float sx in new[] { -1f, 1f })
            Along(m, cg, 0.7f, 1.0f, Belt + 0.05f, Belt + 0.06f, 0.18f, look.Accent, sx * 0.3f);
        // flared arches over every wheel, in the dark cladding
        foreach (var a in s.Axles)
            Along(m, cg, a.At - r - 0.12f, a.At + r + 0.12f, 2f * r * 0.97f + 0.06f, 2f * r * 0.97f + 0.2f, s.Width - 0.02f, look.Lower);

        // ---- the cab: hollow above its floor, glass you see through ----
        Along(m, cg, WsFoot, CabBack, 0.42f, Floor, bodyW, look.Lower);
        Along(m, cg, WsFoot + wall, CabBack - wall, Floor, Floor + 0.01f, inner * 2f, HeavyCabin.FloorColour);
        // running boards under the doors
        Along(m, cg, WsFoot + 0.1f, CabBack - 0.1f, 0.36f, 0.42f, s.Width - 0.1f, Trim);
        // the cowl at the windscreen's foot (the doors are leaves of their own, below)
        Along(m, cg, WsFoot - 0.05f, WsFoot + 0.12f, Floor, Belt + 0.03f, bodyW, look.Paint);
        // the raked windscreen, its pillars, the roof and the headlining
        const float wsTopAt = 2.5f, wsTop = 1.9f;
        float half = bodyW * 0.5f - 0.1f;
        m.Pane(new[]
        {
            new Vector3(-half, Belt + 0.03f, Z(WsFoot + 0.1f)), new Vector3(half, Belt + 0.03f, Z(WsFoot + 0.1f)),
            new Vector3(half - 0.04f, wsTop, Z(wsTopAt)), new Vector3(-half + 0.04f, wsTop, Z(wsTopAt)),
        }, PaneTint);
        foreach (float sx in new[] { -1f, 1f })
            m.Tube(new Vector3(sx * (half + 0.04f), Belt + 0.03f, Z(WsFoot + 0.1f)), new Vector3(sx * (half + 0.01f), Roof - 0.06f, Z(wsTopAt)), 0.05f, look.Paint, 4);
        Along(m, cg, wsTopAt - 0.05f, CabBack, Roof - 0.09f, Roof, bodyW - 0.06f, look.Paint);
        Along(m, cg, wsTopAt, CabBack - wall, Roof - 0.1f, Roof - 0.09f, inner * 2f, HeavyCabin.Lining);
        // the fixed side: the B-pillar between the doors, the strip behind the rear one, the rail over them
        foreach (float sx in new[] { -1f, 1f })
        {
            float xw = sx * (bodyW * 0.5f - wall * 0.5f);
            Along(m, cg, BPillar - 0.05f, BPillar + 0.05f, Floor, Roof - 0.09f, wall, look.Paint, xw);
            Along(m, cg, CabBack - 0.12f, CabBack, Floor, Roof - 0.09f, wall, look.Paint, xw);
            Along(m, cg, wsTopAt, CabBack, GlassTop, Roof - 0.09f, wall, look.Paint, xw);
        }
        // four doors, each a leaf hinged at its front edge and swinging out (#463): the panel to the
        // window line, a dark handle, the glass to the rail (the front one's under the pillar's rake)
        var leaves = new List<HeavyDoorLeaf>();
        foreach (float sx in new[] { -1f, 1f })
            foreach (bool rear in new[] { false, true })
            {
                float at0 = rear ? BPillar + 0.05f : WsFoot + 0.12f, at1 = rear ? CabBack - 0.12f : BPillar - 0.05f;
                float xw = sx * (bodyW * 0.5f - wall * 0.5f), x = sx * bodyW * 0.5f;
                var leaf = new MeshScratch();
                Along(leaf, cg, at0 + 0.01f, at1 - 0.01f, Floor - 0.12f, Belt + 0.04f, wall, look.Paint, xw);
                Along(leaf, cg, at1 - 0.32f, at1 - 0.12f, Belt - 0.12f, Belt - 0.08f, 0.03f, look.Accent, x + sx * 0.015f);
                if (rear) SidePane(leaf, x, Z(at0 + 0.04f), Z(at1 - 0.04f), Belt + 0.04f, GlassTop);
                else
                    leaf.Pane(new[]
                    {
                        new Vector3(x, Belt + 0.04f, Z(at0 + 0.08f)), new Vector3(x, Belt + 0.04f, Z(at1 - 0.04f)),
                        new Vector3(x, GlassTop, Z(at1 - 0.04f)), new Vector3(x, GlassTop, Z(wsTopAt + 0.05f)),
                    }, PaneTint);
                var pivot = new Vector3(x, 0f, Z(at0));
                // node space: the left side is −X; front-hinged, a left door swings out with a negative turn
                leaves.Add(new HeavyDoorLeaf(DoorIndex(sx > 0, rear), leaf.Build(pivot), new Vector3(-pivot.X, 0f, -pivot.Z), sx > 0 ? -1.15f : 1.15f)
                {
                    Centre = new Vector3(-x, (Floor + GlassTop) * 0.5f, -Z((at0 + at1) * 0.5f)),
                });
            }
        // the back wall and its window
        Along(m, cg, CabBack - wall, CabBack, Floor, 1.36f, bodyW, look.Paint);
        Along(m, cg, CabBack - wall, CabBack, 1.82f, Roof - 0.09f, bodyW, look.Paint);
        foreach (float sx in new[] { -1f, 1f })
            Along(m, cg, CabBack - wall, CabBack, 1.36f, 1.82f, bodyW * 0.5f - 0.7f, look.Paint, sx * (bodyW * 0.25f + 0.35f));
        FrontPane(m, Z(CabBack) - 0.01f, 0.7f, 1.36f, 1.82f);
        // the transmission tunnel down the middle
        Along(m, cg, WsFoot + 0.4f, CabBack - 0.3f, Floor, Floor + 0.16f, 0.32f, HeavyCabin.Dash);

        // mirrors out on their arms, on the doors
        var mirrors = new List<(string, Vector3, Vector2)>();
        foreach (float sx in new[] { -1f, 1f })
        {
            m.Tube(new Vector3(sx * (bodyW * 0.5f), Belt + 0.1f, Z(WsFoot + 0.3f)), new Vector3(sx * (hw + 0.1f), Belt + 0.18f, Z(WsFoot + 0.36f)), 0.025f, Trim, 4);
            mirrors.Add((sx > 0 ? "MirrorLeft" : "MirrorRight", new Vector3(sx * (hw + 0.12f), Belt + 0.24f, Z(WsFoot + 0.4f)), new Vector2(0.24f, 0.2f)));
        }
        var cab = new CabFrame
        {
            Front = Z(WsFoot), Floor = Floor, Ceiling = Roof - 0.1f, WsBase = Belt, WsTop = wsTop, DashTop = Belt, InnerHalf = inner,
            Nose = 0.45f, DriverX = 0.45f, HipRise = 0.36f, Recline = 0.3f, ColumnTilt = 0.42f, WheelRadius = 0.19f, DashToX = -inner,
            Clutch = false, PassengerSeat = true,
        };
        var cockpit = HeavyCabin.Build(m, cab, HeavyCabin.GaugesFor(spec.LimiterKmh, spec.Redline),
            HeavyDriveline.AirLow, HeavyDriveline.AirMax, mirrors);
        var hip = cockpit.Seat.Hip;
        // the rear bench of the SuperCrew: three across, behind the front seats
        var bench = new[] { hip with { Z = hip.Z - 0.86f }, new Vector3(0, hip.Y, hip.Z - 0.86f), hip with { X = -hip.X, Z = hip.Z - 0.86f } };
        foreach (var b in bench) CarMeshBuilder.Bucket(m, b, cockpit.Seat.Recline, Floor, HeavyCabin.DriverSeatColour);
        var seats = new List<SeatAnchor>
        {
            new(0, CarMeshBuilder.Turned(hip), cockpit.Seat.Recline, Floor),
            new(0, CarMeshBuilder.Turned(hip with { X = -hip.X }), cockpit.Seat.Recline, Floor),
        };
        seats.AddRange(bench.Select(b => new SeatAnchor(0, CarMeshBuilder.Turned(b), cockpit.Seat.Recline, Floor)));

        // head lamps either side of the grille, the three amber marker lamps across its top
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(head, sx * 0.74f, 1.08f, front - 0.08f, 0.38f, 0.1f, 0.04f, HeadLamp);
            Lamp(m, sx * (bodyW * 0.5f + 0.01f), 1.08f, front - 0.25f, 0.04f, 0.08f, 0.12f, Amber);
        }
        foreach (float x in new[] { -0.14f, 0f, 0.14f })
            Lamp(m, x, 1.2f, front - 0.09f, 0.06f, 0.035f, 0.03f, Amber);

        // ---- the bed: its floor over the wheels, the outer walls, the wheel tubs, the tailgate ----
        Skirt(m, s, cg, CabBack, Tailgate, Sill, BedFloor, bodyW, look.Paint);
        Along(m, cg, CabBack, Tailgate, BedFloor - 0.04f, BedFloor, bodyW - 2f * wall, look.Cargo);
        foreach (float sx in new[] { -1f, 1f })
        {
            Along(m, cg, CabBack, Tailgate, BedFloor - 0.05f, BedRail, wall, look.Paint, sx * (bodyW * 0.5f - wall * 0.5f));
            Along(m, cg, CabBack, Tailgate, BedRail, BedRail + 0.03f, 0.12f, look.Lower, sx * (bodyW * 0.5f - 0.06f));
            var a = s.Axles[^1];
            Along(m, cg, a.At - r - 0.05f, a.At + r + 0.05f, BedFloor, BedFloor + 0.24f, 0.26f, look.Cargo, sx * (bodyW * 0.5f - wall - 0.13f));
        }
        Along(m, cg, Tailgate - 0.05f, Tailgate + 0.03f, 0.62f, BedRail, bodyW, look.Paint);
        // the rear bumper, the receiver under it and the ball on its drop shank
        Along(m, cg, s.Length - 0.12f, s.Length, 0.42f, 0.66f, bodyW - 0.05f, look.Lower);
        Along(m, cg, s.Length - 0.45f, s.Length + 0.02f, 0.46f, 0.54f, 0.08f, Trim);
        Along(m, cg, s.Length, s.HitchAt + 0.05f, s.HitchHeight - 0.05f, s.HitchHeight, 0.07f, Steel);
        m.Tube(new Vector3(0, s.HitchHeight, Z(s.HitchAt)), new Vector3(0, s.HitchHeight + 0.04f, Z(s.HitchAt)), 0.016f, Steel, 6);
        m.Tube(new Vector3(0, s.HitchHeight + 0.03f, Z(s.HitchAt)), new Vector3(0, s.HitchHeight + 0.08f, Z(s.HitchAt)), 0.026f, Rim, 8);
        // the tail lamps up the bed's corners, the reversing lamps under them
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(tail, sx * (bodyW * 0.5f - 0.05f), 1.12f, Z(Tailgate + 0.04f), 0.1f, 0.3f, 0.04f, TailLamp);
            Lamp(rev, sx * (bodyW * 0.5f - 0.05f), 0.9f, Z(Tailgate + 0.04f), 0.1f, 0.12f, 0.04f, White);
        }

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), leaves.ToArray())
        {
            Cockpit = cockpit,
            Seats = seats.ToArray(),
            CarDoors = true,
        };
    }
}
