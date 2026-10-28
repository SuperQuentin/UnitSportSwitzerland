using Godot;
using UnitSport.Avatar;
using UnitSport.Net;

namespace UnitSport.Interiors.Patrons;

/// <summary>
/// A venue's patrons (#1244, plan <c>docs/plans/venue-patrons-ambience.md</c>): figures at the tables, on the stools,
/// at the machines, in the queues, on the dance floor and in a showing hall's seats, as <see cref="BowlingDeco"/>'s
/// bowlers are: client-side decoration, a pure function of the plan (<see cref="PatronSpots"/>) and the shared clock
/// (<see cref="PatronSchedule"/>), nothing on the wire, built with the interior (or a tile's terraces).
///
/// <para>
/// What the schedule wants is shown only while a spot is out of view (outside the camera's frustum, or far), so
/// nobody sees a guest pop in or out; a queue steps forward in view, its served customer stepping aside until you
/// look away. A cinema hall's audience changes only while no player is in the hall (the owner's rule). A place a
/// player sits or stands on has no patron. The nearest <see cref="NearMost"/> are <see cref="SkinnedFigure"/>s posed
/// a few times a second (<see cref="PatronPoser"/>); the rest are still copies in a few <see cref="MultiMesh"/>es.
/// </para>
/// </summary>
public partial class VenuePatrons : Node3D
{
    /// <summary><c>--patronhour h</c>: every venue as busy as at that hour of the day, whatever the clock (shots, checks).</summary>
    public static readonly double? ForcedHour = Core.CmdArgs.Double("--patronhour");

    /// <summary>The game hour at a moment of the shared clock, or <see cref="ForcedHour"/>.</summary>
    public static double HourAt(double now) => ForcedHour ?? World.WorldClock.HourAt(now);

    /// <summary>Skinned, moving figures at most; the rest are still copies.</summary>
    public const int NearMost = 32;
    /// <summary>How often the schedule is read, s.</summary>
    private const double Tick = 0.25;
    /// <summary>Beyond this a spot counts as out of view, m.</summary>
    private const float SeenRange = 45f;
    /// <summary>The poses' rate near and far (Hz): a loop of small moves needs no more.</summary>
    private const double PoseHzNear = 15, PoseHzFar = 5;
    private const float NearPoseRange = 10f;
    /// <summary>A showing's audience is drawn again every this many seconds (taken only while the hall is empty).</summary>
    private const double ShowingLength = 900;
    /// <summary>Still copies: looks per pose.</summary>
    private const int StillLooks = 8;

    /// <summary>One person shown.</summary>
    private sealed class Patron
    {
        public int Spot;
        public int Look;
        public long Id;
        public float Slot, Target;
        public bool Served;
        public double ServedAt;
        public SkinnedFigure? Figure;
        public double PosedAt = -1;
        public float Dist;
    }

    /// <summary>A table, a stool, a queue: places that fill together.</summary>
    private sealed class Group
    {
        public uint Key;
        public int First, Count;
        public PatronActivity Activity;
        public PatronVenue Venue;
        public Func<double, float> Busy = null!;
        /// <summary>A seating bank's hall number, -1 for anything else.</summary>
        public int Hall = -1;
        public readonly List<Patron> Queue = new();
        public double CheerAt;
    }

    private readonly List<PatronSpot> _spots;
    private readonly FurnitureType[] _pieces;
    private readonly Patron?[] _seated;
    private readonly List<Group> _groups = new();
    private readonly InteriorNode? _node;
    /// <summary>A cinema hall with patrons' seats: the showing whose audience sits, and the room its banks stand in.</summary>
    private sealed class Hall
    {
        public int No, Floor, Room;
        public long Showing = long.MinValue;
    }

    private readonly List<Hall> _halls = new();
    private readonly List<Patron> _shown = new();
    private readonly Stack<SkinnedFigure> _spare = new();
    private readonly Dictionary<(PatronPose, int), MultiMeshInstance3D> _stills = new();
    private readonly Dictionary<(PatronPose, int), List<Transform3D>> _stillWant = new();
    private Func<double, float> _weather = _ => 1f;
    private double _nextTick;
    private bool _first = true;
    private Material? _material;
    private static readonly Dictionary<(PatronPose, int), ArrayMesh> StillMeshes = new();

