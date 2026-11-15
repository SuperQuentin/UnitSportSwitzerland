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
            float dist = (float)GlobalPos.HorizontalDistance(h.Plan.E, h.Plan.N, ee, en);
            bool any = false;
            foreach (var c in h.Cows) if (_over.ContainsKey(c.Key)) { any = true; break; }
            if (dist > DrawRange && !any)
            {
                if (h.Bodies is { Visible: true }) h.Bodies.Visible = h.Heads!.Visible = false;
                continue;
            }
            bool fresh = h.Bodies == null;
            if (fresh) Build(h);
            // shown once its ground answered (Pose hides a herd whose ground does not yet, #2031)
            if (!h.Bodies!.Visible && !float.IsNaN(h.AnchorGround)) h.Bodies.Visible = h.Heads!.Visible = true;
            // far herds a few times a second, staggered so they do not all land on one frame (NearEvery, FarEvery)
            int every = dist > NearRange ? FarEvery : NearEvery;
            if (!fresh && !any && every > 1 && (_frame + h.Plan.Index) % every != 0) continue;
            // a near herd posed this frame anyway (being carried, just built) moves by this frame's time only
            Pose(h, now, eye, ee, en, dist <= NearRange && (fresh || any) ? dt : dt * every);
        }

        if (GroundCheck) CheckGround(delta);
        Sounds(delta, ee, en, now);
    }

    // ---- --herdgroundcheck (#2031): the cows stand on the ground as drawn ------------------------------------------

    private static readonly bool GroundCheck = Core.CmdArgs.Has("--herdgroundcheck");
    private double _checkT, _checkNext = 2;
    private float _checkWorst;
    private int _checkCows, _checkHidden, _checkSamples;
    private bool _checkDone;
    private int _checkSnaps;

    /// <summary>
    /// Every 2 s: each drawn standing cow's height against the ground read afresh under it, and the herds in range whose ground
    /// does not answer (hidden). After 20 s of samples, RESULT ok when no drawn cow was more than 0.3 m off.
    /// </summary>
    private void CheckGround(double delta)
    {
        _checkT += delta;
        if (_checkT < _checkNext) return;
        _checkNext += 2;
        float worst = 0; int cows = 0, hidden = 0;
        foreach (var h in _all)
        {
            if (h.Bodies == null) continue;
            if (!h.Bodies.Visible) { if (float.IsNaN(h.AnchorGround) && h.Ground.Length > 0) hidden++; continue; }
            for (int i = 0; i < h.Cows.Length; i++)
            {
                if (h.WasAir[i] || float.IsNaN(h.Ground[i])) continue;
                // at the very point the cow's height was read (it reads again only after walking 0.4 m): staleness alone, not slope
                float fresh = GroundAt(h.Plan.E + h.GroundAt[i].X, h.Plan.N + h.GroundAt[i].Y);
                if (float.IsNaN(fresh)) continue;
                worst = Mathf.Max(worst, Mathf.Abs(fresh - h.Ground[i]));
                cows++;
            }
        }
        _checkWorst = Mathf.Max(_checkWorst, _checkT > 20 ? worst : 0);
        _checkCows += cows; _checkHidden += hidden; _checkSamples++;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[herdgroundcheck] {_checkT:F0} s: {cows} cows drawn, worst {worst:F2} m off the ground; {hidden} herds waiting for their ground; {_checkSnaps} snaps so far"));
        if (_checkT > 40 && !_checkDone && (_checkDone = true))
            GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[herdgroundcheck] RESULT: {(_checkWorst <= 0.3f && _checkCows > 0 && _checkSnaps == 0 ? "ok" : "FAILED")} (worst after 20 s {_checkWorst:F2} m, {_checkCows} cow samples, {_checkSnaps} visible herds moved over 0.3 m)"));
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
        h.Bodies = Styles.ZoomRanges.Track(Make("Cows", CowMeshBuilder.Body(h.Plan.Breed)));
        h.Heads = Styles.ZoomRanges.Track(Make("Heads", CowMeshBuilder.Head(h.Plan.Breed)));
        AddChild(h.Bodies);
        AddChild(h.Heads);
    }

    private void Pose(Live h, double now, Vector3 eye, double ee, double en, float dt)
    {
        var plan = h.Plan;
        // the ground changed since the heights were read (a leaf drawn or rebuilt, the fill on its pyramid, #2031): read
        // them again, or a herd read on a stand-in stood in the air over the ground drawn later
        int version = GroundVersion;
        if (version != h.GroundVersion)
        {
            h.GroundVersion = version;
            if (!float.IsNaN(h.AnchorGround) && GroundAt(plan.E, plan.N) is var again && !float.IsNaN(again) && Math.Abs(again - h.AnchorGround) > 0.3f)
            {
                // a visible herd that moves is a snap the player sees (--herdgroundcheck counts them)
                bool seen = h.Bodies!.Visible;
                if (seen) _checkSnaps++;
                GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[herds] a herd at {plan.E:F0},{plan.N:F0}: its ground moved {again - h.AnchorGround:F1} m, read again{(seen ? " (in view: a snap)" : "")}"));
            }
            h.AnchorGround = float.NaN;
            System.Array.Fill(h.Ground, float.NaN);
        }
        if (float.IsNaN(h.AnchorGround)) h.AnchorGround = GroundAt(plan.E, plan.N);
        // no ground answered there yet (on the earth world only the loaded leaves answer: a herd drawn before its leaf landed):
        // not drawn until it does. It was drawn at the camera's own height meanwhile, a herd standing in the air (#2031)
        bool known = !float.IsNaN(h.AnchorGround);
        if (h.Bodies!.Visible != known) h.Bodies.Visible = h.Heads!.Visible = known;
        if (!known) return;
        float anchorY = h.AnchorGround;
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
            // a cow on ground that does not answer yet (past its herd's leaf): not drawn, never at the anchor's height (#2031)
            bool unplaced = !air && float.IsNaN(h.Ground[i]);
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

            if (unplaced) basis = basis.Scaled(Vector3.Zero);
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
            if (GlobalPos.HorizontalDistanceSquared(h.Plan.E, h.Plan.N, ee, en) > (range + 60) * (range + 60)) continue;
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
