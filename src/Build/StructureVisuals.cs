using Godot;
using UnitSport.Items;

namespace UnitSport.Build;

/// <summary>
/// What a client draws of <see cref="Structures"/>: a node per structure at its LV95 origin, under it
/// a <see cref="StaticBody3D"/> per piece (its cached mesh, its colliders, tagged with the structure
/// and slot so a shot or the build tool can tell what it hit), legs down to the terrain under
/// grounded pieces, a grow-in when a piece goes up live, a darker tint as it takes damage, and a
/// burst of debris when it breaks or falls. A dedicated server builds none of this.
/// </summary>
public partial class StructureVisuals : Node3D, Core.IOriginContainer
{
    private readonly Structures _owner;
    private readonly Dictionary<long, Node3D> _roots = new();
    private readonly Dictionary<(long, Slot), StaticBody3D> _pieces = new();
    /// <summary>Grounded pieces whose legs still wait for the terrain under them to load.</summary>
    private readonly HashSet<(long, Slot)> _legless = new();
    private double _sinceLegs;
    private static readonly StandardMaterial3D?[] Worn = new StandardMaterial3D?[4];
    private static readonly Random Rng = new();

    public StructureVisuals(Structures owner) => _owner = owner;
    public StructureVisuals() : this(null!) { }

    public void AddStructure(Structure s)
    {
        var root = new Node3D { Name = $"S{s.Id}" };
        AddChild(root);
        root.GlobalTransform = s.WorldTransform(_owner.Origin);
        _roots[s.Id] = root;
    }

    public void RemoveStructure(Structure s)
    {
        if (_roots.Remove(s.Id, out var root)) root.QueueFree();
        foreach (var key in _pieces.Keys.Where(k => k.Item1 == s.Id).ToList()) _pieces.Remove(key);
        _legless.RemoveWhere(k => k.Item1 == s.Id);
    }

    public void AddPiece(Structure s, Placed p, bool live)
    {
        if (!_roots.TryGetValue(s.Id, out var root)) return;
        var key = (s.Id, p.Piece.Slot);
        if (_pieces.Remove(key, out var old)) old.QueueFree();

        var body = new StaticBody3D { Name = $"{p.Piece.Kind}_{p.Piece.Slot.X}_{p.Piece.Slot.Y}_{p.Piece.Slot.Z}_{p.Piece.Slot.Side}" };
        body.SetMeta(Structures.StructureMeta, s.Id);
        body.SetMeta(Structures.SlotMeta, new[] { p.Piece.Slot.X, p.Piece.Slot.Y, p.Piece.Slot.Z, (int)p.Piece.Slot.Class, p.Piece.Slot.Side });
        body.Transform = Structures.LocalTransform(p.Piece);
        var mesh = new MeshInstance3D { Name = "Mesh", Mesh = StructureMeshes.Mesh(p.Piece.Kind, p.Piece.Material), MaterialOverride = ItemDefs.Material };
        body.AddChild(mesh);
        foreach (var (shape, at) in StructureMeshes.Colliders(p.Piece.Kind, p.Piece.Material))
            body.AddChild(new CollisionShape3D { Shape = shape, Transform = at });
        root.AddChild(body);
        _pieces[key] = body;
        if (p.Piece.Grounded && !Legs(s, p.Piece, body)) _legless.Add(key);
        Damaged(s, p);

        if (live)
        {
            // it grows up out of its foot over the material's build time, as its strength does
            float seconds = BuildGrid.Spec(p.Piece.Material).Seconds;
            mesh.Scale = new Vector3(1, 0.05f, 1);
            var tween = mesh.CreateTween();
            tween.TweenProperty(mesh, "scale", Vector3.One, seconds).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            Sound(body.GlobalPosition, Audio.SfxSynth.TickBank.Pick(Rng).Stream, 0.55f, -2f);
        }
    }

    public void RemovePiece(Structure s, Placed p, bool broken)
    {
        if (!_pieces.Remove((s.Id, p.Piece.Slot), out var body)) return;
        _legless.Remove((s.Id, p.Piece.Slot));
        if (broken && IsInstanceValid(body))
        {
            var at = body.GlobalTransform * new Vector3(0, BuildGrid.Storey * 0.4f, 0);
            Debris(at, p.Piece.Material);
            Sound(at, Audio.SfxSynth.ImpactBank.Pick(Rng).Stream, p.Piece.Material == BuildMaterial.Metal ? 1.3f : 0.7f, 2f);
        }
        body.QueueFree();
    }