    private VenuePatrons(List<PatronSpot> spots, FurnitureType[] pieces, InteriorNode? node)
    {
        Name = "Patrons";
        _spots = spots;
        _pieces = pieces;
        _node = node;
        _seated = new Patron?[spots.Count];
        for (int i = 0; i < spots.Count;)
        {
            var s = spots[i];
            int n = 1;
            while (i + n < spots.Count && spots[i + n].Group == s.Group) n++;
            var g = new Group { Key = s.Group, First = i, Count = n, Activity = s.Activity, Venue = s.Venue };
            var venue = s.Venue;
            g.Busy = now => PatronSchedule.Busy(venue, HourAt(now)) * _weather(now);
            if (node != null && pieces[i] == FurnitureType.SeatingBank)
            {
                var l = node.Layout;
                var bank = l.Furniture[s.Piece];
                g.Hall = CinemaBooths.HallAt(l, bank.X, bank.Z);
                if (g.Hall >= 0 && !_halls.Exists(h => h.No == g.Hall)) _halls.Add(new Hall { No = g.Hall, Floor = bank.Floor, Room = s.Room });
            }
            _groups.Add(g);
            i += n;
        }
    }

    /// <summary>The patrons of a built interior's venue rooms, if it has any.</summary>
    public static void AddTo(InteriorNode node)
    {
        var spots = PatronSpots.Of(node.Layout);
        if (spots.Count == 0) return;
        var pieces = spots.Select(s => node.Layout.Furniture[s.Piece].Type).ToArray();
        node.AddChild(new VenuePatrons(spots, pieces, node));
    }

    /// <summary>A terrace's guests: their spots in <paramref name="root"/>'s frame; <paramref name="weather"/> keeps them in when it is cold or snowing.</summary>
    public static VenuePatrons? ForTerrace(List<PatronSpot> spots, Func<double, float> weather)
    {
        if (spots.Count == 0) return null;
        return new VenuePatrons(spots, new FurnitureType[spots.Count], null) { _weather = weather };
    }

    /// <summary>The venue whose hours a terrace keeps, by its music's mood; null for one with no terrace guests.</summary>
    public static PatronVenue? VenueOf(Audio.Tracker.VenueMood mood) => mood switch
    {
        Audio.Tracker.VenueMood.Beiz => PatronVenue.Beiz,
        Audio.Tracker.VenueMood.Pizzeria => PatronVenue.Pizzeria,
        Audio.Tracker.VenueMood.Cafe => PatronVenue.Cafe,
        Audio.Tracker.VenueMood.FastFood => PatronVenue.FastFood,
        Audio.Tracker.VenueMood.Bar => PatronVenue.Bar,
        Audio.Tracker.VenueMood.Pub => PatronVenue.Pub,
        Audio.Tracker.VenueMood.Club => PatronVenue.Club,
        _ => null,
    };

    /// <summary>The air at a terrace, °C: the day's weather (#887) at the Plateau's height.</summary>
    private const float TerraceAltitude = 500f;

    /// <summary>
    /// How many of a terrace's guests stay out (0..1) at a moment of the shared clock: none from 23:00 to 07:00
    /// (#1244, quiet hours), none while it snows, fewer the colder it is (all of them from 14 °C, none under 4 °C).
    /// </summary>
    public static float TerraceWeather(double now)
    {
        double hour = HourAt(now);
        if (hour >= 23 || hour < 7) return 0f;
        if (World.Snow.SnowService.Instance is not { } snow) return 1f;
        var w = snow.Today;
        if (w.Snowing(hour < 6 ? hour + 24 : hour)) return 0f;
        return Math.Clamp((w.TempAt(hour, TerraceAltitude) - 4f) / 10f, 0f, 1f);
    }

    /// <summary>Patrons further than this from the camera are not looked at (a tile's terraces far off), m.</summary>
    private const float AwakeRange = 120f;

    public override void _Process(double delta)
    {
        if (GetViewport().GetCamera3D() is not { } camera) return;
        if (_node == null && ToGlobal(_spots[0].At).DistanceSquaredTo(camera.GlobalPosition) > AwakeRange * AwakeRange)
        {
            if (_shown.Count > 0) Empty();
            return;
        }
        double now = ClockSync.ServerNow;
        if (now >= _nextTick || _first)
        {
            _nextTick = now + Tick;
            Schedule(now, camera);
            _first = false;
        }
        Animate(now, delta, camera);
    }

    // —— what the schedule wants, taken when nobody sees it change ——

