using Godot;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// The links on the bodies (#1266 parts 3.2-3.4): the tether, the belay and the caught fall on every body simulated
/// here (the own one, a check's partner, a server-run bot), from the partner's replicated position; the held hands and
/// the ropes drawn on every peer. All of it from the book and the positions: nothing new on the wire.
/// </summary>
public partial class TogetherService
{
    /// <summary>A body this copy simulates while it is tied: its trail, its rope, the delegates hooked on it.</summary>
    private sealed class Owned
    {
        public long Peer;
        public FootPlayer? Body;
        public bool Hooked;
        public readonly TetherTrail Trail = new();
        public TetherState State;
        public LinkMode Mode;
        public long Partner;
        public FootPlayer? PartnerBody;
        public float RopeLen = LinkBook.RopeLength;
        public bool Hauled, Hanging;
        public StaminaCompany Company;
        public float DropCheck;
        public Func<(Vector3 Wish, bool Run), (Vector3 Wish, bool Run)> TetherFn = null!;
        public Func<Vector3, float, (Vector3 Velocity, bool Held)> RopeFn = null!;
        public Func<StaminaCompany> CompanyFn = null!;
    }

    private readonly Dictionary<long, Owned> _owned = new();
    private readonly List<long> _visit = new();

    private Owned OwnedFor(long peer)
    {
        if (_owned.TryGetValue(peer, out var o)) return o;
        o = new Owned { Peer = peer };
        var self = o;
        o.TetherFn = own => self.Hooked ? Tether.Apply(own, self.State) : own;
        o.RopeFn = (v, dt) => RopeStep(self, v, dt);
        o.CompanyFn = () => self.Company;
        _owned[peer] = o;
        return o;
    }

    private static void Unhook(Owned o)
    {
        if (o.Body is { } b && GodotObject.IsInstanceValid(b) && o.Hooked)
        {
            if (b.Tethered == o.TetherFn) b.Tethered = null;
            if (b.RopeHold == o.RopeFn) b.RopeHold = null;
            if (b.CompanyNow == o.CompanyFn) b.CompanyNow = null;
        }
        o.Hooked = false;
        o.Hanging = o.Hauled = false;
        o.RopeLen = LinkBook.RopeLength;
        o.Company = StaminaCompany.None;
        o.Trail.Clear();
    }

    private void ResetTrail(long follower)
    {
        if (_owned.TryGetValue(follower, out var o)) o.Trail.Clear();
    }

    private void SetHauled(long peer, bool on)
    {
        var o = OwnedFor(peer);
        o.Hauled = on;
        GD.Print($"[together] hauled {(on ? "up" : "no more")}");
    }

    /// <summary>Whom the local body would haul now (E held at the rope): a rope neighbour hanging below, else 0.</summary>
    public long HaulTarget(FootPlayer me)
    {
        long self = Self;
        if (Links.ModeOf(self) != LinkMode.Rope || !me.IsOnFloor()) return 0;
        return Hangs(Links.LeaderOf(self)) ? Links.LeaderOf(self) : Hangs(Links.FollowerOf(self)) ? Links.FollowerOf(self) : 0;

        bool Hangs(long other) => other != 0 && Body(other) is { } b && b.PoseKind is FootPlayer.PoseAir or FootPlayer.PoseFall
            && b.GlobalPosition.Y < me.GlobalPosition.Y - 1.5f;
    }

    partial void PhysicsLinks(float dt)
    {
        if (Links.Count == 0 && _owned.Count == 0) return;
        _visit.Clear();
        if (Me?.Invoke() is { } me) Visit(Self, me, dt);
        if (PairPeer != 0 && Body(PairPeer) is { } pair) Visit(PairPeer, pair, dt);
        if (_server)
            foreach (var l in Links.All)
            {
                if (Body(l.Follower) is { Bot: true } fb) Visit(l.Follower, fb, dt);
                if (Body(l.Leader) is { Bot: true } lb) Visit(l.Leader, lb, dt);
            }
        foreach (var (peer, o) in _owned)
            if (o.Hooked && !_visit.Contains(peer)) Unhook(o);
    }

