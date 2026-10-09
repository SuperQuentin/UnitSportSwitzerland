using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Items;

/// <summary>
/// An item stack at the moment it enters the world (dropped, thrown, or put back by the server when
/// its thrower left): the spawn data every peer builds its <see cref="DroppedItem"/> from.
/// </summary>
/// <param name="Owner">Peer that simulates the fall; 0 for the server.</param>
/// <param name="Settled">Already at rest: frozen where it lies, no physics.</param>
public readonly record struct DropState(
    string Name,
    long Owner,
    ItemStack Stack,
    GlobalPos Position,
    Vector3 Rotation,
    Vector3 Velocity,
    Vector3 Spin,
    bool Settled = false,
    int Token = 0)
{
    public Godot.Collections.Dictionary ToDict()
    {
        var d = new Godot.Collections.Dictionary
        {
        ["name"] = Name,
        ["owner"] = Owner,
        ["id"] = (int)Stack.Id,
        ["count"] = Stack.Count,
        ["data"] = Stack.Data ?? "",
        ["rot"] = Rotation,
        ["vel"] = Velocity,
        ["spin"] = Spin,
        ["settled"] = Settled,
        ["token"] = Token,
        };
        Position.Write(d);
        return d;
    }

    public static DropState FromDict(Godot.Collections.Dictionary d)
    {
        string data = d["data"].AsString();
        return new DropState(
            d["name"].AsString(),
            d["owner"].AsInt64(),
            new ItemStack((ItemId)d["id"].AsInt32(), d["count"].AsInt32(), data.Length == 0 ? null : data),
            GlobalPos.Read(d),
            d["rot"].AsVector3(),
            d["vel"].AsVector3(),
            d["spin"].AsVector3(),
            d["settled"].AsBool(),
            d.TryGetValue("token", out var token) ? token.AsInt32() : 0);
    }
}

/// <summary>
/// An item stack lying in the world: dropped or thrown by a player, tumbling to rest on their
/// machine (client authority, like <see cref="RadioBody"/>), then frozen where it landed on every
/// peer until someone picks it up. The stack itself never changes while it lies there, so it only
/// travels with the spawn; the <c>Sync</c> synchronizer carries the fall.
///
/// <para>
/// Nobody waits for the network to see it fly. The dropper throws a local <see cref="Proxy"/> at
/// once, which the server's spawn takes over when it arrives (<see cref="TakeOver"/>); every other
/// peer starts the spawned body falling with the spawn's velocity itself and only hands it to the
/// synchronizer when the dropper's first update comes in.
/// </para>
/// </summary>
public partial class DroppedItem : RigidBody3D, IOriginShiftAware
{
    public const string Group = "dropped_items";

    [Export] public bool Settled { get; set; }

    /// <summary>The dropper's own stand-in until the server's spawn arrives: simulated here, never synced, never picked up.</summary>
    public bool Proxy { get; private set; }

    /// <summary>The dropper's number for this drop, matching its spawn to its <see cref="Proxy"/>; 0 when none.</summary>
    public int Token => _initial.Token;

    public ItemStack Stack { get; private set; }

    /// <summary>Peer that simulated the fall; 0 for the server.</summary>
    public long Owner { get; private set; }

    /// <summary>Seconds with no player within range — for despawning.</summary>
    public double LonelyFor { get; set; }

    /// <summary>The drawn item, for the pointing highlight; null on a dedicated server or headless.</summary>
    public MeshInstance3D? Visual { get; private set; }

    /// <summary>Its label in prompts: "Energy bar ×4".</summary>
    public string Label => (ItemDefs.Get(Stack.Id)?.Name ?? "item") + (Stack.Count > 1 ? $" ×{Stack.Count}" : "");

    private const double SettleAfter = 10, RestFor = 0.6;
    private DropState _initial;
    private WorldOrigin _origin = null!;
    /// <summary>The position on the wire (#185): published by whoever simulates the fall, applied everywhere else.</summary>
    private NetPlace _place = null!;
    private MultiplayerSynchronizer? _sync;
    private double _age, _restTime;
    private bool _predicting;
    private ImpactFx? _impact;
    private static int _localCounter;
    /// <summary>The drawn mesh's box and the collider's size, for the floating pose.</summary>
    private Aabb _box;
    private Vector3 _size;
    private float _floatScale = 1f, _floatPhase;

    /// <summary>At rest and drawn floating (<see cref="DropFloat"/>).</summary>
    internal bool Floating { get; private set; }

    public static DroppedItem Create(DropState state, WorldOrigin origin, bool proxy = false)
    {
        var item = new DroppedItem
        {
            _origin = origin,
            Name = string.IsNullOrEmpty(state.Name) ? $"drop_local_{Interlocked.Increment(ref _localCounter)}" : state.Name,
            _initial = state,
            Owner = state.Owner,
            Stack = state.Stack,
            Settled = state.Settled,
            Proxy = proxy,
        };
        item.SetMultiplayerAuthority(state.Owner > 0 ? (int)state.Owner : 1);
        return item;
    }