    private void Schedule(double now, Camera3D camera)
    {
        foreach (var g in _groups)
        {
            if (g.Hall >= 0) continue;
            if (g.Activity == PatronActivity.Queue) { QueueOf(g, now, camera); continue; }
            var (party, window) = PatronSchedule.Party(g.Key, g.Count, g.Activity, now, g.Busy);
            int first = PatronSchedule.FirstSeat(g.Key, window, g.Count);
            for (int k = 0; k < g.Count; k++)
            {
                int i = g.First + k;
                int place = (k - first + g.Count) % g.Count;
                int look = place < party ? PatronSchedule.Look(g.Key, window, place) : -1;
                bool taken = Taken(i);
                if (taken) look = -1;
                if (look == (_seated[i]?.Look ?? -1)) continue;
                // a player on the spot clears it at once; anything else changes only out of view
                if (!_first && !taken && Seen(i, camera)) continue;
                Seat(i, look);
            }
            if (g.Activity == PatronActivity.Settee && now >= g.CheerAt)
                g.CheerAt = now + 14 + 20 * PatronSchedule.Unit(g.Key, (uint)now);
        }
        Audiences(now);
    }

    /// <summary>Puts person <paramref name="look"/> on spot <paramref name="i"/> (-1 empties it).</summary>
    private void Seat(int i, int look)
    {
        if (_seated[i] is { } old) Drop(old);
        _seated[i] = null;
        if (look < 0) return;
        var p = new Patron { Spot = i, Look = look };
        _seated[i] = p;
        _shown.Add(p);
    }

    /// <summary>Everyone gone at once (far off, nobody to see it); read afresh on the next look.</summary>
    private void Empty()
    {
        foreach (var p in _shown) Release(p);
        _shown.Clear();
        Array.Clear(_seated);
        foreach (var g in _groups) g.Queue.Clear();
        foreach (var mmi in _stills.Values) mmi.Multimesh.InstanceCount = 0;
        _first = true;
    }

    private void Drop(Patron p)
    {
        _shown.Remove(p);
        if (p.Figure != null) Release(p);
    }

    /// <summary>A player (or a walker) on a spot, or about to sit on it.</summary>
    private bool Taken(int i)
    {
        var at = ToGlobal(_spots[i].At);
        foreach (var body in InteriorNode.BeamBodies)
        {
            if (!IsInstanceValid(body) || !body.IsInsideTree()) continue;
            var d = body.GlobalPosition - at;
            if (d.Y is > -1.3f and < 1.3f && d.X * d.X + d.Z * d.Z < 0.45f * 0.45f) return true;
        }
        return false;
    }

    /// <summary>Whether the camera could see a change at spot <paramref name="i"/>: near, and in its frustum.</summary>
    private bool Seen(int i, Camera3D camera) => SeenAt(ToGlobal(_spots[i].At + Vector3.Up * 0.4f), camera);

    private static bool SeenAt(Vector3 at, Camera3D camera) =>
        at.DistanceSquaredTo(camera.GlobalPosition) < SeenRange * SeenRange && camera.IsPositionInFrustum(at);

    /// <summary>
    /// A counter's queue: one served every few seconds, the rest stepping up; the served one stands aside until out
    /// of view; a newcomer joins the back only out of view.
    /// </summary>
    private void QueueOf(Group g, double now, Camera3D camera)
    {
        double serve = ServeOf(_pieces[g.First]);
        var (front, len) = PatronSchedule.Queue(g.Key, now, serve, g.Count, g.Busy);
        var q = g.Queue;
        for (int k = q.Count - 1; k >= 0; k--)
        {
            var p = q[k];
            if (!p.Served && p.Id < front) (p.Served, p.ServedAt) = (true, now);
            bool hidden = !SeenAt(ToGlobal(SlotAt(g, p)), camera);
            // served and gone once out of sight (or after a while); left the queue when it shrank, out of sight
            if (p.Served && (hidden || now - p.ServedAt > 8) || !p.Served && p.Id >= front + len && hidden || _first && p.Served)
            {
                q.RemoveAt(k);
                Drop(p);
                continue;
            }
            if (!p.Served) p.Target = p.Id - front;
        }
        for (long id = front; id < front + len; id++)
        {
            bool there = false;
            foreach (var other in q) there |= other.Id == id;
            if (there) continue;
            int slot = (int)(id - front);
            if (!_first && Seen(g.First + slot, camera)) continue;
            if (Taken(g.First + slot)) continue;
            var p = new Patron { Spot = g.First, Id = id, Look = PatronSchedule.Look(g.Key, id, 0), Slot = slot, Target = slot };
            q.Add(p);
            _shown.Add(p);
        }
    }

