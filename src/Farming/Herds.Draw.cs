using Godot;
using UnitSport.Core;

namespace UnitSport.Farming;

/// <summary>
/// The herds drawn (client): two MultiMeshes a herd, its cows' bodies and heads
/// (<see cref="CowMeshBuilder"/>), each node at its herd's anchor and the cows placed from LV95
/// deltas, so a floating-origin shift moves nothing by hand. Near herds are posed every frame,
/// far ones a few times a second; nothing is allocated per frame. A cow standing near the camera
/// lifts its head and turns it to look; a cow in a beam kicks and wriggles and moos. The moos and the
/// bells come from the nearest cows, a few voices at most.
/// </summary>
public partial class Herds
{
    /// <summary>Herds further than this are not drawn, m.</summary>
    private const float DrawRange = 650f;
    /// <summary>Within this, posed every frame; beyond, every few frames.</summary>
    private const float NearRange = 300f;
    /// <summary>A standing cow turns its head to a camera this close, m.</summary>
    private const float LookRange = 14f;

    private static ShaderMaterial? _material;
    private readonly Random _rng = new();
    private AudioStreamPlayer3D[] _voices = [];
    private int _voice;
    private double _mooIn = 4, _bellIn = 1.5;
    private int _frame;
    private double _t;

    /// <summary>The vertex-colour prop material every cow is drawn with (the style chain swaps its shader).</summary>
    public static ShaderMaterial Material()
    {
        if (_material != null) return _material;
        _material = Styles.StyleKit.Material(Styles.MaterialRole.Prop);
        FogUniforms.Apply(_material);
        return _material;
    }

