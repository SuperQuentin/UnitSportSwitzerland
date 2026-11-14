using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// A colony's airlocks and its air (#1941, docs/plans/moon-surface.md section 6). Each airlock has an outer and an
/// inner door, two sliding leaves each, that are never both open: E at a shut door (pad Y, VR: reach out and grip, all
/// through <c>FootPlayer.TryInteract</c>) cycles it: the open door slides shut, the chamber's lamp turns amber and it
/// hisses for <see cref="PumpSeconds"/>, then the door asked for slides open. No oxygen limit (the user's choice).
///
/// <para>
/// <b>The air</b>: every module's inside, every tube and every airlock chamber whose outer door is shut is pressurised
/// (<see cref="Pressurised"/>): the suit's helmet comes off there (<see cref="MoonSuit"/>).
/// </para>
///
/// <para>
/// <b>Offline in this phase</b>: the state is this client's. Online play on the Moon waits for D2 (#1727) and C3b
/// (#1852); then the server owns each airlock's state (the doors' pattern) and a cycle is a request to it, the same
/// <see cref="Request"/> a bot calls.
/// </para>
/// </summary>
public partial class ColonyAirlocks : Node3D
{
    /// <summary>How long the chamber pumps between the doors, seconds.</summary>
    public const float PumpSeconds = 3f;

    /// <summary>How long a door takes to slide, seconds.</summary>
    public const float SlideSeconds = 0.9f;

    /// <summary>How near a door's middle a walker must stand to use it, metres (flat).</summary>
    public const float Reach = 2.2f;

    private static readonly List<ColonyAirlocks> All = new();

    /// <summary>A door: its node in the colony's frame, its two leaves, how open it is.</summary>
    private sealed class Door
    {
        public Node3D Node = null!;
        public Node3D Left = null!, Right = null!;
        public CollisionShape3D Shut = null!;
        public float Open, Target;
    }

    private sealed class Lock
    {
        public ColonyAirlock Plan;
        public Door Outer = null!, Inner = null!;
        public MeshInstance3D Lamp = null!;
        public double Pump = -1;
        public bool WantOuter;
        public Aabb Chamber;
        public Transform3D ChamberAt;
    }

    private readonly List<Lock> _locks = new();
    private readonly List<(Transform3D At, Vector3 Half, bool Round)> _volumes = new();
    private static readonly StringName Albedo = "albedo_color";