    /// <summary>Seconds a counter takes per customer.</summary>
    private static double ServeOf(FurnitureType counter) => counter switch
    {
        FurnitureType.BoxOffice => 22,
        FurnitureType.ConcessionStand => 30,
        FurnitureType.KebabCounter => 45,
        FurnitureType.OrderCounter => 35,
        FurnitureType.PastryCase => 30,
        _ => 40,
    };

    /// <summary>Where a queuer stands: between its places by its eased slot, or a step aside from the counter once served.</summary>
    private Vector3 SlotAt(Group g, Patron p)
    {
        var front = _spots[g.First];
        if (p.Served)
            return front.At + new Basis(Vector3.Up, front.Yaw) * new Vector3(0.85f, 0, 0.2f);
        float s = Math.Clamp(p.Slot, 0, g.Count - 1);
        int a = (int)s, b = Math.Min(a + 1, g.Count - 1);
        return _spots[g.First + a].At.Lerp(_spots[g.First + b].At, s - a);
    }

    /// <summary>
    /// Each showing hall's audience (#1244, the owner's rule): drawn per showing, from the hall's reel and the shared
    /// clock, but changed only while no player is in the hall, so nobody ever sees a seat fill or empty.
    /// </summary>
    private void Audiences(double now)
    {
        if (_node == null || _halls.Count == 0) return;
        var l = _node.Layout;
        foreach (var h in _halls)
        {
            int hall = h.No;
            var (_, screen) = CinemaBooths.Instance?.StateOf(l.Key, hall) ?? CinemaBooths.Unpack(CinemaBooths.Default(l.Key, hall));
            uint hallKey = PatronSchedule.Mix(PatronSchedule.KeyOf((int)l.CenterX, (int)l.CenterZ, hall, 0xC1u), (uint)hall, 3u);
            long showing = screen ? (long)Math.Floor((now + PatronSchedule.Unit(hallKey, 1) * ShowingLength) / ShowingLength) : -1;
            if (showing == h.Showing || !_first && Occupied(l, h.Floor, h.Room)) continue;
            h.Showing = showing;
            uint draw = PatronSchedule.Mix(hallKey, (uint)showing, (uint)(showing >> 32));
            float fill = showing < 0 ? 0f : (float)(0.3 + 0.5 * PatronSchedule.Unit(draw, 2)) * PatronSchedule.Busy(PatronVenue.Cinema, HourAt(now)) / 0.85f;
            foreach (var g in _groups)
            {
                if (g.Hall != hall) continue;
                for (int k = 0; k < g.Count; k++)
                {
                    int i = g.First + k;
                    bool sits = PatronSchedule.Unit(PatronSchedule.Mix(draw, g.Key, (uint)k), 3) < fill && !Taken(i);
                    Seat(i, sits ? PatronSchedule.Look(draw, g.Key, k) : -1);
                }
            }
        }
    }

    /// <summary>A player (or a walker) inside a hall's room.</summary>
    private bool Occupied(InteriorLayout l, int floor, int room)
    {
        if (room < 0) return false;
        var r = l.Floors[floor].Rooms[room];
        float y0 = l.FloorY(floor) - 1f;
        foreach (var body in InteriorNode.BeamBodies)
        {
            if (!IsInstanceValid(body) || !body.IsInsideTree()) continue;
            var at = _node!.ToLocal(body.GlobalPosition);
            if (at.X >= r.X0 && at.X <= r.X1 && at.Z >= r.Z0 && at.Z <= r.Z1 && at.Y > y0 && at.Y < y0 + 12f) return true;
        }
        return false;
    }

    // —— drawing ——