    private void ReadyDraw()
    {
        _voices = new AudioStreamPlayer3D[3];
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new AudioStreamPlayer3D
            {
                Name = $"Voice{i}", Bus = Audio.SfxBus.Name, UnitSize = 8f, MaxDistance = 120f,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            };
            AddChild(_voices[i]);
        }
    }

    // on Low and Very low (#1216; a standalone headset's since #1112) the near herds every other frame, the far half as often
    private static int NearEvery => Core.GameSettings.Cheap ? 2 : 1;
    private static int FarEvery => Core.GameSettings.Cheap ? 16 : 8;

    public override void _Process(double delta)
    {
        if (_dedicated) return;
        _t += delta;
        _frame++;
        var cam = GetViewport()?.GetCamera3D();
        if (cam == null) return;
        var eye = cam.GlobalPosition;
        var (ee, en) = _origin.ToLv95(eye);
        double now = Now;
        float dt = (float)delta;

        foreach (var h in _all)
        {
            double de = h.Plan.E - ee, dn = h.Plan.N - en;
            float dist = (float)Math.Sqrt(de * de + dn * dn);
            bool any = false;
            foreach (var c in h.Cows) if (_over.ContainsKey(c.Key)) { any = true; break; }
            if (dist > DrawRange && !any)
            {
                if (h.Bodies is { Visible: true }) h.Bodies.Visible = h.Heads!.Visible = false;
                continue;
            }
            bool fresh = h.Bodies == null;
            if (fresh) Build(h);
            if (!h.Bodies!.Visible) h.Bodies.Visible = h.Heads!.Visible = true;
            // far herds a few times a second, staggered so they do not all land on one frame (NearEvery, FarEvery)
            int every = dist > NearRange ? FarEvery : NearEvery;
            if (!fresh && !any && every > 1 && (_frame + h.Plan.Index) % every != 0) continue;
            // a near herd posed this frame anyway (being carried, just built) moves by this frame's time only
            Pose(h, now, eye, ee, en, dist <= NearRange && (fresh || any) ? dt : dt * every);
        }

        Sounds(delta, ee, en, now);
    }

    private void Build(Live h)
    {
        int n = h.Cows.Length;
        h.Ground = new float[n];
        h.GroundAt = new Vector2[n];
        h.HeadPitch = new float[n];
        h.HeadYaw = new float[n];
        h.BodyBuffer = new float[n * 12];
        h.HeadBuffer = new float[n * 12];
        h.WasAir = new bool[n];
        h.AirAt = new Vector3[n];
        Array.Fill(h.Ground, float.NaN);
        MultiMeshInstance3D Make(string name, Mesh mesh) => new()
        {
            Name = $"{name}{h.Plan.Tile}_{h.Plan.Index}",
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = n },
            MaterialOverride = Material(),
            // a cow may be carried a long way off: never cull the herd by its spread
            CustomAabb = new Aabb(new Vector3(-400, -50, -400), new Vector3(800, 450, 800)),
            VisibilityRangeEnd = DrawRange,
        };
        h.Bodies = Make("Cows", CowMeshBuilder.Body(h.Plan.Breed));
        h.Heads = Make("Heads", CowMeshBuilder.Head(h.Plan.Breed));
        AddChild(h.Bodies);
        AddChild(h.Heads);
    }

    private void Pose(Live h, double now, Vector3 eye, double ee, double en, float dt)
    {
        var plan = h.Plan;
        if (float.IsNaN(h.AnchorGround)) h.AnchorGround = GroundAt(plan.E, plan.N);
        float anchorY = float.IsNaN(h.AnchorGround) ? eye.Y : h.AnchorGround;
        var anchor = _origin.ToWorld(plan.E, plan.N, anchorY);
        if (!h.Bodies!.Position.IsEqualApprox(anchor)) h.Bodies.Position = h.Heads!.Position = anchor;

        var bodies = h.Bodies.Multimesh;
        var heads = h.Heads!.Multimesh;
        for (int i = 0; i < h.Cows.Length; i++)
        {
            var (e, n, alt, yaw, head, gait) = Where(h, i, now);
            bool air = !float.IsNaN(alt);
            bool lifted = air && !h.WasAir[i];
            if (air != h.WasAir[i])
            {
                h.WasAir[i] = air;
                if (air) Say(Audio.CowSounds.Alarmed, e, n, alt, 0f);
            }
            float y;
            if (air) y = alt;
            else
            {
                var at = new Vector2((float)(e - plan.E), (float)(n - plan.N));
                if (float.IsNaN(h.Ground[i]) || at.DistanceSquaredTo(h.GroundAt[i]) > 0.16f)
                {
                    h.Ground[i] = GroundAt(e, n);
                    h.GroundAt[i] = at;
                }
                y = float.IsNaN(h.Ground[i]) ? anchorY : h.Ground[i];
            }
            var local = new Vector3((float)(e - plan.E), y - anchorY, (float)(plan.N - n));
            if (air)
            {
                // the authority's updates come ~10 a second: ease toward them (at once on this peer if it is the authority)
                h.AirAt[i] = lifted || Authority ? local : h.AirAt[i].Lerp(local, MathX.Damp(12f, dt));
                local = h.AirAt[i];
            }

            // the head: grazing or up; up and turned to a camera that comes close to a standing cow
            float wantPitch = head * CowMeshBuilder.GrazePitch, wantYaw = 0f;
            double ce = e - ee, cn = n - en;
            if (!air && gait < 0.5f && ce * ce + cn * cn < LookRange * LookRange)
            {
                float toCam = CowMotion.YawOf(new Vector2((float)-ce, (float)-cn));
                wantYaw = Mathf.Clamp(Mathf.AngleDifference(yaw, toCam), -1.0f, 1.0f);
                wantPitch = -0.1f;
            }
            float k = MathX.Damp(3f, dt);
            h.HeadPitch[i] += (wantPitch - h.HeadPitch[i]) * k;
            h.HeadYaw[i] += (wantYaw - h.HeadYaw[i]) * k;

            Basis basis = new(Vector3.Up, yaw);
            float headShake = 0f;
            if (air)
            {
                // kicking in the beam: a wriggle about both axes, the head tossing
                float w = (float)_t * 9f + i * 1.3f;
                basis *= new Basis(Vector3.Forward, Mathf.Sin(w) * 0.28f) * new Basis(Vector3.Right, Mathf.Sin(w * 0.77f) * 0.18f);
                headShake = Mathf.Sin(w * 1.6f) * 0.5f;
            }
            else if (gait > 0.5f)
                local.Y += Mathf.Abs(Mathf.Sin((float)now * 4.2f + i)) * 0.035f;   // the walk's gentle bob

            MultiMeshPack.Transform(h.BodyBuffer, i * 12, new Transform3D(basis, local));
            var headBasis = basis * new Basis(Vector3.Up, h.HeadYaw[i] + headShake) * new Basis(Vector3.Right, -h.HeadPitch[i]);
            MultiMeshPack.Transform(h.HeadBuffer, i * 12, new Transform3D(headBasis, local + basis * CowMeshBuilder.Neck));
        }
        // one engine call a mesh, not one a cow: across the engine boundary each costs more than the pose (#1112)
        bodies.Buffer = h.BodyBuffer;
        heads.Buffer = h.HeadBuffer;
    }

    // ---- sounds -----------------------------------------------------------------------------------

    private void Sounds(double delta, double ee, double en, double now)
    {
        _mooIn -= delta;
        _bellIn -= delta;
        if (_mooIn > 0 && _bellIn > 0) return;
        bool moo = _mooIn <= 0;
        if (moo) _mooIn = 5 + _rng.NextDouble() * 9;
        else _bellIn = 0.7 + _rng.NextDouble() * 1.8;
        float range = moo ? 80f : 35f;

        // a random cow among the near ones (reservoir pick: no list)
        int seen = 0;
        double pe = 0, pn = 0;
        float palt = float.NaN;
        foreach (var h in _all)
        {
            double de = h.Plan.E - ee, dn = h.Plan.N - en;
            if (de * de + dn * dn > (range + 60) * (range + 60)) continue;
            for (int i = 0; i < h.Cows.Length; i++)
            {
                var (e, n, alt, _, _, _) = Where(h, i, now);
                if ((e - ee) * (e - ee) + (n - en) * (n - en) > range * range) continue;
                if (_rng.Next(++seen) != 0) continue;
                pe = e; pn = n; palt = alt;
            }
        }
        if (seen == 0) return;
        if (moo) Say(Audio.CowSounds.Moo, pe, pn, palt, -4f);
        else Say(Audio.CowSounds.Bell, pe, pn, palt, -10f);
    }

    private void Say(Audio.SfxBank bank, double e, double n, float alt, float db)
    {
        if (_voices.Length == 0) return;
        if (float.IsNaN(alt)) alt = GroundAt(e, n);
        if (float.IsNaN(alt)) return;
        var (stream, pitch, jitter) = bank.Pick(_rng);
        var v = _voices[_voice++ % _voices.Length];
        v.GlobalPosition = _origin.ToWorld(e, n, alt + 1.2f);
        v.Stream = stream;
        v.PitchScale = pitch;
        v.VolumeDb = db + jitter;
        v.Play();
    }
}