    private void Visit(long peer, FootPlayer body, float dt)
    {
        if (_visit.Contains(peer)) return;
        _visit.Add(peer);
        var o = OwnedFor(peer);
        if (o.Body != body) { Unhook(o); o.Body = body; }
        long partner = Links.PartnerOf(peer);
        if (partner == 0 || Body(partner) is not { } pb || !IsInstanceValid(pb))
        {
            if (o.Hooked) Unhook(o);
            return;
        }
        if (!o.Hooked)
        {
            body.Tethered = o.TetherFn;
            body.RopeHold = o.RopeFn;
            body.CompanyNow = o.CompanyFn;
            o.Hooked = true;
        }
        var mode = Links.ModeOf(peer);
        if (mode != o.Mode || partner != o.Partner) o.Trail.Clear();
        o.Mode = mode;
        o.Partner = partner;
        o.PartnerBody = pb;
        var self = body.GlobalPosition;
        var other = pb.GlobalPosition;
        bool led = mode == LinkMode.Hand && Links.LeaderOf(peer) == partner;
        if (led) o.Trail.Push(other);
        // the pull never points over a drop the walk would refuse: a look at the ground toward the partner, 4 times a second
        if ((o.DropCheck -= dt) <= 0f)
        {
            o.DropCheck = 0.25f;
            o.State.PullBlocked = DropToward(body, other);
        }
        o.State.Self = self;
        o.State.Partner = other;
        o.State.Length = LinkRules.Length(mode);
        o.State.Led = led;
        o.State.TrailTarget = led ? o.Trail.Target(other) : null;
        o.State.PartnerSpeed = pb.WorldVelocity.Length();
        // the stamina's company: belayed on a face while the partner stands or holds still, else linked when close
        bool partnerHolds = pb.PoseKind == FootPlayer.PoseStride && pb.RideKindId == 0
            || pb.PoseKind == FootPlayer.PoseWallClimb && pb.WorldVelocity.Length() < 0.3f;
        bool climbing = body.IsClimbing;
        float dist = self.DistanceTo(other);
        o.Company = mode == LinkMode.Rope && climbing && partnerHolds ? StaminaCompany.Belayed
            : dist <= LinkRules.Length(mode) + 0.5f ? StaminaCompany.Linked : StaminaCompany.None;
        // a rope that caught a fall: back on the floor, the rope's full length again
        if (body.IsOnFloor() || climbing)
        {
            if (o.Hanging) GD.Print($"[together] {(climbing ? "climbing" : "standing")} again off the rope");
            o.Hanging = false;
            if (!o.Hauled) o.RopeLen = LinkBook.RopeLength;
        }
        else if (o.Hanging && o.Hauled) o.RopeLen = Mathf.Max(1.2f, o.RopeLen - HaulSpeed * dt);
    }

    /// <summary>Hauled up the rope this fast, m/s.</summary>
    public const float HaulSpeed = 0.6f;

    private static bool DropToward(FootPlayer body, Vector3 partner)
    {
        if (body.Terrain is not { } terrain) return false;
        var flat = new Vector3(partner.X - body.GlobalPosition.X, 0, partner.Z - body.GlobalPosition.Z);
        if (flat.LengthSquared() < 0.01f) return false;
        var ahead = body.GlobalPosition + flat.Normalized() * 1.5f;
        return terrain.TryGetHeight(ahead, out float h) && h < body.GlobalPosition.Y - 2.5f;
    }

    /// <summary>
    /// The rope round a partner who stands or holds still (a body in the air): a fall is caught at the rope's length,
    /// the velocity projected onto the sphere round the partner's harness, so it swings, then hangs. If both fall,
    /// nothing holds.
    /// </summary>
    private (Vector3 Velocity, bool Held) RopeStep(Owned o, Vector3 v, float dt)
    {
        if (!o.Hooked || o.Mode != LinkMode.Rope || o.Body is not { } body || o.PartnerBody is not { } pb || !IsInstanceValid(pb)) return (v, false);
        bool partnerHolds = pb.PoseKind == FootPlayer.PoseStride && pb.RideKindId == 0
            || pb.PoseKind == FootPlayer.PoseWallClimb && pb.WorldVelocity.Length() < 0.5f;
        if (!partnerHolds) return (v, false);
        var anchor = pb.GlobalPosition + Vector3.Up * HarnessHeight;
        var d = body.GlobalPosition + Vector3.Up * HarnessHeight - anchor;
        float dist = d.Length();
        if (dist <= o.RopeLen || dist < 1e-3f) return (v, o.Hanging);
        var radial = d / dist;
        float outward = v.Dot(radial);
        if (outward > 0f) v -= radial * outward;
        v -= radial * Mathf.Min((dist - o.RopeLen) * 4f, 6f);
        if (!o.Hanging)
        {
            o.Hanging = true;
            GD.Print(FormattableString.Invariant($"[together] fall held at {dist:F1} m"));
            PlayAt(o.Peer, TogetherSounds.Zip);
        }
        return (v, true);
    }

    /// <summary>The harness above the feet, m: where the rope ties on.</summary>
    public const float HarnessHeight = 1.0f;

    /// <summary>Whether a simulated body hangs in its rope now (checks).</summary>
    public bool Hanging(long peer) => _owned.TryGetValue(peer, out var o) && o.Hanging;

