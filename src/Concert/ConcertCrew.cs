using Godot;
using UnitSport.Core;
using UnitSport.Styles;
using UnitSport.Terrain;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>Who works a concert's show (#985): the three at the front-of-house desk, and a camera's operator.</summary>
public enum CrewRole : byte { Sound, Lighting, Video, Camera }

/// <summary>Where a crew member stands at rest, stadium frame: their feet and the way they face (yaw, −Z forward); a camera operator's rig.</summary>
public readonly record struct CrewPost(CrewRole Role, Vector3 Feet, float Yaw, int Rig = -1);

/// <summary>
/// The concert's crew (#985): a sound engineer, a lighting operator and a video director at the
/// front-of-house desk, and an operator at each crewed camera (<see cref="CameraRig.Crewed"/>: the
/// dollies, the jib's base, the Steadicam, the platforms' tripods). Low-poly figures of a few boxes in
/// the stage's own prop material, each a root, a head and two arms: a frame writes those transforms in
/// place. They nod on the beat as hard as the show goes, keep their hands on the desk, look up at the
/// stage now and then; the lighting operator hits the buttons on a drop; a camera operator stands behind
/// the lens as it moves and tilts their head with it. Scenery, not players: no collision, no input.
/// </summary>
public partial class ConcertCrew : Node3D
{
    private readonly StagePlan _plan;
    private readonly LightDesk _desk;
    private readonly ConcertCameras _cameras;

    private sealed class Body
    {
        public required Node3D Root, Head, ArmL, ArmR;
        public CrewPost Post;
        public uint Seed;
        /// <summary>0 eyes on the desk, 1 on the stage: eased.</summary>
        public float Look;
    }

    private readonly List<Body> _bodies = new();
    private float _t;

    /// <summary>A stadium's protection floor's top: what a jib operator stands on (the rigs are placed on the pitch).</summary>
    public float FloorTop { get; init; }

    public ConcertCrew(StagePlan plan, LightDesk desk, ConcertCameras cameras)
    {
        _plan = plan;
        _desk = desk;
        _cameras = cameras;
        Name = "Crew";
    }

    public ConcertCrew() : this(null!, null!, null!) { }

    /// <summary>
    /// Where the crew stands at rest (pure; the check reads it): the three at the desk on the riser,
    /// facing the stage, the console between them and it (the middle left for a player at the desk's
    /// screen); an operator behind each crewed camera.
    /// </summary>
    public static List<CrewPost> Posts(StagePlan plan, IReadOnlyList<CameraRig> rigs)
    {
        var list = new List<CrewPost>();
        // the desk's frame: across the riser, and towards the stage
        var across = plan.StagePoint(1, 0, 0) - plan.StagePoint(0, 0, 0);
        var toStage = plan.StagePoint(0, 0, -1) - plan.StagePoint(0, 0, 0);
        float yaw = Mathf.Atan2(-toStage.X, -toStage.Z);
        // Desk is the screen's point, 0.6 m behind the console: they stand a step nearer it
        var at = plan.Desk + toStage * 0.3f;
        list.Add(new CrewPost(CrewRole.Lighting, at - across * 1.8f, yaw));
        list.Add(new CrewPost(CrewRole.Sound, at + across * 0.9f, yaw));
        list.Add(new CrewPost(CrewRole.Video, at + across * 2.0f, yaw + 0.25f));
        for (int i = 0; i < rigs.Count; i++)
            if (rigs[i].Crewed > 0 && !rigs[i].Hidden)
                list.Add(new CrewPost(CrewRole.Camera, rigs[i].A - Vector3.Up * rigs[i].Crewed, 0f, i));
        return list;
    }

    public override void _Ready()
    {
        var material = StyleKit.Material(MaterialRole.Prop);
        var rigs = _cameras.Rigs.Count > 0 ? _cameras.Rigs : CameraRig.For(_plan);
        uint seed = (uint)_plan.Event.Seed * 2654435761u;
        foreach (var post in Posts(_plan, rigs))
        {
            seed = seed * 1664525u + 1013904223u;
            var b = Build(post.Role, seed, material);
            b.Post = post;
            b.Root.Transform = new Transform3D(new Basis(Vector3.Up, post.Yaw), post.Feet);
            AddChild(b.Root);
            _bodies.Add(b);
        }
    }

    // ---- every frame ---------------------------------------------------------------------------------

    private static float Energy(ShowSection s) => s switch
    {
        ShowSection.Calm => 0.3f, ShowSection.Groove => 0.65f, ShowSection.Chorus => 0.9f, ShowSection.Peak => 1f, _ => 0.1f,
    };

    private static uint Hash(int a, uint seed)
    {
        uint h = (uint)a * 2654435761u ^ seed;
        h ^= h >> 15;
        return h * 2246822519u;
    }

    /// <summary>The arms' rest on the desk: forward and down to its top (radians about the shoulder).</summary>
    private const float DeskArms = 0.8f, CameraArms = 1.25f;

