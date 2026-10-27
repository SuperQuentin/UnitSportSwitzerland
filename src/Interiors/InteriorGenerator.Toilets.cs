using System.Runtime.CompilerServices;
using UnitSport.Terrain.Format;
using static UnitSport.Interiors.ToiletSizing;

namespace UnitSport.Interiors;

/// <summary>
/// Public toilets (#1285): one planner every public building's generator hands a strip of its floor
/// along a wall of the room people are in (the cinema's lobby, the bowling concourse, the arcade hall,
/// a restaurant's guest room, the IKEA market). It lays the set <see cref="ToiletSizing.Choose"/>
/// picks for the strip and the people:
///
/// <list type="bullet">
/// <item><b>Gendered</b>: along the host wall the women's washroom (in by its door, the basins along its
/// outer wall with a mirror over each and the hand dryer, a row of stalls along its far side wall), the
/// men's the same with the urinals and their screens along the host wall, then the accessible WC
/// (SIA 500) in a big building.</item>
/// <item><b>Shared</b>: one anteroom with the basin, a women's and a men's stall off its back wall.</item>
/// <item><b>Single</b>: one WC with its own door.</item>
/// </list>
///
/// <para>
/// A door into the room with the basins, then one into each stall: every way in is a leaf
/// (<see cref="InnerDoorPlan"/>, a flat door's: E / Y or a VR grip, server state) hanging in the room before it
/// and swinging into the toilet: the washrooms', the shared anteroom's, the stalls' (rooms walled to the
/// ceiling), the WC's and the accessible WC's. The pieces are planned with the rooms and stood when
/// the floor is furnished (<see cref="PlaceToiletPieces"/>). Numbers in <c>docs/notes/terrain/public-toilets.md</c>.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>The toilet pieces planned on a floor, stood by <see cref="Furnish(InteriorLayout, Random, int)"/>.</summary>
    private static readonly ConditionalWeakTable<FloorPlan, List<FurniturePlan>> ToiletPieceTable = new();

    /// <summary>A room only the toilet planner furnishes.</summary>
    internal static bool ToiletRoom(RoomType t) => t is RoomType.ToiletLobby or RoomType.WomensToilet or RoomType.MensToilet
        or RoomType.ToiletStall or RoomType.AccessibleToilet;

    /// <summary>A strip's walls: the one on the host, the one across from it, the one at <c>a</c> = 0, the one at its far end.</summary>
    private enum StripWall { Host, Back, Start, End }

    /// <summary>
    /// A strip's frame: <c>a</c> along its host wall from one end, <c>b</c> from the host wall into the strip.
    /// The four ways a strip can face the host are all turns of one frame, so every layout below is written once.
    /// </summary>
    private readonly struct ToiletFrame
    {
        private readonly float _ox, _oz, _tx, _tz, _nx, _nz;
        public readonly float Length, Depth;
        public readonly Side Host;

        public ToiletFrame(RectPlan strip, Side host, bool reverse)
        {
            Host = host;
            (_nx, _nz) = host switch { Side.Front => (0f, 1f), Side.Back => (0f, -1f), Side.Left => (1f, 0f), _ => (-1f, 0f) };
            (_tx, _tz) = (_nz, -_nx);
            if (reverse) (_tx, _tz) = (-_tx, -_tz);
            _ox = _nx > 0.5f ? strip.X0 : _nx < -0.5f ? strip.X1 : _tx > 0.5f ? strip.X0 : strip.X1;
            _oz = _nz > 0.5f ? strip.Z0 : _nz < -0.5f ? strip.Z1 : _tz > 0.5f ? strip.Z0 : strip.Z1;
            bool alongX = Math.Abs(_tx) > 0.5f;
            Length = alongX ? strip.X1 - strip.X0 : strip.Z1 - strip.Z0;
            Depth = alongX ? strip.Z1 - strip.Z0 : strip.X1 - strip.X0;
        }

        public (float X, float Z) Point(float a, float b) => (_ox + _tx * a + _nx * b, _oz + _tz * a + _nz * b);

        public RectPlan Rect(float a0, float b0, float a1, float b1)
        {
            var (x0, z0) = Point(a0, b0);
            var (x1, z1) = Point(a1, b1);
            return new RectPlan(Math.Min(x0, x1), Math.Min(z0, z1), Math.Max(x0, x1), Math.Max(z0, z1));
        }

        /// <summary>The plan side a wall of the frame faces, seen from inside the strip.</summary>
        public Side Wall(StripWall e) => e switch
        {
            StripWall.Host => Host,
            StripWall.Back => Opposite(Host),
            StripWall.Start => SideOf(-_tx, -_tz),
            _ => SideOf(_tx, _tz),
        };

        /// <summary>An opening's <see cref="OpeningPlan.Center"/> for a cut through wall <paramref name="e"/> at the frame's (a, b).</summary>
        public float Center(StripWall e, float a, float b)
        {
            var (x, z) = Point(a, b);
            return Wall(e) is Side.Front or Side.Back ? x : z;
        }

        private static Side SideOf(float x, float z) => z < -0.5f ? Side.Front : x > 0.5f ? Side.Right : z > 0.5f ? Side.Back : Side.Left;
    }

    /// <summary>
    /// The toilets for <paramref name="people"/> in <paramref name="strip"/>, a rectangle of
    /// <paramref name="floor"/> beside room <paramref name="host"/> across its wall <paramref name="hostSide"/>
    /// (the strip's wall facing the host). They start at the strip's end nearer <paramref name="startNear"/>.
    /// <paramref name="clear"/>: the toilet rooms' headroom when they are a low block in a tall hall (0: the storey's).
    /// Returns the set planned and what of the strip it left, from the far end: null when nothing is left, else at
    /// least <see cref="ToiletRest"/> long (a shorter sliver widens the set's last room).
    /// </summary>
    /// <param name="reuse">A room of the floor whose slot the set's first room takes (a store the toilets replace), −1 for none.</param>
    internal static (ToiletSizing.Plan Plan, RectPlan? Left) PublicToilets(InteriorLayout l, FloorPlan floor, int host, RectPlan strip,
        Side hostSide, (float X, float Z) startNear, int people, float clear = 0, int reuse = -1)
    {
        var fr = new ToiletFrame(strip, hostSide, false);
        var (sx, sz) = fr.Point(0, 0);
        var (ex, ez) = fr.Point(fr.Length, 0);
        if (Sq(ex - startNear.X, ez - startNear.Z) < Sq(sx - startNear.X, sz - startNear.Z)) fr = new ToiletFrame(strip, hostSide, true);
        var plan = Choose(fr.Length, fr.Depth, people);
        if (plan.Set == ToiletSet.None) return (plan, strip);
        float extra = fr.Length - plan.Length;
        if (extra < ToiletRest) plan = plan with { Length = fr.Length };
        else extra = 0;
        new ToiletPlanner(l, floor, host, fr, clear, extra, reuse).Lay(plan);
        return (plan, fr.Length - plan.Length >= 0.01f ? fr.Rect(plan.Length, 0, fr.Length, fr.Depth) : null);
    }

    /// <summary>What is left of a strip past its toilets is a room of its own from this length; less widens the set's last room.</summary>
    internal const float ToiletRest = 1.2f;

    private static float Sq(float x, float z) => x * x + z * z;

    /// <summary>One set being laid into a strip: its rooms, doorways, leaves and pieces.</summary>
    private sealed class ToiletPlanner
    {
        private readonly InteriorLayout _l;
        private readonly FloorPlan _floor;
        private readonly int _host, _f;
        private readonly ToiletFrame _fr;
        private readonly float _clear, _top, _extra;
        private int _reuse;
        private readonly List<FurniturePlan> _pieces;
        /// <summary>What stands in the set so far, and the doorways' clear strips, in the frame: a piece placed later keeps off them.</summary>
        private readonly List<(float A0, float B0, float A1, float B1)> _taken = new();

        /// <summary>A wall piece's distance off the wall's line: the wall's face and a centimetre.</summary>
        private const float Off = WallInset + 0.01f;

        /// <param name="extra">The sliver past the set its last room takes too: the accessible WC, the men's washroom, the shared stalls, the WC.</param>
        public ToiletPlanner(InteriorLayout l, FloorPlan floor, int host, ToiletFrame fr, float clear, float extra, int reuse)
        {
            (_l, _floor, _host, _fr, _clear, _extra, _reuse) = (l, floor, host, fr, clear, extra, reuse);
            _f = Math.Max(0, l.Floors.IndexOf(floor));
            float headroom = clear > 0 ? clear : l.StoreyHeight - Slab;
            _top = Math.Min(2.05f, headroom - 0.2f);
            _pieces = ToiletPieceTable.GetValue(floor, _ => new List<FurniturePlan>());
        }

        public void Lay(ToiletSizing.Plan p)
        {
            switch (p.Set)
            {
                case ToiletSet.Gendered:
                {
                    float a = Washroom(0, true, p.WomenStalls, 0, 0);
                    a = Washroom(a, false, p.MenStalls, p.Urinals, p.Accessible ? 0 : _extra);
                    if (p.Accessible) Accessible(a);
                    break;
                }
                case ToiletSet.Shared: Shared(0); break;
                case ToiletSet.Single: Single(0); break;
            }
        }

        private float D => _fr.Depth;

        private int Room(RoomType t, float a0, float b0, float a1, float b1)
        {
            var r = new RoomPlan { Type = t, Clear = _clear };
            SetRect(r, _fr.Rect(a0, b0, a1, b1));
            if (_reuse >= 0)
            {
                int at = _reuse;
                _reuse = -1;
                _floor.Rooms[at] = r;
                return at;
            }
            _floor.Rooms.Add(r);
            return _floor.Rooms.Count - 1;
        }

        /// <summary>
        /// A doorway through the wall between room <paramref name="from"/> and room <paramref name="to"/>, which is
        /// <paramref name="from"/>'s wall <paramref name="e"/>, at the frame's (a, b); with a leaf hanging in
        /// <paramref name="from"/> and swinging into <paramref name="to"/> when <paramref name="leaf"/>.
        /// </summary>
        private void Link(int from, StripWall e, int to, float a, float b, float width, bool leaf)
        {
            var side = _fr.Wall(e);
            float c = _fr.Center(e, a, b);
            _floor.Rooms[from].Openings.Add(new OpeningPlan { Side = side, Center = c, Width = width, Top = _top, Kind = OpeningKind.Door, Other = to });
            _floor.Rooms[to].Openings.Add(new OpeningPlan { Side = Opposite(side), Center = c, Width = width, Top = _top, Kind = OpeningKind.Door, Other = from });
            if (leaf) _l.InnerDoors.Add(new InnerDoorPlan { Floor = _f, Room = from, Side = side, Center = c, Width = width, Top = _top, Unit = -1 });
            // the validator's 0.6 m inside a doorway, on both sides
            float h = width / 2;
            _taken.Add(e is StripWall.Host or StripWall.Back ? (a - h, b - 0.6f, a + h, b + 0.6f) : (a - 0.6f, b - h, a + 0.6f, b + h));
        }

        /// <summary>A doorway from the host into the room <paramref name="into"/> at <paramref name="a"/>, with its sign over it on the host's side.</summary>
        private void HostDoor(int into, float a, float width, bool leaf, FurnitureType sign)
        {
            // the leaf hangs on the host's side of the wall and swings into the toilet
            var side = _fr.Wall(StripWall.Host);
            float c = _fr.Center(StripWall.Host, a, 0);
            _floor.Rooms[into].Openings.Add(new OpeningPlan { Side = side, Center = c, Width = width, Top = _top, Kind = OpeningKind.Door, Other = _host });
            _floor.Rooms[_host].Openings.Add(new OpeningPlan { Side = Opposite(side), Center = c, Width = width, Top = _top, Kind = OpeningKind.Door, Other = into });
            if (leaf) _l.InnerDoors.Add(new InnerDoorPlan { Floor = _f, Room = _host, Side = Opposite(side), Center = c, Width = width, Top = _top, Unit = -1 });
            _taken.Add((a - width / 2, 0, a + width / 2, 0.6f));
            Sign(sign, a, -1, 0);
        }

        /// <summary>A sign over a doorway at <paramref name="a"/>: on the host's face of the host wall (<paramref name="dir"/> -1), or on the face of a wall at <paramref name="b"/> looking toward the host (+1).</summary>
        private void Sign(FurnitureType type, float a, int dir, float b)
        {
            const float w = 0.3f, d = 0.03f, h = 0.3f;
            float lift = Math.Min(_top + 0.1f, (_clear > 0 ? _clear : _l.StoreyHeight - Slab) - h - 0.05f);
            float at = dir < 0 ? -(Off + d / 2) : b - (Off + d / 2);
            var (x, z) = _fr.Point(a, at);
            // it faces the host (or the anteroom): its back to the wall that looks toward the strip
            _pieces.Add(new FurniturePlan { Type = type, Floor = _f, X = x, Z = z, Turns = TurnsAgainst(Opposite(_fr.Wall(StripWall.Host))), W = w, D = d, H = h, Lift = lift });
        }

        /// <summary>
        /// A piece against wall <paramref name="e"/> of the frame's box <paramref name="a0"/>..<paramref name="b1"/>,
        /// centred at <paramref name="s"/> along it, <paramref name="gap"/> off it; left out when it would stand in
        /// a doorway or on another piece (false).
        /// </summary>
        private bool Fix(FurnitureType type, StripWall e, float a0, float b0, float a1, float b1, float s, float w, float d, float h,
            float lift = 0, float gap = 0, bool force = false)
        {
            float off = Off + gap;
            var (ca, cb, aw, bw) = e switch
            {
                StripWall.Host => (s, b0 + off + d / 2, w, d),
                StripWall.Back => (s, b1 - off - d / 2, w, d),
                StripWall.Start => (a0 + off + d / 2, s, d, w),
                _ => (a1 - off - d / 2, s, d, w),
            };
            var box = (A0: ca - aw / 2, B0: cb - bw / 2, A1: ca + aw / 2, B1: cb + bw / 2);
            if (box.A0 < a0 - 1e-3f || box.A1 > a1 + 1e-3f || box.B0 < b0 - 1e-3f || box.B1 > b1 + 1e-3f) return false;
            // overhead pieces (a sign, a mirror at 1.5 m) are not in anyone's way
            if (!force && lift < 1.5f && _taken.Any(t => t.A0 < box.A1 - 0.015f && box.A0 < t.A1 - 0.015f && t.B0 < box.B1 - 0.015f && box.B0 < t.B1 - 0.015f))
                return false;
            if (lift < 1.5f) _taken.Add(box);
            var (x, z) = _fr.Point(ca, cb);
            _pieces.Add(new FurniturePlan { Type = type, Floor = _f, X = x, Z = z, Turns = TurnsAgainst(_fr.Wall(e)), W = w, D = d, H = h, Lift = lift });
            return true;
        }

        /// <summary>
        /// A washroom from <paramref name="a"/>: the women's (basins, stalls) or the men's (basins, stalls and
        /// urinals). Returns where the next room starts.
        /// </summary>
        private float Washroom(float a, bool women, int stalls, int urinals, float extra)
        {
            float a1 = a + AnteW, wash1 = a1 + (women ? WomenWashW : MenWashW(urinals)) + extra, row0 = wash1 - StallD;
            // one room: in by its door from the host, the basins along its outer wall, the stalls along the far one
            int wash = Room(women ? RoomType.WomensToilet : RoomType.MensToilet, a, 0, row0, D);
            HostDoor(wash, a1 - 0.1f - ToiletSizing.Door / 2, ToiletSizing.Door, true, women ? FurnitureType.WcSignWomen : FurnitureType.WcSignMen);

            // the basins along the outer wall, a mirror over each, the dryer after them
            int fixtures = stalls + urinals;
            int basins = Basins(fixtures, D - 0.6f);
            float run = basins * BasinPitch, b0 = 0.1f;
            if (Fix(FurnitureType.SinkRow, StripWall.Start, a, 0, row0, D, b0 + run / 2, run, 0.45f, 0.85f, gap: 0.04f))
                for (int i = 0; i < basins; i++)
                    Fix(FurnitureType.Mirror, StripWall.Start, a, 0, row0, D, b0 + BasinPitch * (i + 0.5f), 0.54f, 0.025f, 0.64f, 1.2f, force: true);
            Fix(FurnitureType.HandDryer, StripWall.Start, a, 0, row0, D, b0 + run + 0.3f, 0.3f, 0.2f, 0.32f, 1.05f);

            // the stalls along the washroom's far side wall, and a cleaner's cupboard past them
            var (sw, left) = StallRow(stalls, D);
            for (int i = 0; i < stalls; i++)
            {
                float s0 = i * sw, s1 = (i + 1) * sw;
                int st = Room(RoomType.ToiletStall, row0, s0, wash1, s1);
                Link(wash, StripWall.End, st, row0, (s0 + s1) / 2, StallDoor, true);
                Stall(row0, s0, wash1, s1, StripWall.End);
            }
            if (left > 0)
            {
                int cup = Room(RoomType.Storage, row0, stalls * sw, wash1, D);
                Link(wash, StripWall.End, cup, row0, (stalls * sw + D) / 2, StallDoor, true);
            }

            // the urinals along the washroom's host wall, a screen between each two and at the row's ends
            for (int i = 0; i < urinals; i++)
                Fix(FurnitureType.Urinal, StripWall.Host, a1, 0, row0, D, a1 + 0.2f + UrinalPitch * (i + 0.5f), 0.36f, 0.32f, 0.7f);
            for (int i = 0; i <= urinals && urinals > 0; i++)
                Fix(FurnitureType.UrinalScreen, StripWall.Host, a1, 0, row0, D, a1 + 0.2f + UrinalPitch * i, 0.04f, 0.45f, 1.5f);
            return wash1;
        }

        /// <summary>A stall's bowl against the wall across from its door, the paper beside it.</summary>
        private void Stall(float a0, float b0, float a1, float b1, StripWall far)
        {
            float mid = (b0 + b1) / 2;
            Fix(FurnitureType.Toilet, far, a0, b0, a1, b1, mid, 0.4f, 0.65f, 0.75f, force: true);
            float along = far == StripWall.End ? a1 - Off - 0.45f : a0 + Off + 0.45f;
            Fix(FurnitureType.PaperRoll, StripWall.Host, a0, b0, a1, b1, along, 0.14f, 0.1f, 0.14f, 0.7f, force: true);
        }

        /// <summary>The accessible WC (SIA 500) from <paramref name="a"/>: the door at the far side, the bowl 0.45 m off the near wall.</summary>
        private void Accessible(float a)
        {
            float a1 = a + AccessW + _extra;
            int wc = Room(RoomType.AccessibleToilet, a, 0, a1, D);
            HostDoor(wc, a1 - 0.15f - ToiletSizing.Door / 2, ToiletSizing.Door, true, FurnitureType.WcSignAccessible);
            float axis = a + WallInset + 0.45f;
            Fix(FurnitureType.Toilet, StripWall.Back, a, 0, a1, D, axis, 0.4f, 0.7f, 0.75f, force: true);
            // the fold-down rail on the bowl's open side, the fixed one on the wall beside it
            float railB = D - Off - 0.75f / 2;
            var (rx, rz) = _fr.Point(axis + 0.36f, railB);
            _pieces.Add(new FurniturePlan { Type = FurnitureType.GrabRail, Floor = _f, X = rx, Z = rz, Turns = TurnsAgainst(_fr.Wall(StripWall.Back)), W = 0.08f, D = 0.75f, H = 0.8f });
            _taken.Add((axis + 0.32f, D - Off - 0.75f, axis + 0.40f, D - Off));
            Fix(FurnitureType.GrabRail, StripWall.Start, a, 0, a1, D, railB, 0.75f, 0.08f, 0.8f, force: true);
            // the basin within reach of the bowl, on the far wall
            Fix(FurnitureType.Sink, StripWall.End, a, 0, a1, D, Math.Max(1.0f, D - 1.0f), 0.5f, 0.45f, 0.85f);
        }

        /// <summary>The shared set from <paramref name="a"/>: an anteroom with a basin, a women's and a men's stall off its back wall.</summary>
        private void Shared(float a)
        {
            float a1 = a + SharedW + _extra, b1 = D - StallD, half = (a1 - a) / 2;
            int ante = Room(RoomType.ToiletLobby, a, 0, a1, b1);
            HostDoor(ante, a1 - 0.1f - ToiletSizing.Door / 2, ToiletSizing.Door, true, FurnitureType.WcSignAll);
            foreach (var (i, sign) in new[] { (0, FurnitureType.WcSignWomen), (1, FurnitureType.WcSignMen) })
            {
                float s0 = a + i * half, s1 = s0 + half, mid = (s0 + s1) / 2;
                int st = Room(RoomType.ToiletStall, s0, b1, s1, D);
                Link(ante, StripWall.Back, st, mid, b1, StallDoor, true);
                Stall2(s0, b1, s1, D);
                Sign(sign, mid, 1, b1);
            }
            if (Fix(FurnitureType.SinkRow, StripWall.Start, a, 0, a1, b1, 0.07f + 0.3f, 0.6f, 0.45f, 0.85f, gap: 0.04f))
                Fix(FurnitureType.Mirror, StripWall.Start, a, 0, a1, b1, 0.37f, 0.54f, 0.025f, 0.64f, 1.2f, force: true);
            Fix(FurnitureType.HandDryer, StripWall.Host, a, 0, a1, b1, a + 0.95f, 0.3f, 0.2f, 0.32f, 1.05f);
        }

        /// <summary>A stall entered from the host's side: the bowl against its back wall, the paper beside it.</summary>
        private void Stall2(float a0, float b0, float a1, float b1)
        {
            Fix(FurnitureType.Toilet, StripWall.Back, a0, b0, a1, b1, (a0 + a1) / 2, 0.4f, 0.65f, 0.75f, force: true);
            Fix(FurnitureType.PaperRoll, StripWall.Start, a0, b0, a1, b1, b1 - Off - 0.45f, 0.14f, 0.1f, 0.14f, 0.7f, force: true);
        }

        /// <summary>The single WC from <paramref name="a"/>: its own door, the bowl at the back, a basin on the side wall.</summary>
        private void Single(float a)
        {
            float a1 = a + SingleW + _extra;
            int wc = Room(RoomType.WC, a, 0, a1, D);
            HostDoor(wc, (a + a1) / 2, 0.8f, true, FurnitureType.WcSignAll);
            Fix(FurnitureType.Toilet, StripWall.Back, a, 0, a1, D, (a + a1) / 2, 0.4f, 0.65f, 0.75f, force: true);
            Fix(FurnitureType.Sink, StripWall.Start, a, 0, a1, D, Math.Max(1.05f, D - Off - 0.65f - 0.3f), 0.45f, 0.35f, 0.85f);
            Fix(FurnitureType.PaperRoll, StripWall.End, a, 0, a1, D, D - Off - 0.45f, 0.14f, 0.1f, 0.14f, 0.7f, force: true);
        }
    }

    /// <summary>
    /// Stands the toilet pieces planned on floor <paramref name="f"/> that are in room <paramref name="r"/>
    /// (<see cref="PublicToilets"/>); true when the room is a toilet room they furnish, which nothing else then does.
    /// </summary>
    private static bool PlaceToiletPieces(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed)
    {
        if (!ToiletPieceTable.TryGetValue(l.Floors[f], out var pieces)) return false;
        bool any = false;
        foreach (var p in pieces)
        {
            if (!(p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1)) continue;
            l.Furniture.Add(p);
            bool odd = p.Turns % 2 == 1;
            float w = (odd ? p.D : p.W) / 2, d = (odd ? p.W : p.D) / 2;
            placed.Add(new RectPlan(p.X - w, p.Z - d, p.X + w, p.Z + d));
            any = true;
        }
        return ToiletRoom(r.Type) || any && r.Type == RoomType.WC;
    }

    /// <summary>
    /// An existing WC made the public one (#1285), the single set: a leaf in its doorway from room
    /// <see cref="OpeningPlan.Other"/> swinging into it, and the sign over that doorway. Its bowl and basin
    /// are the WC's own (<see cref="Pieces"/>).
    /// </summary>
    internal static void AdoptWC(InteriorLayout l, int f, int wc, OpeningPlan door)
    {
        var r = l.Floors[f].Rooms[wc];
        var hostSide = Opposite(door.Side);
        if (!l.InnerDoors.Any(d => d.Floor == f && Math.Abs(d.Center - door.Center) < 0.01f && (d.Room == wc || d.Room == door.Other)))
            l.InnerDoors.Add(new InnerDoorPlan { Floor = f, Room = door.Other, Side = hostSide, Center = door.Center, Width = door.Width, Top = door.Top, Unit = -1 });
        const float w = 0.3f, d = 0.03f, h = 0.3f, off = WallInset + 0.01f + d / 2;
        var (x, z) = door.Side switch
        {
            Side.Front => (door.Center, r.Z0 - off),
            Side.Back => (door.Center, r.Z1 + off),
            Side.Left => (r.X0 - off, door.Center),
            _ => (r.X1 + off, door.Center),
        };
        float lift = Math.Min(door.Top + 0.1f, l.ClearOf(r) - h - 0.05f);
        ToiletPieceTable.GetValue(l.Floors[f], _ => new List<FurniturePlan>()).Add(new FurniturePlan
        {
            Type = FurnitureType.WcSignAll, Floor = f, X = x, Z = z, Turns = TurnsAgainst(hostSide), W = w, D = d, H = h, Lift = lift,
        });
    }

    /// <summary>The toilet set a finished plan holds (#1285), read off its rooms: what the probes check.</summary>
    internal static ToiletSet ToiletSetOf(InteriorLayout l)
    {
        var rooms = l.Floors.SelectMany(fl => fl.Rooms).ToList();
        if (rooms.Any(r => r.Type == RoomType.WomensToilet) && rooms.Any(r => r.Type == RoomType.MensToilet)) return ToiletSet.Gendered;
        if (rooms.Any(r => r.Type == RoomType.ToiletLobby)) return ToiletSet.Shared;
        if (l.Furniture.Any(p => p.Type == FurnitureType.WcSignAll)) return ToiletSet.Single;
        return ToiletSet.None;
    }

    /// <summary>The toilet pieces at the sizes the planner stands them, for the model viewer.</summary>
    private static readonly Piece[] ToiletPieces =
    {
        new(FurnitureType.SinkRow, 3 * BasinPitch, 0.45f, 0.85f, true),
        new(FurnitureType.HandDryer, 0.3f, 0.2f, 0.32f, true),
        new(FurnitureType.UrinalScreen, 0.04f, 0.45f, 1.5f, true),
        new(FurnitureType.GrabRail, 0.08f, 0.75f, 0.8f, true),
        new(FurnitureType.WcSignWomen, 0.3f, 0.03f, 0.3f, true),
        new(FurnitureType.WcSignMen, 0.3f, 0.03f, 0.3f, true),
        new(FurnitureType.WcSignAll, 0.3f, 0.03f, 0.3f, true),
        new(FurnitureType.WcSignAccessible, 0.3f, 0.03f, 0.3f, true),
        new(FurnitureType.Urinal, 0.36f, 0.32f, 0.7f, true),
    };

    /// <summary>
    /// The three sets in the model viewer (#1285): each planned into a strip along the back of a plain hall,
    /// the gendered one with its accessible WC.
    /// </summary>
    [Core.Showcase("Interiors", "Public toilets")]
    private static IEnumerable<(string Name, Func<Godot.Node3D> Make)> ShowcaseToilets()
    {
        foreach (var (name, w, depth, people) in new[] { ("Gendered", 14f, 3.4f, 150), ("Shared", 6f, 3.0f, 40), ("Single", 5f, 2.2f, 8) })
            yield return (name, () =>
            {
                var l = new InteriorLayout { Key = "toilets-" + name, Kind = BuildingKind.Commercial, Width = w, Depth = 8f, StoreyHeight = 3f, EntryX = 0, EntryWidth = 1.8f };
                var floor = new FloorPlan();
                l.Floors.Add(floor);
                var hall = new RoomPlan { X0 = -w / 2, Z0 = -4f, X1 = w / 2, Z1 = 4f - depth, Type = RoomType.Lobby };
                floor.Rooms.Add(hall);
                hall.Openings.Add(new OpeningPlan { Side = Side.Front, Center = 0, Width = 1.8f, Top = 2.2f, Kind = OpeningKind.Entry });
                var (_, rest) = PublicToilets(l, floor, 0, new RectPlan(-w / 2, 4f - depth, w / 2, 4f), Side.Front, (-w / 2, 4f), people);
                if (rest != null)
                {
                    var store = new RoomPlan { Type = RoomType.Storage };
                    SetRect(store, rest);
                    floor.Rooms.Add(store);
                    Opening(floor.Rooms, floor.Rooms.Count - 1, Side.Front, 0, (rest.X0 + rest.X1) / 2, InnerDoor, 2.05f, OpeningKind.Door);
                }
                Furnish(l, new Random(1));
                var node = InteriorNode.Create(l, InteriorMeshBuilder.Build(l), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Godot.Transform3D.Identity);
                foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = leaf.Name.ToString().StartsWith("Leaf_flat");
                return node;
            });
    }
}