    public ColonyAirlocks(MoonColony colony, ColonyPlan plan)
    {
        Name = "Airlocks";
        var pal = ColonyKit.Of(colony.Theme);
        foreach (var a in plan.Airlocks)
        {
            var m = plan.Modules[a.Module];
            var l = new Lock { Plan = a };
            l.Outer = MakeDoor(plan.Doors[a.Outer], pal);
            l.Inner = MakeDoor(plan.Doors[a.Inner], pal);
            // the start: the inner door open, the outer shut, the chamber full
            l.Inner.Open = l.Inner.Target = 1;
            Pose(l.Inner);
            Pose(l.Outer);
            l.Lamp = new MeshInstance3D
            {
                Name = "Lamp", Mesh = new BoxMesh { Size = new Vector3(0.3f, 0.1f, 0.3f) },
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Green },
                Position = new Vector3((float)m.X, ColonyKit.FloorY + ColonyKit.DoorH + 0.9f, (float)m.Z),
            };
            AddChild(l.Lamp);
            l.ChamberAt = new Transform3D(new Basis(Vector3.Up, -(float)m.Yaw), new Vector3((float)m.X, 0, (float)m.Z));
            _locks.Add(l);
        }
        // the pressurised insides, in the colony's frame
        foreach (var m in plan.Modules)
        {
            if (m.Kind == ModuleKind.Airlock) continue;
            var at = new Transform3D(new Basis(Vector3.Up, -(float)m.Yaw), new Vector3((float)m.X, 0, (float)m.Z));
            bool round = m.Kind is ModuleKind.Hub or ModuleKind.Stack or ModuleKind.Dome or ModuleKind.Biodome;
            _volumes.Add((at, new Vector3((float)m.Width / 2 - 0.1f, (float)m.Height + 1, round ? (float)m.Width / 2 - 0.1f : (float)m.Length / 2 + 0.05f), round));
        }
        foreach (var link in plan.Links)
        {
            var a = plan.Doors[link.DoorA];
            var b = plan.Doors[link.DoorB];
            var pa = new Vector3((float)a.X, 0, (float)a.Z);
            var pb = new Vector3((float)b.X, 0, (float)b.Z);
            var d = pb - pa;
            if (d.Length() < 0.1f) continue;
            var at = new Transform3D(new Basis(Vector3.Up, Mathf.Atan2(d.X, d.Z)), (pa + pb) / 2);
            _volumes.Add((at, new Vector3(ColonyKit.TubeR, ColonyKit.TubeY + ColonyKit.TubeR, d.Length() / 2 + 0.3f), false));
        }
    }

    private static readonly Color Green = new(0.3f, 1f, 0.45f), Amber = new(1f, 0.62f, 0.15f);

    public override void _EnterTree() => All.Add(this);

    public override void _ExitTree() => All.Remove(this);

    /// <summary>A door at a plan door: a frame-flush node facing out, two leaves that slide apart, a box that blocks the way while shut.</summary>
    private Door MakeDoor(ColonyDoor d, ColonyKit.Palette pal)
    {
        var node = new Node3D
        {
            Name = "Door",
            Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Atan2((float)Math.Sin(d.Out), (float)-Math.Cos(d.Out)) ),
                new Vector3((float)d.X, ColonyKit.FloorY, (float)d.Z)),
        };
        var mesh = LeafMesh(pal);
        var left = new MeshInstance3D { Name = "Left", Mesh = mesh, MaterialOverride = HumanMeshBuilder.Material() };
        var right = new MeshInstance3D { Name = "Right", Mesh = mesh, MaterialOverride = HumanMeshBuilder.Material() };
        node.AddChild(left);
        node.AddChild(right);
        var body = new StaticBody3D { Name = "Shut" };
        var shut = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(ColonyKit.DoorW, ColonyKit.DoorH, 0.12f) }, Position = new Vector3(0, ColonyKit.DoorH / 2, 0) };
        body.AddChild(shut);
        node.AddChild(body);
        AddChild(node);
        return new Door { Node = node, Left = left, Right = right, Shut = shut };
    }

    private static ArrayMesh? _leaf;

    /// <summary>One sliding leaf: half the doorway, a porthole, hazard stripes at its foot (built once, shared).</summary>
    private static ArrayMesh LeafMesh(ColonyKit.Palette pal)
    {
        if (_leaf != null) return _leaf;
        var s = new MeshScratch();
        float w = ColonyKit.DoorW / 2;
        s.Box(new(0, ColonyKit.DoorH / 2, 0), new(w, ColonyKit.DoorH, 0.06f), new Color("b8bec4") with { A = 0 });
        s.Box(new(0, 1.45f, 0), new(0.28f, 0.28f, 0.08f), new Color("2a3a4a") with { A = 0 });
        for (int k = 0; k < 3; k++) s.Box(new(-w / 2 + (k + 0.5f) * w / 3, 0.12f, 0), new(w / 6, 0.2f, 0.08f), new Color("f2c21b") with { A = 0 });
        _leaf = s.Build();
        return _leaf;
    }

    private static void Pose(Door d)
    {
        float s = d.Open * d.Open * (3 - 2 * d.Open);
        float w = ColonyKit.DoorW / 2;
        d.Left.Position = new Vector3(-w / 2 - s * w * 0.95f, 0, 0);
        d.Right.Position = new Vector3(w / 2 + s * w * 0.95f, 0, 0);
        d.Shut.Disabled = d.Open > 0.7f;
    }

    public override void _Process(double delta)
    {
        float step = (float)delta / SlideSeconds;
        foreach (var l in _locks)
        {
            foreach (var d in new[] { l.Outer, l.Inner })
            {
                // exact: MoveToward lands on the target (an approximate stop left a shut door at 3e-7 and the pump waiting on it)
                if (d.Open == d.Target) continue;
                d.Open = Mathf.MoveToward(d.Open, d.Target, step);
                Pose(d);
            }
            if (l.Pump < 0) continue;
            // the far door shuts first; then the pump; then the door asked for opens
            var shutting = l.WantOuter ? l.Inner : l.Outer;
            var opening = l.WantOuter ? l.Outer : l.Inner;
            if (shutting.Open > 0) continue;
            if (l.Pump == 0) Hiss(l);
            l.Pump += delta;
            if (l.Pump >= PumpSeconds)
            {
                l.Pump = -1;
                opening.Target = 1;
                Sound(opening, open: true);
                Light(l, Green);
            }
        }
    }

    /// <summary>Asks a cycle toward the outside (<paramref name="outward"/>) or the inside: refused while one runs or that door is open already.</summary>
    public bool Request(int index, bool outward)
    {
        var l = _locks[index];
        if (l.Pump >= 0) return false;
        var opening = outward ? l.Outer : l.Inner;
        var shutting = outward ? l.Inner : l.Outer;
        if (opening.Target > 0) return false;
        l.WantOuter = outward;
        l.Pump = 0;
        if (shutting.Target > 0)
        {
            shutting.Target = 0;
            Sound(shutting, open: false);
        }
        Light(l, Amber);
        return true;
    }

    private static void Light(Lock l, Color c) => ((StandardMaterial3D)l.Lamp.MaterialOverride).AlbedoColor = c;

    private static readonly Random SoundRng = new(1941);

    /// <summary>A door sliding: the doors' own latch-and-slide sound (<c>SfxSynth.DoorOpenBank</c>/<c>DoorCloseBank</c>), heard at it.</summary>
    private static void Sound(Door d, bool open)
    {
        var (stream, pitch, db) = (open ? Audio.SfxSynth.DoorOpenBank : Audio.SfxSynth.DoorCloseBank).Pick(SoundRng);
        OneShot(d.Node, stream, pitch * 0.8f, db - 3f);
    }

    /// <summary>The chamber filling or emptying: the existing hiss (<c>AlcoholSounds.Hiss</c>), lower and slower.</summary>
    private static void Hiss(Lock l) => OneShot(l.Lamp, Adventure.Alcohol.AlcoholSounds.Hiss, 0.55f, -2f);

    private static void OneShot(Node3D at, AudioStream stream, float pitch, float db)
    {
        var p = new AudioStreamPlayer3D { Stream = stream, VolumeDb = db, UnitSize = 3f, MaxDistance = 18f, PitchScale = pitch };
        at.AddChild(p);
        p.Finished += p.QueueFree;
        p.Play();
    }

    // ---- the walker's E -------------------------------------------------------------------------------------------

    /// <summary>The shut door nearest a walker within <see cref="Reach"/>, with its airlock and which side it is.</summary>
    private static (ColonyAirlocks Set, int Index, bool Outer, Door Door)? Nearest(Vector3 at)
    {
        (ColonyAirlocks, int, bool, Door)? best = null;
        float bestD = Reach;
        foreach (var set in All)
            for (int i = 0; i < set._locks.Count; i++)
            {
                var l = set._locks[i];
                foreach (var (d, outer) in new[] { (l.Outer, true), (l.Inner, false) })
                {
                    if (d.Target > 0) continue;   // an open door is no target: the shut one is
                    var p = d.Node.GlobalPosition;
                    float dist = new Vector2(p.X - at.X, p.Z - at.Z).Length();
                    if (dist < bestD && Mathf.Abs(p.Y + 1 - at.Y) < 2.5f)
                    {
                        bestD = dist;
                        best = (set, i, outer, d);
                    }
                }
            }
        return best;
    }

    /// <summary>E at an airlock's shut door (pad Y; VR: reach out and grip): cycles it to open that door.</summary>
    public static bool TryUse(Player.FootPlayer p)
    {
        if (Nearest(p.GlobalPosition) is not { } n || n.Door.Target > 0) return false;
        return n.Set.Request(n.Index, n.Outer);
    }

    /// <summary>The prompt at a shut door: "Cycle the airlock", the door outlined; null elsewhere or while it cycles.</summary>
    public static Interaction? InteractionFor(Player.FootPlayer p)
    {
        if (Nearest(p.GlobalPosition) is not { } n || n.Door.Target > 0) return null;
        bool busy = n.Set._locks[n.Index].Pump >= 0;
        return new Interaction(busy ? "Cycling…" : InputHints.Prompt(PlayerInput.InteractMount, n.Outer ? "Cycle the airlock: out" : "Cycle the airlock: in"),
            Outline.Of(n.Door.Node), InBar: busy);
    }

    // ---- for the checks ---------------------------------------------------------------------------------------------

    /// <summary>The airlocks of the colonies in place.</summary>
    public static IReadOnlyList<ColonyAirlocks> Sets => All;

    public int Count => _locks.Count;

    /// <summary>A door's middle on its floor, world space.</summary>
    public Vector3 DoorAt(int index, bool outer) => (outer ? _locks[index].Outer : _locks[index].Inner).Node.GlobalPosition;

    /// <summary>The way out of a door (world, horizontal).</summary>
    public Vector3 DoorOut(int index, bool outer) => (outer ? _locks[index].Outer : _locks[index].Inner).Node.GlobalBasis.Z.Normalized();

    /// <summary>An airlock's state for a check's log.</summary>
    public string State(int index)
    {
        var l = _locks[index];
        return FormattableString.Invariant($"pump {l.Pump:F1} s, outward {l.WantOuter}, targets {l.Outer.Target}/{l.Inner.Target}, processing {CanProcess()}");
    }

    /// <summary>How open a door is, 0..1.</summary>
    public float Opening(int index, bool outer) => (outer ? _locks[index].Outer : _locks[index].Inner).Open;

    // ---- the air ---------------------------------------------------------------------------------------------------

    /// <summary>Whether a world point is inside a pressurised volume of any colony in place.</summary>
    public static bool Pressurised(Vector3 world)
    {
        foreach (var set in All)
        {
            if (!set.IsInsideTree()) continue;
            var local = set.GlobalTransform.AffineInverse() * world;
            foreach (var (at, half, round) in set._volumes)
            {
                var q = at.AffineInverse() * local;
                if (q.Y < 0 || q.Y > half.Y) continue;
                if (round ? q.X * q.X + q.Z * q.Z < half.X * half.X : Mathf.Abs(q.X) < half.X && Mathf.Abs(q.Z) < half.Z) return true;
            }
            // an airlock's chamber holds air while its outer door is shut
            foreach (var l in set._locks)
            {
                if (l.Outer.Open > 0.02f) continue;
                var q = l.ChamberAt.AffineInverse() * local;
                if (Mathf.Abs(q.X) < 2.2f && Mathf.Abs(q.Z) < 2.05f && q.Y > 0 && q.Y < 6) return true;
            }
        }
        return false;
    }
}