    private void Animate(double now, double delta, Camera3D camera)
    {
        var eye = ToLocal(camera.GlobalPosition);
        foreach (var g in _groups)
            foreach (var p in g.Queue)
                if (!p.Served && p.Slot != p.Target)
                    p.Slot = Mathf.MoveToward(p.Slot, p.Target, (float)delta * 1.4f);
        foreach (var p in _shown) p.Dist = At(p).DistanceSquaredTo(eye);
        _shown.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        foreach (var list in _stillWant.Values) list.Clear();
        for (int k = 0; k < _shown.Count; k++)
        {
            var p = _shown[k];
            var spot = _spots[p.Spot];
            if (k < NearMost)
            {
                if (p.Figure == null) Claim(p);
                var f = p.Figure!;
                var t = Frame(p);
                if (f.Transform != t) f.Transform = t;
                double hz = p.Dist < NearPoseRange * NearPoseRange ? PoseHzNear : PoseHzFar;
                if (now - p.PosedAt < 1 / hz) continue;
                p.PosedAt = now;
                float cheer = 0f;
                if (spot.Activity == PatronActivity.Settee && GroupOf(p.Spot) is { } g)
                    cheer = (float)Math.Clamp(1 - Math.Abs(now - g.CheerAt + 1.2) / 1.2, 0, 1);
                PatronPoser.Pose(f, spot, _pieces[p.Spot], p.Look, now, cheer);
                continue;
            }
            if (p.Figure != null) Release(p);
            var key = (spot.Pose, p.Look % StillLooks);
            if (!_stillWant.TryGetValue(key, out var want)) _stillWant[key] = want = new List<Transform3D>();
            want.Add(Frame(p));
        }
        Stills();
    }

    private Group? GroupOf(int spot)
    {
        foreach (var g in _groups)
            if (spot >= g.First && spot < g.First + g.Count) return g;
        return null;
    }

    /// <summary>Where a patron is, in this node's frame: its spot, or a queuer's eased place.</summary>
    private Vector3 At(Patron p)
    {
        var s = _spots[p.Spot];
        if (s.Activity != PatronActivity.Queue) return s.At;
        foreach (var g in _groups)
            if (g.First == p.Spot) return SlotAt(g, p);
        return s.At;
    }

    private Transform3D Frame(Patron p) => new(new Basis(Vector3.Up, _spots[p.Spot].Yaw), At(p));

    private void Claim(Patron p)
    {
        _material ??= HumanMeshBuilder.FigureMaterial();
        var f = _spare.Count > 0 ? _spare.Pop() : null;
        if (f == null)
        {
            f = new SkinnedFigure { Name = "Patron", MaterialOverride = _material };
            AddChild(f);
        }
        f.Visible = true;
        f.Show(new FigureLook(HumanPalette.ForRider(p.Look)));
        p.Figure = f;
        p.PosedAt = -1;
    }

    private void Release(Patron p)
    {
        if (p.Figure is not { } f) return;
        f.Visible = false;
        _spare.Push(f);
        p.Figure = null;
    }

    /// <summary>The far patrons as still copies: one multimesh per pose and look, instances written only when they change.</summary>
    private void Stills()
    {
        foreach (var (key, want) in _stillWant)
        {
            if (!_stills.TryGetValue(key, out var mmi))
            {
                if (want.Count == 0) continue;
                _material ??= HumanMeshBuilder.FigureMaterial();
                mmi = new MultiMeshInstance3D
                {
                    Name = $"Still_{key.Item1}_{key.Item2}",
                    MaterialOverride = _material,
                    Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = StillMesh(key.Item1, key.Item2) },
                };
                AddChild(mmi);
                _stills[key] = mmi;
            }
            var mm = mmi.Multimesh;
            bool same = mm.InstanceCount == want.Count;
            for (int i = 0; same && i < want.Count; i++) same = mm.GetInstanceTransform(i) == want[i];
            if (same) continue;
            mm.InstanceCount = want.Count;
            for (int i = 0; i < want.Count; i++) mm.SetInstanceTransform(i, want[i]);
        }
    }

    /// <summary>A still figure of a pose and look, baked once from a posed skinned one (in its node's frame).</summary>
    private ArrayMesh StillMesh(PatronPose pose, int look)
    {
        if (StillMeshes.TryGetValue((pose, look), out var mesh)) return mesh;
        var f = new SkinnedFigure();
        AddChild(f);
        f.Show(new FigureLook(HumanPalette.ForRider(look * 131 + 7)));
        var spot = new PatronSpot(0, 0, 1, new Vector3(0, pose == PatronPose.Stool ? PatronSpots.StoolHip : 0.45f, 0), 0, 0,
            pose, pose == PatronPose.Stand ? PatronActivity.Queue : PatronActivity.Watch, PatronVenue.Cinema, 0, -1);
        PatronPoser.Pose(f, spot, FurnitureType.SeatingBank, look, 0);
        mesh = f.Baked();
        RemoveChild(f);
        f.QueueFree();
        StillMeshes[(pose, look)] = mesh;
        return mesh;
    }
}
