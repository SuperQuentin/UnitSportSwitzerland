using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A cinema's screens and beams (#1238): on every <see cref="FurnitureType.MovieScreen"/> a picture quad
/// (cat photos, a slide every few seconds: <see cref="Slide"/>), and from its hall's porthole a projector beam to it, both built
/// hidden with the interior and shown by <see cref="CinemaBooths"/> with the house lights, on change only.
/// The picture sits 2 cm in front of the screen's white, which nothing else does there (no z-fight).
///
/// <para>
/// Two ways to show it (#1250), by <see cref="RealProjection"/>. At High detail on a desktop, the hall the
/// camera is in has its picture thrown by a light, as the POC did (<see cref="CinemaProjectionPoc"/>): a
/// shadowed spot light just out of the porthole carries the slide as its projector texture, the screen is a
/// lit white quad (<c>shaders/projection_screen.gdshader</c>), and a player walking in the beam wears the
/// picture and leaves a shadow on it; its beam samples the picture along the haze (projector_beam). About
/// 0.23 ms of GPU for the one hall (<c>--projectionperf</c>); the other halls, behind walls, keep the pasted
/// picture. Below High, in VR and on phones: the pasted picture in every hall and a plain beam of the slide's
/// mean colour (<see cref="PlainBeam"/>).
/// </para>
/// </summary>
public partial class InteriorNode
{
    /// <summary>
    /// One hall's screen: its pasted picture, its lit screen and projector light, its beam, the hall's box (node-local)
    /// and its number (<see cref="CinemaBooths.HallAt"/>), whose panel switches it (#1279).
    /// </summary>
    private sealed record CinemaScreen(MeshInstance3D Picture, MeshInstance3D? Lit, SpotLight3D? Projector,
        MeshInstance3D? Beam, ShaderMaterial? Marched, Aabb Hall, int No, MeshInstance3D? Port = null, MeshInstance3D? Glow = null);

    private readonly List<CinemaScreen> _screens = new();
    /// <summary>The halls whose screens are on, a bit each.</summary>
    private int _screensOn;
    /// <summary>Each showing hall's music (#1279), by its number: made when its screen comes on, freed when it goes off.</summary>
    private readonly Dictionary<int, (Audio.Live.VenueMusicSource Source, int Reel)> _hallMusic = new();
    /// <summary>The house lights shown (-1: not yet) and where they are going (#1279).</summary>
    private float _house = -1f, _houseTarget = 1f;

    /// <summary>How long a cinema's house lights take from full to off, s [S: dimmers fade over 5-10 s].</summary>
    private const float HouseFadeSeconds = 6f;

    /// <summary>The house lights a step nearer their level, written only while they move. Per frame from <see cref="CinemaBooths"/>.</summary>
    public void FadeHouse(double delta)
    {
        if (_house < 0 || _house == _houseTarget) return;
        _house = Mathf.MoveToward(_house, _houseTarget, (float)delta / HouseFadeSeconds);
        _lit?.SetShaderParameter(GHouse, _house);
    }

    /// <summary>The halls' codes as last shown (<see cref="CinemaBooths"/>'s, read only), for their showing numbers.</summary>
    private int[]? _codes;
    private bool _realShow;
    private int _projecting = -1;
    private const string GHouse = "house_lights";

    /// <summary>The projector light's reach out of the porthole, m: clear of the wall, which would shadow it all.</summary>
    private const float LensOut = 0.5f;

    /// <summary>How far behind the lens's glass the beam's lines meet, m (#1279): a lens's focus is inside it, so the
    /// beam leaves the glass already a few centimetres wide, not from a point in front of it.</summary>
    private const float ApexBehindLens = 0.2f;

    /// <summary>The bodies that shadow a beam (#1279): every player's, registered while in the tree, read on the main thread.</summary>
    public static readonly HashSet<Node3D> BeamBodies = new();

    /// <summary>Each seating bank's cushions (#1279): the piece, its mesh, the seats let down now (and a spare set to fill).</summary>
    private readonly List<(FurniturePlan Bank, MeshInstance3D Mesh, HashSet<int> Down, HashSet<int> Next)> _cushions = new();

    /// <summary>A body's height over its feet, m, for the beam's capsules.</summary>
    private const float BodyHeight = 1.8f;