    /// <summary>One frame (only while someone is near): the nod, the looks, the hands, the operators behind their cameras.</summary>
    public void Step(bool live, float dt)
    {
        _t += dt;
        float beat = _desk.Beat, ph = beat - MathF.Floor(beat);
        int bar = (int)MathF.Floor(beat / 4f);
        var section = _desk.Section;
        float energy = live ? Energy(section) : 0f;
        // the nod: down on the beat, back up through it
        float pulse = energy * MathF.Exp(-ph * 6f);
        // the lighting operator's hit: arms up and down onto the buttons on a drop; on the beat in a build's end or a peak
        float hit = 0f;
        if (live && _desk.Music is { SinceDrop: < 0.5f } m) hit = MathF.Sin(MathF.PI * m.SinceDrop / 0.5f);
        else if (live && (_desk.Strobing || section == ShowSection.Peak)) hit = 0.45f * MathF.Max(0f, MathF.Sin(MathF.PI * MathF.Min(1f, ph * 2.5f)));
        var rigs = _cameras.Rigs;
        foreach (var b in _bodies)
        {
            var post = b.Post;
            if (post.Role == CrewRole.Camera)
            {
                if (post.Rig >= rigs.Count) continue;
                Operate(b, rigs[post.Rig], pulse);
                continue;
            }
            // eyes on the stage for a bar now and then (the lighting operator at every chorus's start)
            uint h = Hash(bar, b.Seed);
            bool up = live && (h % 3 == 0 || post.Role == CrewRole.Lighting && section == ShowSection.Chorus && _desk.SectionBar < 1);
            b.Look = Mathf.MoveToward(b.Look, up ? 1f : 0f, dt * 2.5f);
            float headYaw = post.Role == CrewRole.Video ? ((h >> 8) % 2 == 0 ? 0.35f : -0.45f) * b.Look : 0.08f * MathF.Sin(_t * 0.4f + b.Seed % 7);
            b.Head.Transform = new Transform3D(Basis.FromEuler(new Vector3(Mathf.Lerp(-0.5f, 0.03f, b.Look) - 0.18f * pulse, headYaw, 0)), b.Head.Position);
            b.Root.Transform = new Transform3D(new Basis(Vector3.Up, post.Yaw), post.Feet - new Vector3(0, 0.025f * pulse, 0));
            float l = DeskArms, r = DeskArms;
            switch (post.Role)
            {
                case CrewRole.Sound:   // riding the faders
                    r += 0.1f * MathF.Sin(_t * 0.9f + b.Seed % 5) * (live ? 1f : 0.2f);
                    l += 0.05f * MathF.Sin(_t * 0.6f + 1f);
                    break;
                case CrewRole.Lighting:
                    l += 1.1f * hit;
                    r += 1.1f * hit;
                    break;
                case CrewRole.Video:   // a cut on the bar
                    r += 0.25f * MathF.Max(0f, 1f - (beat / 4f - bar) * 6f) * energy;
                    break;
            }
            b.ArmL.Transform = new Transform3D(new Basis(Vector3.Right, l), b.ArmL.Position);
            b.ArmR.Transform = new Transform3D(new Basis(Vector3.Right, r), b.ArmR.Position);
        }
    }

    /// <summary>A camera operator behind the lens: a dolly's or a platform's at its feet's height, a jib's at the base beside the arm.</summary>
    private void Operate(Body b, CameraRig rig, float pulse)
    {
        var lens = rig.Position;
        Vector3 feet;
        float yaw;
        if (rig.Kind == RigKind.Jib)
        {
            // at the base, beside the arm, facing where it swings
            var d = (lens - rig.A) with { Y = 0 };
            var dir = d.LengthSquared() > 1e-4f ? d.Normalized() : Vector3.Forward;
            yaw = Mathf.Atan2(-dir.X, -dir.Z);
            feet = rig.A + new Vector3(dir.Z, 0, -dir.X) * 0.75f - dir * 0.3f + Vector3.Up * FloorTop;
        }
        else
        {
            yaw = rig.Yaw;
            var fwd = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
            feet = lens - fwd * 0.6f - Vector3.Up * rig.Crewed;
        }
        b.Root.Transform = new Transform3D(new Basis(Vector3.Up, yaw), feet - new Vector3(0, 0.02f * pulse, 0));
        b.Head.Transform = new Transform3D(Basis.FromEuler(new Vector3(Mathf.Clamp(rig.Pitch * 0.6f, -0.5f, 0.4f), 0, 0)), b.Head.Position);
    }

    // ---- the figure ----------------------------------------------------------------------------------