    /// <summary>The company a simulated body's stamina sees now (checks).</summary>
    public StaminaCompany CompanyOf(long peer) => _owned.TryGetValue(peer, out var o) && o.Hooked ? o.Company : StaminaCompany.None;

    // ---- every peer: the hands and the ropes ----------------------------------------------------------

    private HashSet<FootPlayer> _holdNow = new(), _holdBefore = new();
    private readonly Dictionary<long, RopeNode> _ropes = new();
    private AudioStreamPlayer3D? _sfx;

    partial void ProcessLinks(double delta, double now)
    {
        if (Authority) TickLinksAuthority(now);
        if (Net.NetworkManager.DedicatedServer) return;
        UpdateHolds();
        PlaceRopes();
    }

    /// <summary>
    /// Each hand link: the two wrists meet at the midpoint between the bodies' facing sides, 0.95 m up; each figure
    /// reaches with the hand on its partner's side (the builder clamps the reach and solves the elbow).
    /// </summary>
    private void UpdateHolds()
    {
        (_holdBefore, _holdNow) = (_holdNow, _holdBefore);
        _holdNow.Clear();
        foreach (var l in Links.All)
        {
            if (l.Mode != LinkMode.Hand || Body(l.Leader) is not { Visible: true } a || Body(l.Follower) is not { Visible: true } b) continue;
            var (point, aLeft, bLeft) = HoldPoint(a.GlobalPosition, a.GlobalBasis.X, b.GlobalPosition, b.GlobalBasis.X);
            a.HandHold = (point, aLeft);
            b.HandHold = (point, bLeft);
            _holdNow.Add(a);
            _holdNow.Add(b);
        }
        foreach (var body in _holdBefore)
            if (!_holdNow.Contains(body) && IsInstanceValid(body)) body.HandHold = null;
    }

    /// <summary>Where two held hands meet, and which hand each reaches with (true: its left), from the feet and the right vectors.</summary>
    public static (Vector3 Point, bool ALeft, bool BLeft) HoldPoint(Vector3 a, Vector3 aRight, Vector3 b, Vector3 bRight)
    {
        aRight.Y = 0;
        bRight.Y = 0;
        aRight = aRight.LengthSquared() > 1e-6f ? aRight.Normalized() : Vector3.Right;
        bRight = bRight.LengthSquared() > 1e-6f ? bRight.Normalized() : Vector3.Right;
        bool aLeft = (b - a).Dot(aRight) < 0f;
        bool bLeft = (a - b).Dot(bRight) < 0f;
        var sa = a + aRight * (aLeft ? -0.2f : 0.2f);
        var sb = b + bRight * (bLeft ? -0.2f : 0.2f);
        var mid = (sa + sb) * 0.5f;
        mid.Y = (a.Y + b.Y) * 0.5f + 0.95f;
        return (mid, aLeft, bLeft);
    }

    /// <summary>One rope node per rope link, made and freed as the book changes.</summary>
    private void SyncRopes()
    {
        if (Net.NetworkManager.DedicatedServer) return;
        _gone.Clear();
        foreach (var (follower, node) in _ropes)
            if (Links.ModeOf(follower) != LinkMode.Rope || Links.LeaderOf(follower) == 0)
            {
                node.QueueFree();
                _gone.Add(follower);
            }
        foreach (long f in _gone) _ropes.Remove(f);
        foreach (var l in Links.All)
            if (l.Mode == LinkMode.Rope && !_ropes.ContainsKey(l.Follower))
            {
                var node = new RopeNode { Name = $"Rope{l.Follower}" };
                AddChild(node);
                _ropes[l.Follower] = node;
            }
    }

    private void PlaceRopes()
    {
        foreach (var (follower, node) in _ropes)
        {
            if (Body(follower) is { Visible: true } a && Body(Links.LeaderOf(follower)) is { Visible: true } b)
                node.Place(a.GlobalPosition + Vector3.Up * HarnessHeight, b.GlobalPosition + Vector3.Up * HarnessHeight, LinkBook.RopeLength);
            else if (node.Visible) node.Visible = false;
        }
    }

    /// <summary>The rope between a follower and its leader (checks, screenshots), or null.</summary>
    public RopeNode? RopeOf(long follower) => _ropes.GetValueOrDefault(follower);

    private void PlayAt(long peer, AudioStream stream)
    {
        if (Net.NetworkManager.DedicatedServer || Body(peer) is not { } body) return;
        if (_sfx == null)
        {
            _sfx = new AudioStreamPlayer3D { Name = "TogetherSfx", Bus = "Master", UnitSize = 6f };
            AddChild(_sfx);
        }
        _sfx.GlobalPosition = body.GlobalPosition + Vector3.Up;
        _sfx.Stream = stream;
        _sfx.Play();
    }
}
