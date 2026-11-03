using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// <c>--skincheck</c> (#1202): a skinned figure looks as the rebuilt one did. For looks and poses
/// across everything a figure does, the pose is built the old way (<see cref="AppendRig"/> at the
/// pose's rig) and the look's skinned mesh is deformed on the CPU by the pose's bones; every
/// skinned vertex is measured to the rebuilt surface. Headless, no scene.
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary>
    /// The surface gap, metres, most of a figure stays within (p99) and nothing passes: what is left
    /// is a round part's facets turned about its own axis, a hat's fixed-size parts on a head the
    /// old builder shrank with its length, a skirt's strap between its rings.
    /// </summary>
    private const float SkinP99 = 0.008f, SkinMax = 0.015f;

    public static int SkinCheck()
    {
        var poses = SkinPoses();
        var looks = SkinLooks();
        // --skincheck <look> <pose>: only those, with the worst vertices printed
        var args = OS.GetCmdlineUserArgs();
        int at0 = Array.IndexOf(args, "--skincheck");
        string? onlyLook = at0 + 1 < args.Length && !args[at0 + 1].StartsWith("--") ? args[at0 + 1] : null;
        string? onlyPose = at0 + 2 < args.Length && !args[at0 + 2].StartsWith("--") ? args[at0 + 2] : null;
        _dump = onlyLook != null;
        int failed = 0, compared = 0;
        float worstMax = 0f, worstP99 = 0f;
        string worst = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (lookName, look, everyPose) in looks)
            foreach (var (poseName, rig) in poses)
            {
                if (onlyLook != null && !lookName.Contains(onlyLook, StringComparison.OrdinalIgnoreCase)) continue;
                if (onlyPose != null && !poseName.Contains(onlyPose, StringComparison.OrdinalIgnoreCase)) continue;
                if (onlyLook == null && !everyPose && !poseName.StartsWith("key", StringComparison.Ordinal)) continue;
                var (p99, max, at, count) = SkinGap(look, rig, $"{lookName} {poseName}");
                compared++;
                if (count < 0) count = -count;   // measured to the surface only
                if (max > worstMax) { worstMax = max; worst = $"{lookName} {poseName} (bone {at})"; }
                worstP99 = Mathf.Max(worstP99, p99);
                bool ok = p99 <= SkinP99 && max <= SkinMax;
                if (!ok) failed++;
                if (!ok || _dump || args.Contains("--verbose"))
                    GD.Print($"[skincheck] {(ok ? "ok  " : "FAIL")} {lookName,-18} {poseName,-22} p99 {p99 * 1000f,5:F1} mm  max {max * 1000f,5:F1} mm (bone {at}), {count} vertices");
            }
        for (int i = 0; i < MaxBones; i++)
            if (_boneWorst[i].Gap > 0.002f) GD.Print($"[skincheck] bone {i,2}: worst {_boneWorst[i].Gap * 1000f,5:F1} mm in {_boneWorst[i].Where}");
        GD.Print($"[skincheck] {compared} poses compared in {sw.ElapsedMilliseconds} ms; worst p99 {worstP99 * 1000f:F1} mm, worst gap {worstMax * 1000f:F1} mm: {worst}");
        GD.Print(failed == 0 ? "[skincheck] RESULT: ok" : $"[skincheck] RESULT: FAILED — {failed} poses off");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Looks: a few in every pose, then every garment in the key poses.</summary>
    private static List<(string Name, FigureLook Look, bool EveryPose)> SkinLooks()
    {
        var looks = new List<(string, FigureLook, bool)>();
        var p = HumanPalette.ForRider(3);
        foreach (bool smooth in new[] { false, true })
        {
            string st = smooth ? "smooth" : "flat";
            looks.Add(($"plain {st}", new FigureLook(p) { Smooth = smooth }, true));
            looks.Add(($"hat {st}", new FigureLook(p, Hat: Headwear.WitchHat) { Smooth = smooth }, true));
            looks.Add(($"helmet {st}", new FigureLook(p, Helmet: true) { Smooth = smooth }, true));
        }
        foreach (var build in Enum.GetValues<BodyBuild>())
            foreach (var hair in new[] { HairStyle.Long, HairStyle.Ponytail, HairStyle.Twintails })
                looks.Add(($"{build} {hair}", new FigureLook(p.With(new Appearance(build, 1, 0, 2, hair, 2))), false));
        foreach (var g in Garments.All)
            looks.Add(($"{g.Item}", new FigureLook(p with { Outfit = Outfit.Empty.With(g.Slot, g.Code) }), false));
        return looks;
    }

    /// <summary>Poses: "key" ones run for every look.</summary>
    private static List<(string Name, Rig Rig)> SkinPoses()
    {
        var poses = new List<(string, Rig)>
        {
            ("key stand", RigFor(HumanPose.Standing)),
            ("key run", GaitRig(4.6f, 0.3f)),
            ("key dance", GaitWithDance(0f, 0f, new DanceParams(Audio.Cd.MusicStyle.Pop, 2, 0.3f, 0.6f, 3, 1f))),
            ("key aim", ApplyArms(GaitRig(1.6f, 0.7f), ItemArmPose.ShoulderAim, 1f)),
            ("key driver", DriverRig(KartMeshBuilder.SeatFor(1.05f), 0.8f, 0.5f, 0f)),
        };
        foreach (float speed in new[] { 0.5f, 1.6f, 3f, 7f })
            foreach (float phase in new[] { 0f, 0.13f, 0.25f, 0.4f, 0.6f, 0.85f })
                poses.Add(($"gait {speed}/{phase}", GaitRig(speed, phase)));
        foreach (var pose in Enum.GetValues<HumanPose>())
            poses.Add(($"pose {pose}", RigFor(pose)));
        foreach (var arm in Enum.GetValues<ItemArmPose>())
            if (arm != ItemArmPose.None)
            {
                poses.Add(($"arm {arm}", ApplyArms(RigFor(HumanPose.Standing), arm, arm is ItemArmPose.PhoneRaised or ItemArmPose.PhoneSelfie ? 2f : 1f)));
                poses.Add(($"arm {arm} half", ApplyArms(GaitRig(1.6f, 0.4f), arm, 0.5f)));
            }
        poses.Add(("vr reach", ApplyVr(RigFor(HumanPose.Standing), new VrArms(new Vector3(0.3f, -0.1f, -0.5f), new Vector3(-0.2f, 0.2f, -0.4f)))));
        poses.Add(("vr up", ApplyVr(RigFor(HumanPose.Standing), new VrArms(new Vector3(0.2f, 0.5f, -0.1f), new Vector3(-0.4f, -0.5f, 0.1f)))));
        foreach (float t in new[] { 0.1f, 0.4f, 0.9f })
            poses.Add(($"fall {t}", FallRig(t)));
        foreach (var style in Enum.GetValues<SwimStyle>())
            foreach (float phase in new[] { 0f, 0.3f, 0.7f })
                poses.Add(($"swim {style} {phase}", SwimRig(style, phase)));
        foreach (var (phase, jump) in new[] { (0f, 0f), (0.25f, 0f), (0.5f, 0f), (0.8f, 0f), (0.3f, 1f) })
            poses.Add(($"wallclimb {phase}/{jump}", WallClimbRig(phase, jump)));
        foreach (var style in Enum.GetValues<Audio.Cd.MusicStyle>())
            for (int move = 0; move < MoveCount(style); move += 2)
                foreach (float beat in new[] { 0.1f, 0.55f })
                    poses.Add(($"dance {style} {move} {beat}", GaitWithDance(move % 4 == 0 ? 1.4f : 0f, beat,
                        new DanceParams(style, move, beat, beat * 0.5f, 1, 1f))));
        foreach (int emote in new[] { 0, 3, 6, 9 })
            poses.Add(($"emote {emote}", GaitWithDance(0f, 0f, new DanceParams(Audio.Cd.MusicStyle.Pop, EmoteMoves + emote, 0.4f, 0.2f, 0, 1f))));
        var kart = KartMeshBuilder.SeatFor(1.05f);
        foreach (float wheel in new[] { 0f, -1.2f, 1.7f })
            poses.Add(($"driver {wheel}", DriverRig(kart, wheel, 0.3f, wheel > 1f ? 0.8f : 0f)));
        poses.Add(("rider", RiderRig(new Vector3(0, 0.8f, -0.1f), new Vector3(0.32f, 1.0f, 0.5f), new Vector3(0.16f, 0.35f, 0.05f))));
        foreach (var position in Enum.GetValues<Player.RidingPosition>())
            poses.Add(($"cycling {position}", CyclingRigFor(position)));
        // a ragdoll's free joints: the running ones shaken
        var j = JointsOf(RigFor(HumanPose.Running));
        var rng = new RandomNumberGenerator { Seed = 1202 };
        for (int i = 0; i < j.Length; i++) j[i] += new Vector3(rng.Randfn(0, 0.015f), rng.Randfn(0, 0.015f), rng.Randfn(0, 0.015f));
        // a ragdoll's bones keep their lengths (Player/Ragdoll's sticks): each joint back along its bone
        var rest = JointsOf(RigFor(HumanPose.Running));
        ReadOnlySpan<(Joint From, Joint To)> sticks =
        [
            (Joint.HeadBase, Joint.HeadTop), (Joint.ShoulderL, Joint.ElbowL), (Joint.ElbowL, Joint.WristL),
            (Joint.ShoulderR, Joint.ElbowR), (Joint.ElbowR, Joint.WristR), (Joint.HipL, Joint.KneeL), (Joint.KneeL, Joint.AnkleL),
            (Joint.AnkleL, Joint.ToeL), (Joint.HipR, Joint.KneeR), (Joint.KneeR, Joint.AnkleR), (Joint.AnkleR, Joint.ToeR),
        ];
        foreach (var (from, to) in sticks)
            j[(int)to] = j[(int)from] + (j[(int)to] - j[(int)from]).Normalized() * rest[(int)to].DistanceTo(rest[(int)from]);
        poses.Add(("ragdoll", new Rig(j[0], j[1], j[2], j[3], j[4], j[5], j[6], j[7], j[8], j[9], j[10], j[11],
            j[12], j[13], j[14], j[15], j[16], j[17], j[18], j[19], TorsoLean: 0f)));
        return poses;
    }

    /// <summary>
    /// How far <paramref name="look"/>'s skin posed to <paramref name="rig"/> lies from the figure
    /// rebuilt at it: p99 and max over the skinned vertices, the bone of the worst, the vertex count
    /// (−1 when the two meshes differ in it).
    /// </summary>
    private static (float P99, float Max, int Bone, int Count) SkinGap(in FigureLook look, in Rig rig, string where = "")
    {
        if (!_checkSkins.TryGetValue(look, out var skin)) _checkSkins[look] = skin = BuildSkin(look);
        var palette = look.Palette;
        // the rebuilt figure, as every builder drew it before (the look's style, no wind)
        var frames = new Transform3D[skin.BoneCount];
        Frames(rig, skin.Body, Vector3.Zero, frames);
        // a round part's facets turned with the head, the neck, a foot, as the skinned figure's are:
        // otherwise a hat's brim turning its facets round its own axis reads as a gap, and is none
        Basis Turned(int bone) => (frames[bone] * skin.BindInverse[bone]).Basis.Orthonormalized();
        var scratch = new MeshScratch
        {
            FacetTurn = part => part switch
            {
                PartHead => Turned(BoneHead), PartNeck => Turned(BoneNeck), PartCollar => Turned(BoneCollar),
                PartFootL => Turned(BoneLegL + 3), PartFootR => Turned(BoneLegR + 3),
                _ => Basis.Identity,
            },
        };
        _smoothOverride = look.Smooth;
        try { AppendRig(scratch, palette, rig, look.Legs, look.Helmet, look.Hat, look.Body, look.Head, look.FullFace); }
        finally { _smoothOverride = null; }
        var rebuilt = scratch.Build();

        var flip = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), Vector3.Zero);
        var bones = new Transform3D[skin.BoneCount];
        for (int i = 0; i < bones.Length; i++) bones[i] = flip * (frames[i] * skin.BindInverse[i]) * flip;

        var a = skin.Mesh.SurfaceGetArrays(0);
        var rest = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var bi = a[(int)Mesh.ArrayType.Bones].AsInt32Array();
        var bw = a[(int)Mesh.ArrayType.Weights].AsFloat32Array();
        // the shader's skirt floor: its custom channel marks a skirt's vertices
        var custom = a[(int)Mesh.ArrayType.Custom1].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.Custom1].AsFloat32Array();
        float floor = SkirtFloor(rig);
        var b = rebuilt.SurfaceGetArrays(0);
        var target = b[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var tris = b[(int)Mesh.ArrayType.Index].AsInt32Array();
        // a drape's slit leaves out the faces it faces (pose by pose): measure to the surface only
        bool sameCount = target.Length == rest.Length;

        TriangleGrid? grid = null;
        var gaps = new float[rest.Length];
        var skinned = _dump ? new Vector3[rest.Length] : Array.Empty<Vector3>();
        float max = 0f;
        int at = 0;
        for (int v = 0; v < rest.Length; v++)
        {
            var p = Vector3.Zero;
            for (int k = 0; k < 4; k++)
                if (bw[v * 4 + k] > 0f) p += bones[bi[v * 4 + k]] * rest[v] * bw[v * 4 + k];
            if (custom != null && custom[v * 4 + 3] != 0f) p.Y = Mathf.Max(p.Y, floor);
            // the same vertex where it was rebuilt, or (a facet turned round its limb) the surface near it
            float gap = sameCount ? p.DistanceTo(target[v]) : float.MaxValue;
            if (gap > 0.001f) gap = Mathf.Min(gap, (grid ??= new TriangleGrid(target, tris, 0.02f)).Distance(p, 0.03f));
            if (_dump) skinned[v] = p;
            gaps[v] = gap;
            if (gap > max) { max = gap; at = bi[v * 4]; }
            int bone = bw[v * 4 + 1] > bw[v * 4] ? bi[v * 4 + 1] : bi[v * 4];
            if (gap > _boneWorst[bone].Gap) _boneWorst[bone] = (gap, where);
        }
        if (_dump)
        {
            var order = Enumerable.Range(0, rest.Length).OrderByDescending(v => gaps[v]).Take(12);
            foreach (int v in order)
                GD.Print($"[skincheck]   v{v} gap {gaps[v] * 1000f:F1} mm bones {bi[v * 4]}:{bw[v * 4]:F2} {bi[v * 4 + 1]}:{bw[v * 4 + 1]:F2} rest {rest[v]} skinned {skinned[v]}"
                    + (sameCount ? $" rebuilt {target[v]}" : ""));
        }
        Array.Sort(gaps);
        // a slit is cut where the pose turns it on the rebuilt skirt, in the cloth on the skinned one:
        // the faces either side of it are each other's gap, so such a pose is judged on the rest
        if (!sameCount) return (gaps[(int)(gaps.Length * 0.9f)], gaps[(int)(gaps.Length * 0.9f)], at, -rest.Length);
        return (gaps[(int)(gaps.Length * 0.99f)], max, at, rest.Length);
    }

    private static readonly Dictionary<FigureLook, FigureSkin> _checkSkins = new();
    private static bool _dump;
    private static readonly (float Gap, string Where)[] _boneWorst = new (float, string)[MaxBones];

    /// <summary>Triangles bucketed in a grid, for the distance from a point to the nearest one.</summary>
    private sealed class TriangleGrid
    {
        private readonly Vector3[] _v;
        private readonly int[] _t;
        private readonly float _cell;
        private readonly Dictionary<(int, int, int), List<int>> _cells = new();

        public TriangleGrid(Vector3[] v, int[] t, float cell)
        {
            _v = v; _t = t; _cell = cell;
            for (int i = 0; i < t.Length; i += 3)
            {
                var lo = v[t[i]].Min(v[t[i + 1]]).Min(v[t[i + 2]]);
                var hi = v[t[i]].Max(v[t[i + 1]]).Max(v[t[i + 2]]);
                var (a, b) = (Key(lo), Key(hi));
                for (int x = a.Item1; x <= b.Item1; x++)
                    for (int y = a.Item2; y <= b.Item2; y++)
                        for (int z = a.Item3; z <= b.Item3; z++)
                        {
                            if (!_cells.TryGetValue((x, y, z), out var list)) _cells[(x, y, z)] = list = new List<int>();
                            list.Add(i);
                        }
            }
        }

        private (int, int, int) Key(Vector3 p) => ((int)Mathf.Floor(p.X / _cell), (int)Mathf.Floor(p.Y / _cell), (int)Mathf.Floor(p.Z / _cell));

        /// <summary>The distance to the nearest triangle, at most <paramref name="limit"/>.</summary>
        public float Distance(Vector3 p, float limit)
        {
            float best = limit;
            var (lo, hi) = (Key(p - Vector3.One * limit), Key(p + Vector3.One * limit));
            for (int x = lo.Item1; x <= hi.Item1; x++)
                for (int y = lo.Item2; y <= hi.Item2; y++)
                    for (int z = lo.Item3; z <= hi.Item3; z++)
                        if (_cells.TryGetValue((x, y, z), out var list))
                            foreach (int i in list)
                                best = Mathf.Min(best, p.DistanceTo(Closest(p, _v[_t[i]], _v[_t[i + 1]], _v[_t[i + 2]])));
            return best;
        }

        // the closest point of triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5)
        private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = ab.Dot(ap), d2 = ac.Dot(ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            var bp = p - b;
            float d3 = ab.Dot(bp), d4 = ac.Dot(bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            var cp = p - c;
            float d5 = ab.Dot(cp), d6 = ac.Dot(cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }
    }
}