    private static readonly Color[] Skins = { new(0.86f, 0.68f, 0.55f), new(0.72f, 0.53f, 0.4f), new(0.5f, 0.35f, 0.25f), new(0.93f, 0.78f, 0.66f) };
    private static readonly Color[] Hairs = { new(0.12f, 0.09f, 0.07f), new(0.35f, 0.24f, 0.14f), new(0.7f, 0.6f, 0.4f), new(0.25f, 0.25f, 0.27f) };
    private static readonly Color Black = new(0.06f, 0.06f, 0.07f), Trousers = new(0.1f, 0.1f, 0.12f), Hoodie = new(0.24f, 0.25f, 0.28f),
        Pass = new(0.95f, 0.75f, 0.15f), Cans = new(0.03f, 0.03f, 0.03f);

    /// <summary>A part's mesh: boxes (min, max) in the part's frame, the stage's vertex colours.</summary>
    private static MeshInstance3D Part(Node3D parent, string name, Material material, params (Vector3 Min, Vector3 Max, Color Color)[] boxes)
    {
        var g = new StadiumBuilder.Geo(mesh: true, collision: false);
        foreach (var (min, max, col) in boxes)
            g.Box((min + max) / 2, max - min, Basis.Identity, col.SrgbToLinear() with { A = 0f }, false);
        var mi = new MeshInstance3D
        {
            Name = name, Mesh = ChunkNode.ToPropMesh(g.Data()!, material), VisibilityRangeEnd = 300f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(mi);
        return mi;
    }

    /// <summary>
    /// A crew figure, feet at the origin, facing −Z: legs, a black crew shirt (a video director's grey
    /// hoodie) with a pass on it, a head on a neck pivot (headphones on the sound engineer), two arms on
    /// shoulder pivots. Its parts overlap or keep more than a centimetre apart (zfight); its soles a hair
    /// up off whatever it stands on.
    /// </summary>
    private static Body Build(CrewRole role, uint seed, Material material)
    {
        var skin = Skins[seed % (uint)Skins.Length];
        var hair = Hairs[(seed >> 4) % (uint)Hairs.Length];
        var shirt = role == CrewRole.Video ? Hoodie : Black;
        var root = new Node3D { Name = role.ToString() };
        Part(root, "Body", material,
            (new Vector3(-0.16f, 0.012f, -0.1f), new Vector3(0.16f, 0.86f, 0.1f), Trousers),
            (new Vector3(-0.21f, 0.84f, -0.12f), new Vector3(0.21f, 1.42f, 0.12f), shirt),
            (new Vector3(-0.12f, 1.18f, -0.14f), new Vector3(-0.04f, 1.28f, -0.11f), Pass));
        var head = new Node3D { Name = "Head", Position = new Vector3(0, 1.44f, 0) };
        root.AddChild(head);
        var hd = new List<(Vector3, Vector3, Color)>
        {
            (new Vector3(-0.1f, 0.02f, -0.11f), new Vector3(0.1f, 0.26f, 0.11f), skin),
            (new Vector3(-0.112f, 0.2f, -0.122f), new Vector3(0.112f, 0.29f, 0.122f), hair),
        };
        if (role == CrewRole.Sound)
        {
            hd.Add((new Vector3(-0.135f, 0.07f, -0.05f), new Vector3(-0.09f, 0.19f, 0.05f), Cans));
            hd.Add((new Vector3(0.09f, 0.07f, -0.05f), new Vector3(0.135f, 0.19f, 0.05f), Cans));
        }
        Part(head, "Face", material, hd.ToArray());
        Node3D Arm(string name, float x)
        {
            var pivot = new Node3D { Name = name, Position = new Vector3(x, 1.38f, 0) };
            root.AddChild(pivot);
            Part(pivot, name + "Mesh", material,
                (new Vector3(-0.055f, -0.5f, -0.055f), new Vector3(0.055f, 0.04f, 0.055f), shirt),
                (new Vector3(-0.045f, -0.62f, -0.045f), new Vector3(0.045f, -0.48f, 0.045f), skin));
            return pivot;
        }
        var armL = Arm("ArmL", -0.28f);
        var armR = Arm("ArmR", 0.28f);
        float rest = role == CrewRole.Camera ? CameraArms : DeskArms;
        armL.Transform = new Transform3D(new Basis(Vector3.Right, rest), armL.Position);
        armR.Transform = new Transform3D(new Basis(Vector3.Right, rest), armR.Position);
        head.Transform = new Transform3D(Basis.FromEuler(new Vector3(role == CrewRole.Camera ? 0f : -0.4f, 0, 0)), head.Position);
        return new Body { Root = root, Head = head, ArmL = armL, ArmR = armR, Seed = seed };
    }

    /// <summary>The crew at rest, one of each role.</summary>
    [Showcase("Concert", "Crew")]
    private static IEnumerable<(string, Func<Node3D>)> Showcase()
    {
        foreach (var role in Enum.GetValues<CrewRole>())
        {
            var r = role;
            yield return (r switch
            {
                CrewRole.Sound => "sound engineer", CrewRole.Lighting => "lighting operator", CrewRole.Video => "video director", _ => "camera operator",
            }, () => Build(r, (uint)r * 977u + 3u, StyleKit.Material(MaterialRole.Prop)).Root);
        }
    }
}