    private static readonly StringName[] GBody = { "body0", "body1", "body2", "body3", "body4", "body5", "body6", "body7" };
    /// <summary>What each beam capsule was last set to, so a parameter is written only when it moves.</summary>
    private readonly Vector4[] _bodies = new Vector4[8];

    /// <summary>
    /// Whether this client throws the picture with a light (High detail, a desktop, no headset) or pastes it
    /// with a plain beam. <c>--cinemaprojection real|plain</c> forces one, for shots and checks.
    /// </summary>
    public static bool RealProjection => ProjectionOverride switch
    {
        "real" => true,
        "plain" => false,
        _ => Core.GameSettings.Current.Detail == Core.DetailPreset.High && !XR.XrSession.Active && !Core.Platform.IsMobile,
    };

    private static readonly string? ProjectionOverride = Core.CmdArgs.Value("--cinemaprojection");

    private static void AddScreens(InteriorNode node)
    {
        var layout = node.Layout;
        if (layout.Type != BuildingType.Cinema) return;
        AddViewGlass(node);
        AddClearGlass(node);
        AddCushions(node);
        var ports = layout.Furniture.Where(f => f.Type == FurnitureType.Porthole).ToList();
        var projectors = layout.Furniture.Where(f => f.Type == FurnitureType.Projector).ToList();
        foreach (var p in layout.Furniture.Where(f => f.Type == FurnitureType.MovieScreen))
        {
            var basis = new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2);
            var origin = new Vector3(p.X, layout.FloorY(p.Floor) + p.Lift, p.Z);
            var (c, half) = InteriorMeshBuilder.ScreenPicture(p);
            var centre = origin + basis * (c + new Vector3(0, 0, 0.02f));
            MeshInstance3D Quad(string name, Material m)
            {
                var q = new MeshInstance3D
                {
                    Name = name,
                    Mesh = new QuadMesh { Size = half * 2 },
                    MaterialOverride = m,
                    Transform = new Transform3D(basis, centre),
                    Visible = false,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                node.AddChild(q);
                return q;
            }
            var picture = Quad("CatScreen", CatMaterial());
            var hall = HallOf(layout, p);
            int no = CinemaBooths.HallAt(layout, p.X, p.Z);

            // the beam from this hall's porthole: the nearest one to the screen
            var port = ports.MinBy(q => (q.X - p.X) * (q.X - p.X) + (q.Z - p.Z) * (q.Z - p.Z));
            if (port == null) { node._screens.Add(new CinemaScreen(picture, null, null, null, null, hall, no)); continue; }
            // the beam's apex at the projector's lens in the booth, behind the hole in the wall; the port's middle
            // if the booth had no room for that projector
            var from = new Vector3(port.X, layout.FloorY(port.Floor) + port.Lift + port.H / 2, port.Z);
            // the wall's line through the port (the hall's porthole stands 0.1 m in front of it)
            var portBasis = new Basis(Vector3.Up, port.Turns * Mathf.Pi / 2);
            var wallAt = from + portBasis * new Vector3(0, 0, -0.1f);
            MeshInstance3D? lensGlow = null;
            var proj = projectors.MinBy(q => (q.X - port.X) * (q.X - port.X) + (q.Z - port.Z) * (q.Z - port.Z));
            if (proj != null && (proj.X - port.X) * (proj.X - port.X) + (proj.Z - port.Z) * (proj.Z - port.Z) < 1.5f * 1.5f)
            {
                from = new Vector3(proj.X, layout.FloorY(proj.Floor) + proj.Lift + proj.H - InteriorGenerator.ProjectorLensDrop, proj.Z)
                    + new Basis(Vector3.Up, proj.Turns * Mathf.Pi / 2) * new Vector3(0, 0, proj.D / 2);
                // the lens's glow, in the picture's mean colour brightened (#1279): a bloom on its glass, off with the screen
                var toward = (centre - from).Normalized();
                lensGlow = new MeshInstance3D
                {
                    Name = "LensGlow",
                    Mesh = new QuadMesh { Size = new Vector2(0.18f, 0.18f) },
                    MaterialOverride = LensGlowMaterial(),
                    Position = from + toward * 0.02f,
                    Visible = false,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                node.AddChild(lensGlow);
                from -= toward * ApexBehindLens;
            }
            var right = basis * new Vector3(half.X, 0, 0);
            var up = new Vector3(0, half.Y, 0);
            // the picture going through the port, where its glass would be (#1279): faint, the cone's own cut through the
            // wall's line (the screen's picture scaled by apex-to-wall over apex-to-screen), on the beam's axis, seen
            // from the hall's side so mirrored (PortMaterial); the wall hides all of it but what the hole shows
            var wallNormal = portBasis * Vector3.Back;
            float k = Math.Clamp(wallNormal.Dot(wallAt - from) / Math.Max(1e-3f, wallNormal.Dot(centre - from)), 0.01f, 1f);
            var portPicture = new MeshInstance3D
            {
                Name = "PortPicture",
                Mesh = new QuadMesh { Size = new Vector2(2 * half.X * k, 2 * half.Y * k) },
                MaterialOverride = PortMaterial(),
                Transform = new Transform3D(basis, from + (centre - from) * k),
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            node.AddChild(portPicture);
            var marched = BeamFor(from, centre, right, up);
            var beam = new MeshInstance3D
            {
                Name = "ProjectorBeam",
                Mesh = Beam(from, centre, right, up),
                MaterialOverride = marched,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            node.AddChild(beam);

            // the real projection's screen and light, built hidden like the rest: lit only in the camera's hall
            var lit = Quad("LitScreen", ScreenMaterial());
            var lens = from + (centre - from).Normalized() * LensOut;
            var spot = new SpotLight3D
            {
                Name = "Projector",
                LightProjector = Letterboxed(_slide),
                // about the pasted picture's brightness on the white screen
                LightEnergy = 3.5f,
                // far past the screen: a spot fades out over the last part of its range, by 70 % at the screen with +4 m
                SpotRange = lens.DistanceTo(centre) * 3f,
                SpotAttenuation = 0f,
                // the square texture spans the cone's diameter: half the picture's width at the throw
                SpotAngle = Mathf.RadToDeg(Mathf.Atan(half.X / lens.DistanceTo(centre))),
                ShadowEnabled = true,
                ShadowBias = 0.03f,
                Visible = false,
            };
            node.AddChild(spot);
            spot.LookAtFromPosition(lens, centre, Vector3.Up);
            Projectors.RemoveAll(w => !w.TryGetTarget(out var s) || !IsInstanceValid(s));
            Projectors.Add(new WeakReference<SpotLight3D>(spot));
            node._screens.Add(new CinemaScreen(picture, lit, spot, beam, marched, hall, no, portPicture, lensGlow));
        }
    }

    private static StandardMaterial3D? _viewGlass;

    /// <summary>
    /// The booth's viewing windows' glass (#1250): every glazed window between two rooms, a dark see-through pane
    /// on the wall's shared line, the projectionist's view of the hall and a glimpse of the lit booth from it.
    /// The interior's own glass is opaque (it shows the sky), so these are a node of their own; built once,
    /// from the room on the Back or Right of the pair, so the two rooms' openings give one pane.
    /// </summary>
    private static void AddViewGlass(InteriorNode node)
    {
        var layout = node.Layout;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int panes = 0;
        for (int f = 0; f < layout.Floors.Count; f++)
            foreach (var r in layout.Floors[f].Rooms)
                foreach (var o in r.Openings)
                {
                    if (o.Kind != OpeningKind.Window || o.Other < 0 || o.Open || o.Side is not (Side.Back or Side.Right)) continue;
                    float y0 = layout.FloorY(f) + o.Bottom, y1 = layout.FloorY(f) + o.Top;
                    float a = o.Center - o.Width / 2, b = o.Center + o.Width / 2;
                    Vector3 P(float along, float y) => o.Side == Side.Back ? new Vector3(along, y, r.Z1) : new Vector3(r.X1, y, along);
                    foreach (var v in new[] { P(a, y0), P(b, y0), P(b, y1), P(a, y0), P(b, y1), P(a, y1) }) st.AddVertex(v);
                    panes++;
                }
        if (panes == 0) return;
        _viewGlass ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.02f, 0.025f, 0.03f, 0.6f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        node.AddChild(new MeshInstance3D
        {
            Name = "ViewGlass",
            Mesh = st.Commit(),
            MaterialOverride = _viewGlass,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>Every seating bank's cushions, all folded (nobody sits yet).</summary>
    private static void AddCushions(InteriorNode node)
    {
        var l = node.Layout;
        foreach (var bank in l.Furniture)
        {
            if (bank.Type != FurnitureType.SeatingBank) continue;
            var down = new HashSet<int>();
            var mi = new MeshInstance3D { Name = "Cushions", Mesh = CushionMesh(node, bank, down) };
            node.AddChild(mi);
            node._cushions.Add((bank, mi, down, new HashSet<int>()));
        }
    }

    private static ArrayMesh CushionMesh(InteriorNode node, FurniturePlan bank, HashSet<int> down) =>
        BuildMesh(InteriorMeshBuilder.Cushions(bank, node.Layout.FloorY(bank.Floor) + bank.Lift, down), node._lit!);

    /// <summary>
    /// The seats let down under whoever sits in this cinema (#1279), anyone's body in the sit pose over a seat; a bank's
    /// cushions are built again only when its set changes. Per frame from <see cref="CinemaBooths"/>, without allocating.
    /// </summary>
    public void UpdateSeats()
    {
        for (int b = 0; b < _cushions.Count; b++)
        {
            var (bank, mesh, down, next) = _cushions[b];
            next.Clear();
            var basis = new Basis(Vector3.Up, bank.Turns * Mathf.Pi / 2);
            var origin = new Vector3(bank.X, Layout.FloorY(bank.Floor) + bank.Lift, bank.Z);
            var rows = InteriorGenerator.RowsOf(bank);
            foreach (var body in BeamBodies)
            {
                if (body is not Player.FootPlayer { PoseKind: Player.FootPlayer.PoseSit } p || !IsInstanceValid(p) || !p.IsInsideTree()) continue;
                var local = basis.Inverse() * (ToLocal(p.GlobalPosition) - origin);
                if (Math.Abs(local.Z) > bank.D / 2 || Math.Abs(local.X) > bank.W / 2) continue;
                int id = CinemaSeats.SeatId(rows, local);
                if (id >= 0) next.Add(id);
            }
            if (next.SetEquals(down)) continue;
            _cushions[b] = (bank, mesh, next, down);
            mesh.Mesh = CushionMesh(this, bank, next);
        }
    }

    private static StandardMaterial3D? _clearGlass;

    /// <summary>The box office's glass, in the counter's frame (x across, z out of its back): see-through, as the mesh's never is.</summary>
    internal static (float X0, float X1, float Y0, float Y1, float Z) BoxOfficeGlass(FurniturePlan p) =>
        (-p.W / 2 + 0.055f, p.W / 2 - 0.055f, 1.1f, 1.95f, p.D / 2 - 0.41f);

    /// <summary>
    /// Clear panes over counters (#1279): the box office's glass, which the interior's one opaque mesh drew as a
    /// pale wall in front of the cashier. A faint tint and nothing else, so the cashier shows through it.
    /// </summary>
    private static void AddClearGlass(InteriorNode node)
    {
        var layout = node.Layout;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int panes = 0;
        foreach (var p in layout.Furniture.Where(f => f.Type == FurnitureType.BoxOffice))
        {
            var basis = new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2);
            var at = new Vector3(p.X, layout.FloorY(p.Floor) + p.Lift, p.Z);
            var (x0, x1, y0, y1, z) = BoxOfficeGlass(p);
            Vector3 P(float x, float y) => at + basis * new Vector3(x, y, z);
            foreach (var v in new[] { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y0), P(x1, y1), P(x0, y1) }) st.AddVertex(v);
            panes++;
        }
        if (panes == 0) return;
        _clearGlass ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.75f, 0.86f, 0.92f, 0.14f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        node.AddChild(new MeshInstance3D
        {
            Name = "ClearGlass",
            Mesh = st.Commit(),
            MaterialOverride = _clearGlass,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>The auditorium a screen stands in, as a box in the node's frame (the screen's own spot if none holds it).</summary>
    private static Aabb HallOf(InteriorLayout layout, FurniturePlan p)
    {
        float y = layout.FloorY(p.Floor);
        if (p.Floor >= 0 && p.Floor < layout.Floors.Count)
            foreach (var r in layout.Floors[p.Floor].Rooms)
                if (p.X > r.X0 - 0.5f && p.X < r.X1 + 0.5f && p.Z > r.Z0 - 0.5f && p.Z < r.Z1 + 0.5f && r.Type == RoomType.Auditorium)
                    return new Aabb(new Vector3(r.X0, y, r.Z0), new Vector3(r.X1 - r.X0, layout.ClearOf(r), r.Z1 - r.Z0));
        return new Aabb(new Vector3(p.X - 1, y, p.Z - 1), new Vector3(2, 3, 2));
    }

    /// <summary>
    /// The booth's state on this cinema: the house lights' level and the screen's wash (one each for the whole
    /// interior: the hall the camera is in), the halls whose cat slides and beams are on (a bit each), and the way they are shown.
    /// </summary>
    public void ShowCinema(float houseLights, int screens, int[]? codes, bool glow, bool real, bool snap = true)
    {
        // the house lights fade to their new level (FadeHouse), as a cinema's dim over seconds; another hall's level,
        // or the first showing, is at once
        _houseTarget = houseLights;
        if (_house < 0 || snap)
        {
            _house = houseLights;
            _lit?.SetShaderParameter(GHouse, houseLights);
        }
        ShowGlow(glow);
        _screensOn = screens;
        _codes = codes;
        _realShow = real;
        RefreshScreens();
    }

    /// <summary>The hall a node-local point is in, or the nearest one (-1 in a cinema with none): whose house lights to show.</summary>
    public int HallNear(Vector3 at)
    {
        int best = -1;
        float bestD = float.MaxValue;
        foreach (var s in _screens)
        {
            if (s.Hall.HasPoint(at)) return s.No;
            float d = (s.Hall.GetCenter() - at).LengthSquared();
            if (d < bestD) (best, bestD) = (s.No, d);
        }
        return best;
    }

    private bool On(CinemaScreen s) => s.No >= 0 && (_screensOn >> s.No & 1) != 0;

    /// <summary>
    /// Real projection: the hall whose projector throws the picture, the one the camera stands in
    /// (<paramref name="camera"/> in the node's frame); per frame from <see cref="CinemaBooths"/>, on change only.
    /// </summary>
    public void ProjectFor(Vector3 camera)
    {
        int i = _realShow ? _screens.FindIndex(s => s.Projector != null && On(s) && s.Hall.HasPoint(camera)) : -1;
        if (i != _projecting)
        {
            _projecting = i;
            RefreshScreens();
        }
        if (i >= 0 && _screens[i].Marched is { } beam) ShadowBeam(beam, _screens[i].Hall);
    }

    /// <summary>
    /// The people standing in the hall being projected, as the beam's capsules (#1279): the beam shows the shadow
    /// a body throws through it, as the screen does. Written only when a capsule moves.
    /// </summary>
    private void ShadowBeam(ShaderMaterial beam, Aabb hall)
    {
        int k = 0;
        var room = hall.Grow(0.5f);
        foreach (var body in BeamBodies)
        {
            if (k >= GBody.Length) break;
            if (!IsInstanceValid(body) || !body.IsInsideTree()) continue;
            var feet = ToLocal(body.GlobalPosition);
            if (!room.HasPoint(feet + Vector3.Up * 0.5f)) continue;
            Body(beam, k++, new Vector4(feet.X, feet.Z, feet.Y, feet.Y + BodyHeight));
        }
        for (; k < GBody.Length; k++) Body(beam, k, new Vector4(0, 0, 0, -1));
    }

    /// <summary>The projected beam's capsules again, for a body that moves without a booth driving the show (the model viewer's walker).</summary>
    public void ShadowProjected()
    {
        if (_projecting >= 0 && _screens[_projecting].Marched is { } beam) ShadowBeam(beam, _screens[_projecting].Hall);
    }

    private void Body(ShaderMaterial beam, int k, Vector4 v)
    {
        if (_bodies[k].IsEqualApprox(v)) return;
        _bodies[k] = v;
        beam.SetShaderParameter(GBody[k], v);
    }

    private void RefreshScreens()
    {
        for (int i = 0; i < _screens.Count; i++)
        {
            var s = _screens[i];
            // a 3D film keeps its pasted picture: a light carries one picture, not two eyes (#1298 phase 6)
            bool on = On(s), thrown = on && _realShow && i == _projecting && !(_videoIs3d && s.No == _videoHall);
            s.Picture.Visible = on && !thrown;
            if (s.Lit != null) s.Lit.Visible = thrown;
            if (s.Projector != null) s.Projector.Visible = thrown;
            HallMusic(s, on && (_videoMuted >> Math.Max(0, s.No) & 1) == 0);
            if (s.Port != null) s.Port.Visible = on;
            if (s.Glow != null) s.Glow.Visible = on;
            if (s.Beam == null) continue;
            s.Beam.Visible = on;
            s.Beam.MaterialOverride = _realShow ? s.Marched : s.No == _videoHall && _videoPlain != null ? _videoPlain : PlainBeam();
        }
    }

    /// <summary>
    /// A hall's film music while its screen is on (#1279), drawn per hall from the hall's own seed so a multiplex's
    /// halls do not all play one thing: Radio Swiss Classic on the relay (its modules when the relay is silent) as
    /// one draw among the film playlist's modules (<see cref="Audio.Tracker.VenueMood.Film"/>), which play at once,
    /// each hall its own bar of them, the same on every client (the playlist runs on the shared clock). Under the
    /// hall's ceiling, in its middle.
    /// </summary>
    private void HallMusic(CinemaScreen s, bool on)
    {
        if (s.No < 0) return;
        // a new showing (the screen switched off and on again, #1279) draws again
        int reel = _codes != null && s.No < _codes.Length ? CinemaBooths.ReelOf(_codes[s.No]) : 0;
        bool playing = _hallMusic.TryGetValue(s.No, out var now);
        if (playing && (!on || now.Reel != reel))
        {
            _hallMusic.Remove(s.No);
            if (IsInstanceValid(now.Source)) now.Source.QueueFree();
            playing = false;
        }
        if (!on || playing) return;
        var c = s.Hall.GetCenter();
        uint seed = (uint)Core.Fnv.Hash($"{Layout.Key}#{s.No}#{reel}");
        int modules = Audio.Tracker.TrackerLibrary.Playlists[Audio.Tracker.VenueMood.Film].Length;
        var music = new Audio.Live.VenueMusicSource
        {
            Name = $"FilmMusic{s.No}",
            Station = seed % (uint)(modules + 1) == 0 ? VenueMusic.FilmStation : 0,
            Volume = VenueMusic.RoomVolume,
            Mood = Audio.Tracker.VenueMood.Film,
            // the halls spread evenly round the playlist, so the next hall is a song or two on
            // the halls spread evenly round the playlist, so the next hall is a song or two on; each showing somewhere in its hall's share
            Salt = (uint)((s.No + seed % 1000 / 1000.0) * Audio.Tracker.TrackerLibrary.Cycle(Audio.Tracker.VenueMood.Film) / Math.Max(1, _screens.Count)),
            Position = new Vector3(c.X, s.Hall.Position.Y + Math.Min(3f, s.Hall.Size.Y - 0.5f), c.Z),
            // the whole hall at full level, as its surround speakers make it
            Spread = new Vector2(s.Hall.Size.X, s.Hall.Size.Z).Length() / 2,
        };
        AddChild(music);
        _hallMusic[s.No] = (music, reel);
    }

    /// <summary>A light pyramid from a point to a rectangle: four faces, bright at the lens, faint at the screen.</summary>
    private static ArrayMesh Beam(Vector3 from, Vector3 centre, Vector3 right, Vector3 up)
    {
        var corners = new[] { centre - right - up, centre + right - up, centre + right + up, centre - right + up };
        var v = new List<Vector3>();
        var col = new List<Color>();
        // bright at the lens, nothing left at the screen (#1279)
        var at = new Color(1f, 0.96f, 0.86f, 0.06f);
        var screen = new Color(1f, 0.96f, 0.86f, 0f);
        for (int i = 0; i < 4; i++)
        {
            v.Add(from); col.Add(at);
            v.Add(corners[i]); col.Add(screen);
            v.Add(corners[(i + 1) % 4]); col.Add(screen);
        }
        // the cone closed at the screen, 3 cm short of the picture (#1250): looking at the screen from inside
        // the beam, its haze is drawn from there back to the eye, over the picture
        var near = corners.Select(c => c + (from - c).Normalized() * 0.03f).ToArray();
        foreach (var (a, b, c) in new[] { (0, 1, 2), (0, 2, 3) })
        {
            v.Add(near[a]); col.Add(screen);
            v.Add(near[b]); col.Add(screen);
            v.Add(near[c]); col.Add(screen);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = col.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static StandardMaterial3D? _catMaterial;
    private static ShaderMaterial? _beamMaterial;

    private static StandardMaterial3D CatMaterial() => _catMaterial ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoTexture = Slide(0),
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
    };

    /// <summary>Every beam's material alive, to give each the new picture: one per beam, its cone in its uniforms.</summary>
    private static readonly List<WeakReference<ShaderMaterial>> Beams = new();

    /// <summary>A beam's material: the cone in the shader's terms (projector_beam), the lens, the picture's centre and half extents.</summary>
    private static ShaderMaterial BeamFor(Vector3 apex, Vector3 centre, Vector3 right, Vector3 up)
    {
        var m = (ShaderMaterial)BeamMaterial().Duplicate();
        m.SetShaderParameter("apex", apex);
        m.SetShaderParameter("centre", centre);
        m.SetShaderParameter("half_right", right);
        m.SetShaderParameter("half_up", up);
        m.SetShaderParameter(GPicture, Slide(_slide));
        Beams.RemoveAll(w => !w.TryGetTarget(out _));
        Beams.Add(new WeakReference<ShaderMaterial>(m));
        return m;
    }

    private static ShaderMaterial BeamMaterial() => _beamMaterial ??= new ShaderMaterial
    {
        Shader = GD.Load<Shader>("res://shaders/projector_beam.gdshader"),
    };

    private static StandardMaterial3D? _plainBeam;
    private static ShaderMaterial? _screenMaterial;

    /// <summary>
    /// Below High detail: the beam without its march through the picture, the cone's vertex fade in the
    /// slide's mean colour (<see cref="SlideTint"/>), added onto what is behind. One for every beam.
    /// </summary>
    private static StandardMaterial3D PlainBeam() => _plainBeam ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        AlbedoColor = Full(SlideTint(_slide)),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        DisableReceiveShadows = true,
    };

    private static StandardMaterial3D? _portMaterial, _lensGlow;

    /// <summary>The picture in the port (#1279): see-through, unlit, both faces (seen from the hall and the booth), mirrored.</summary>
    private static StandardMaterial3D PortMaterial() => _portMaterial ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoTexture = Slide(_slide),
        AlbedoColor = new Color(1, 1, 1, 0.45f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        // no flip of its own: it faces the screen's way, so from the seats (looking back at the port) it is seen from
        // behind, the screen's picture mirrored, as light going through glass toward you is
    };

    /// <summary>The lens's bloom (#1279): a soft round glow facing the camera, added on, in the picture's mean colour.</summary>
    private static StandardMaterial3D LensGlowMaterial() => _lensGlow ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoTexture = new GradientTexture2D
        {
            Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
            Gradient = new Gradient { Colors = new[] { Colors.White, new Color(1, 1, 1, 0.35f), new Color(1, 1, 1, 0) }, Offsets = new[] { 0f, 0.25f, 1f } },
            Width = 64, Height = 64,
        },
        AlbedoColor = Full(SlideTint(_slide)) * 1.6f,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        NoDepthTest = false,
    };

    /// <summary>A colour brought to full brightness, its hue kept: the plain beam is as bright for a dark cat as a light one.</summary>
    private static Color Full(Color c)
    {
        float m = Math.Max(0.05f, Math.Max(c.R, Math.Max(c.G, c.B)));
        return new Color(c.R / m, c.G / m, c.B / m);
    }

    /// <summary>The white screen a projector light shows the picture on: lit by it alone (projection_screen).</summary>
    private static ShaderMaterial ScreenMaterial()
    {
        if (_screenMaterial != null) return _screenMaterial;
        _screenMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/projection_screen.gdshader") };
        _screenMaterial.SetShaderParameter("albedo", new Color(0.92f, 0.92f, 0.90f));
        return _screenMaterial;
    }

    /// <summary>Every projector light alive, to give each the new picture.</summary>
    private static readonly List<WeakReference<SpotLight3D>> Projectors = new();

    private static ImageTexture?[]? _letterboxed;

    /// <summary>
    /// A slide in a square of black, as a projector light's texture: the spot's projection is square, so the
    /// 2.39:1 picture keeps its shape. Made once per slide.
    /// </summary>
    public static ImageTexture Letterboxed(int i)
    {
        _letterboxed ??= new ImageTexture?[SlideCount];
        i = ((i % SlideCount) + SlideCount) % SlideCount;
        if (_letterboxed[i] is { } done) return done;
        var img = Slide(i).GetImage();
        if (img.IsCompressed()) img.Decompress();
        img.Convert(Image.Format.Rgb8);
        int w = img.GetWidth();
        var square = Image.CreateEmpty(w, w, false, Image.Format.Rgb8);
        square.Fill(Colors.Black);
        square.BlitRect(img, new Rect2I(0, 0, w, img.GetHeight()), new Vector2I(0, (w - img.GetHeight()) / 2));
        square.GenerateMipmaps();
        return _letterboxed[i] = ImageTexture.CreateFromImage(square);
    }

    /// <summary>
    /// The pictures on the screen until films come (a follow-up streams a video or a shared screen): a
    /// slideshow of six cat photos, CC0 from Wikimedia Commons (<c>assets/cinema/</c>, sources in
    /// <c>assets/ASSETS.md</c>), cropped to the screen's 2.39:1. One every <see cref="SlideSeconds"/> on
    /// the shared clock, so everyone in the hall sees the same cat (<see cref="SlideAt"/>).
    /// </summary>
    public const int SlideCount = 6;
    public const double SlideSeconds = 8;

    private static Texture2D[]? _slides;

    public static Texture2D Slide(int i)
    {
        _slides ??= Enumerable.Range(1, SlideCount)
            .Select(k => GD.Load<Texture2D>($"res://assets/cinema/cat_{k}.jpg") ?? MissingSlide(k)).ToArray();
        return _slides[((i % SlideCount) + SlideCount) % SlideCount];
    }

    /// <summary>
    /// A plain grey picture for a photo that did not load (a checkout without git-lfs has text pointers
    /// where the JPEGs are): said once, never a cinema that cannot be built.
    /// </summary>
    private static Texture2D MissingSlide(int k)
    {
        GD.PushWarning($"[cinema] assets/cinema/cat_{k}.jpg did not load (git-lfs not installed? `git lfs pull`): a grey slide instead");
        var img = Image.CreateEmpty(478, 200, false, Image.Format.Rgb8);
        img.Fill(new Color(0.35f, 0.35f, 0.37f));
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The slide showing at a shared-clock time, s.</summary>
    public static int SlideAt(double now) => (int)(Math.Floor(now / SlideSeconds) % SlideCount + SlideCount) % SlideCount;

    private static readonly StringName GGlow = "screen_glow", GPicture = "picture";

    /// <summary>How much of the picture's colour the hall takes back from its screen: a wash, under the house lights.</summary>
    private const float GlowGain = 0.45f;

    private static int _slide;
    private static Color[]? _tints;

    /// <summary>A slide's mean colour, from its picture shrunk to a pixel: what its beam and its glow are tinted.</summary>
    public static Color SlideTint(int i)
    {
        _tints ??= new Color[SlideCount];
        i = ((i % SlideCount) + SlideCount) % SlideCount;
        if (_tints[i].A > 0) return _tints[i];
        var img = Slide(i).GetImage();
        if (img == null) return _tints[i] = Colors.White;
        if (img.IsCompressed()) img.Decompress();
        img.Resize(1, 1, Image.Interpolation.Bilinear);
        var c = img.GetPixel(0, 0);
        return _tints[i] = new Color(c.R, c.G, c.B, 1f);
    }

    /// <summary>Every screen's picture (one material for all) and every beam's colour: to slide <paramref name="i"/>, on change only.</summary>
    public static void ShowSlide(int i)
    {
        _slide = i;
        CatMaterial().AlbedoTexture = Slide(i);
        // the beams carry the picture itself, blurred: what each part of the haze lights (projector_beam)
        foreach (var w in Beams)
            if (w.TryGetTarget(out var m) && !VideoOwned.Contains(m)) m.SetShaderParameter(GPicture, Slide(i));
        if (_plainBeam != null) _plainBeam.AlbedoColor = Full(SlideTint(i));
        if (_portMaterial != null) _portMaterial.AlbedoTexture = Slide(i);
        if (_lensGlow != null) _lensGlow.AlbedoColor = Full(SlideTint(i)) * 1.6f;
        foreach (var w in Projectors)
            if (w.TryGetTarget(out var s) && IsInstanceValid(s) && !VideoOwned.Contains(s)) s.LightProjector = Letterboxed(i);
    }

    /// <summary>This hall's wash of the screen's colour: the current slide's while the screen is on, none off.</summary>
    public void ShowGlow(bool screen)
    {
        // a video's hall takes its wash from its frames (ShowVideo)
        if (_videoHall >= 0) return;
        var t = SlideTint(_slide);
        _lit?.SetShaderParameter(GGlow, screen ? new Vector3(t.R, t.G, t.B) * GlowGain : Vector3.Zero);
    }
}