    public override void _Ready()
    {
        AddToGroup(Group);
        CollisionMask |= World.TreeColliders.Layer;
        var s = _initial;
        Position = _origin.ToWorld(s.Position);
        AddChild(_place = new NetPlace(_origin, s.Position));
        Rotation = s.Rotation;

        // the collider is the drawn item's box (thin cards and bars get a minimum, or they sink)
        var mesh = ItemDefs.HandMesh(s.Stack.Id);
        var box = mesh?.GetAabb() ?? new Aabb(new Vector3(-0.06f, -0.06f, -0.06f), new Vector3(0.12f, 0.12f, 0.12f));
        var size = new Vector3(Mathf.Max(box.Size.X, 0.05f), Mathf.Max(box.Size.Y, 0.05f), Mathf.Max(box.Size.Z, 0.05f));
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = box.GetCenter() });
        CenterOfMassMode = CenterOfMassModeEnum.Custom;
        CenterOfMass = box.GetCenter();
        Mass = Mathf.Clamp(size.X * size.Y * size.Z * 600f, 0.1f, 8f);
        PhysicsMaterialOverride = new PhysicsMaterial { Friction = 0.9f, Bounce = 0.25f };
        // a full-strength throw covers a stone's whole length in a physics step: no tunnelling
        ContinuousCd = true;

        if (Proxy)
        {
            Simulate(s);
            AddVisual(mesh, size);
            return;
        }

        var fall = new SceneReplicationConfig();
        foreach (var prop in NetPlace.Properties.Append(".:rotation").Append(".:Settled")) fall.AddProperty(prop);
        fall.PropertySetReplicationMode(".:Settled", SceneReplicationConfig.ReplicationMode.OnChange);
        _sync = new MultiplayerSynchronizer
        {
            Name = "Sync", RootPath = new NodePath(".."), ReplicationConfig = fall,
            ReplicationInterval = s.Settled ? 2f : 0.05f,
        };
        _sync.SetMultiplayerAuthority(GetMultiplayerAuthority());
        AddChild(_sync);

        if (s.Settled || NetworkManager.DedicatedServer)
        {
            FreezeMode = IsMultiplayerAuthority() ? FreezeModeEnum.Static : FreezeModeEnum.Kinematic;
            Freeze = true;
            if (IsMultiplayerAuthority()) Settled = true;
            SetPhysicsProcess(false);
        }
        else if (IsMultiplayerAuthority()) Simulate(s);
        else
        {
            // someone else's throw: fall the same way here until their first update says where it is
            Simulate(s);
            _predicting = true;
            _sync.Synchronized += StopPredicting;
        }
        AddVisual(mesh, size);
    }

    private void Simulate(DropState s)
    {
        LinearVelocity = s.Velocity;
        AngularVelocity = s.Spin;
        _lastPos = _origin.ToWorld(s.Position);
        _lastVel = s.Velocity;
    }

    /// <summary>The origin moved (#185): the last step of the flight, kept for hitting someone on the way, moves with it.</summary>
    public void OnOriginShifted(OriginShift shift)
    {
        _lastPos = shift.Point(_lastPos);
        _lastVel = shift.Direction(_lastVel);
    }

    /// <summary>The flight so far, for hitting someone on the way (#261): last step's position and speed, and whether it already has.</summary>
    private Vector3 _lastPos, _lastVel;
    private bool _bonked;

    private void StopPredicting()
    {
        if (!_predicting) return;
        _predicting = false;
        FreezeMode = FreezeModeEnum.Kinematic;
        Freeze = true;
        SetPhysicsProcess(false);
    }

    private void AddVisual(ArrayMesh? mesh, Vector3 size)
    {
        if (DisplayServer.GetName() != "headless" && !NetworkManager.DedicatedServer && mesh != null)
        {
            Visual = new MeshInstance3D
            {
                Name = "Visual", Mesh = mesh,
                MaterialOverride = ItemDefs.HandMaterial(Stack.Id, Stack.Data) ?? ItemDefs.Material,
            };
            AddChild(Visual);
            _impact = new ImpactFx { Name = "Impact", Size = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z)) };
            AddChild(_impact);
            _box = mesh.GetAabb();
            _size = size;
            _floatScale = DropFloat.ScaleFor(_box.Size);
            _floatPhase = (Name.ToString().GetHashCode() & 0xffff) / 65536f * Mathf.Tau;
            DropFloat.Add(this);
        }
    }

    public override void _ExitTree()
    {
        if (Visual != null) DropFloat.Remove(this);
    }

    /// <summary>
    /// Draws the item hovering upright over where it lies, blown up to a readable size, turned
    /// <paramref name="time"/> into its spin and bob. The first call also limits its draw distance.
    /// </summary>
    internal void PoseFloat(float time)
    {
        if (Visual == null) return;
        if (!Floating)
        {
            Floating = true;
            Visual.VisibilityRangeEnd = DropFloat.DrawRange;
        }
        var b = GlobalBasis.Orthonormalized();
        // the resting collider's centre and its height above the ground, both in world axes
        var rest = b * _box.GetCenter();
        var half = _size * 0.5f;
        float restHalfY = Mathf.Abs(b.X.Y) * half.X + Mathf.Abs(b.Y.Y) * half.Y + Mathf.Abs(b.Z.Y) * half.Z;
        float s = _floatScale;
        float bob = (1f + Mathf.Sin(time * DropFloat.BobSpeed + _floatPhase)) * 0.5f * DropFloat.BobHeight;
        var center = new Vector3(rest.X, rest.Y - restHalfY + DropFloat.Hover + bob + _box.Size.Y * s * 0.5f, rest.Z);
        var spin = new Basis(Vector3.Up, time * DropFloat.SpinSpeed + _floatPhase).Scaled(Vector3.One * s);
        // near music (#734) it hops on every beat and squashes as it lands: the drawing only
        if (BeatField.Count > 0 && BeatField.At(GlobalPosition, out var groove) is var reach and > 0.01f && groove.Beating)
        {
            float k = reach * groove.BounceScale;
            float hop = Mathf.Sin(Mathf.Pi * Mathf.Clamp(groove.Phase / 0.6f, 0f, 1f));
            float squash = Mathf.Exp(-groove.Phase * 8f);
            center.Y += 0.12f * k * hop;
            spin = spin.Scaled(new Vector3(1f + 0.12f * k * squash, 1f - 0.16f * k * squash, 1f + 0.12f * k * squash));
        }
        // from world axes back into the body's: it lies however it landed
        var inv = b.Inverse();
        Visual.Transform = new Transform3D(inv * spin, inv * (center - spin * _box.GetCenter()));
    }

    /// <summary>
    /// The server's spawn of this player's drop has arrived: it carries on from where the
    /// <paramref name="proxy"/> got to (in mid-air, or already at rest) and the proxy goes.
    /// </summary>
    public void TakeOver(DroppedItem proxy)
    {
        GlobalTransform = proxy.GlobalTransform;
        _age = proxy._age;
        _bonked = proxy._bonked;
        _lastPos = proxy._lastPos;
        _lastVel = proxy._lastVel;
        if (proxy.Settled) Settle();
        else
        {
            LinearVelocity = proxy.LinearVelocity;
            AngularVelocity = proxy.AngularVelocity;
        }
        _impact?.Rebase();
        proxy.QueueFree();
    }

    private void Settle()
    {
        Settled = true;
        Freeze = true;
        FreezeMode = FreezeModeEnum.Static;
        if (_sync != null) _sync.ReplicationInterval = 2f;
        SetPhysicsProcess(false);
    }

    /// <summary>Outdoors and under the terrain by more than this: fallen through, put back on top.</summary>
    private const float SunkDepth = 1f;

    /// <summary>The tumble, until it comes to rest: the authority's and the proxy's for real, everyone else's as a guess.</summary>
    public override void _PhysicsProcess(double delta)
    {
        if (Settled) return;
        _age += delta;
        // through the ground (a terrain tile not there yet, a corner case of the solver): back on top
        var p = GlobalPosition;
        if (p.Y > -1000f && DroppedItems.GroundHeight?.Invoke(p) is float ground && p.Y < ground - SunkDepth)
        {
            GlobalPosition = p with { Y = ground + 0.3f };
            LinearVelocity = Vector3.Zero;
        }
        if (_predicting)
        {
            if (_age > SettleAfter) StopPredicting();
            return;
        }
        if (!Proxy) _place.Publish(Position);
        // simulated here (the thrower's proxy or body): someone in the way takes it (#261)
        if (!_bonked && _age < 4) _bonked = ThrowHits.Step(this, _lastPos, GlobalPosition, _lastVel, Stack.Id);
        _lastPos = GlobalPosition;
        _lastVel = LinearVelocity;
        _restTime = LinearVelocity.LengthSquared() < 0.05f * 0.05f && AngularVelocity.LengthSquared() < 0.2f ? _restTime + delta : 0;
        if (Sleeping || _restTime > RestFor || _age > SettleAfter || Position.Y < Interiors.InteriorManager.LostBelowY) Settle();
    }

    /// <summary>The state to respawn it from: where it lies now, what it is.</summary>
    public DropState Capture() => new(Name, Owner, Stack, _place.Global, Rotation, Vector3.Zero, Vector3.Zero, Settled);
}