    /// <summary>Darkens a piece as it loses strength: four steps, shared materials.</summary>
    public void Damaged(Structure s, Placed p)
    {
        if (!_pieces.TryGetValue((s.Id, p.Piece.Slot), out var body) || body.GetNodeOrNull<MeshInstance3D>("Mesh") is not { } mesh) return;
        float max = BuildGrid.MaxHp(p.Piece.Kind, p.Piece.Material);
        int step = Math.Clamp((int)(p.Damage / max * 4f), 0, 3);
        mesh.MaterialOverride = step == 0 ? ItemDefs.Material : WornMaterial(step);
    }

    private static StandardMaterial3D WornMaterial(int step)
    {
        if (Worn[step] is { } m) return m;
        var mat = (StandardMaterial3D)ItemDefs.Material.Duplicate();
        float k = 1f - 0.18f * step;
        mat.AlbedoColor = new Color(k, k, k);
        return Worn[step] = mat;
    }

    public override void _Process(double delta)
    {
        // terrain streams in after a structure can be drawn: retry the missing legs now and then
        if (_legless.Count == 0) return;
        _sinceLegs += delta;
        if (_sinceLegs < 2) return;
        _sinceLegs = 0;
        foreach (var key in _legless.ToList())
            if (_owner.All.TryGetValue(key.Item1, out var s) && s.Pieces.TryGetValue(key.Item2, out var p)
                && _pieces.TryGetValue(key, out var body) && Legs(s, p.Piece, body))
                _legless.Remove(key);
    }

    /// <summary>Puts legs under a grounded piece down to the terrain. False if the terrain there is not loaded yet.</summary>
    private bool Legs(Structure s, Piece p, StaticBody3D body)
    {
        if (_owner.GroundAt == null) return false;
        var feet = StructureMeshes.Feet(p.Kind);
        var heights = new float[feet.Length];
        for (int i = 0; i < feet.Length; i++)
        {
            if (_owner.GroundAt(body.GlobalTransform * feet[i]) is not { } h) return false;
            heights[i] = h;
        }
        var stilt = StructureMeshes.Stilt(p.Material == BuildMaterial.Sandbag ? BuildMaterial.Wood : p.Material);
        for (int i = 0; i < feet.Length; i++)
        {
            var foot = body.GlobalTransform * feet[i];
            float gap = foot.Y - heights[i];
            if (gap < 0.05f) continue;
            var leg = new MeshInstance3D { Name = $"Leg{i}", Mesh = stilt, MaterialOverride = ItemDefs.Material };
            body.AddChild(leg);
            leg.Position = feet[i];
            leg.Scale = new Vector3(1, gap + 0.2f, 1);
            // a leg holds you up as much as it looks like it does
            body.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = new Vector3(0.2f, gap, 0.2f) },
                Position = feet[i] - new Vector3(0, gap / 2, 0),
            });
        }
        return true;
    }

    private void Debris(Vector3 at, BuildMaterial m)
    {
        var colour = m switch
        {
            BuildMaterial.Stone => new Color(0.58f, 0.58f, 0.54f),
            BuildMaterial.Metal => new Color(0.46f, 0.50f, 0.54f),
            BuildMaterial.Sandbag => new Color(0.72f, 0.64f, 0.44f),
            _ => new Color(0.60f, 0.42f, 0.24f),
        };
        var burst = new CpuParticles3D
        {
            Emitting = true, OneShot = true, Amount = 22, Lifetime = 1.6f, Explosiveness = 1f,
            Direction = Vector3.Up, Spread = 80f, InitialVelocityMin = 2f, InitialVelocityMax = 6f, Gravity = new Vector3(0, -9.8f, 0),
            AngularVelocityMin = -360f, AngularVelocityMax = 360f, EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(1f, 0.8f, 0.3f),
            Mesh = new BoxMesh { Size = m == BuildMaterial.Wood ? new Vector3(0.4f, 0.04f, 0.1f) : new Vector3(0.18f, 0.14f, 0.16f),
                Material = new StandardMaterial3D { AlbedoColor = colour } },
            TopLevel = true,
        };
        AddChild(burst);
        burst.GlobalPosition = at;
        GetTree().CreateTimer(2.2).Timeout += burst.QueueFree;
    }

    private void Sound(Vector3 at, AudioStream stream, float pitch, float db)
    {
        var s = new AudioStreamPlayer3D { Stream = stream, PitchScale = pitch, VolumeDb = db, UnitSize = 10f, MaxDistance = 200f, Bus = Audio.SfxBus.Name, TopLevel = true };
        AddChild(s);
        s.GlobalPosition = at;
        s.Finished += s.QueueFree;
        s.Play();
    }
}
