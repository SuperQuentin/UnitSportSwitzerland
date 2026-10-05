using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// Three Swiss items (#478), each an item event every peer plays its own part of (docs/notes/items/swiss-items.md):
/// <list type="bullet">
/// <item><see cref="ItemId.Alphorn"/>: heard far and wide. Whoever hears it gets a marker where it was blown; the
/// blower's own radar picks up every opponent near, hidden or not, for a while (<c>BrManager.HeardHorn</c>).</item>
/// <item><see cref="ItemId.FonduePot"/>: everyone standing round it is fed, whoever they are.</item>
/// <item><see cref="ItemId.SmokeCanister"/>: a cloud that hides who is inside from the radar (<see cref="InSmoke"/>).</item>
/// </list>
/// </summary>
public static class SwissItems
{
    /// <summary>Seconds a horn's effects last: the blower's radar, everyone else's marker.</summary>
    public const double HornSeconds = 8;
    /// <summary>Seconds between two blows of one horn.</summary>
    public const double HornCooldown = 30;
    /// <summary>How far the blower's radar reaches while the horn sounds, m.</summary>
    public const float HornRadar = 120f;

    /// <summary>Health the fondue gives everyone within <see cref="FondueReach"/>.</summary>
    public const float FondueHeal = 40f;
    public const float FondueReach = 4f;

    /// <summary>A smoke cloud's radius, m, and how long it lasts, s.</summary>
    public const float SmokeRadius = 5f;
    public const double SmokeSeconds = 12;
    /// <summary>How far the canister is thrown, m.</summary>
    public const float SmokeThrow = 15f;

    // ---- smoke, in LV95 (every peer has its own origin, #185) ---------------------------------

    private static readonly List<(GlobalPos At, double Until)> Clouds = new();

    /// <summary>Game time (docs/notes/general/fast-checks.md): a cloud lasts its seconds of simulation.</summary>
    private static double Now => GameClock.Now;

    /// <summary>Whether a point stands in a live smoke cloud (the radar does not see in).</summary>
    public static bool InSmoke(GlobalPos at)
    {
        Clouds.RemoveAll(c => c.Until < Now);
        foreach (var c in Clouds)
            if (c.At.HorizontalDistanceTo(at) <= SmokeRadius + 1f && Math.Abs(c.At.Alt - at.Alt) < 8) return true;
        return false;
    }

    /// <summary>The <see cref="ItemEventKind.Smoke"/> handler: the cloud, on every peer, where the canister landed.</summary>
    public static void OnSmoke(ItemEvents n, ItemEvent e)
    {
        if (n.Origin is { } origin) Clouds.Add((origin.ToGlobal(e.Position), Now + SmokeSeconds));
        n.Sound3D(e.Position, Hiss, 1f, -4f, unitSize: 8f, maxDistance: 120f);
        var cloud = new Node3D { TopLevel = true };
        n.AddChild(cloud);
        cloud.GlobalPosition = e.Position;
        var rng = new Random(e.Position.GetHashCode());
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(0.78f, 0.79f, 0.80f, 0.9f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        for (int i = 0; i < 16; i++)
        {
            float a = (float)(rng.NextDouble() * Math.Tau), d = SmokeRadius * 0.75f * Mathf.Sqrt((float)rng.NextDouble());
            float r = 1.6f + (float)rng.NextDouble() * 1.6f;
            cloud.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = r, Height = r * 1.6f, RadialSegments = 8, Rings = 4 },
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(Mathf.Cos(a) * d, 0.8f + (float)rng.NextDouble() * 2.2f, Mathf.Sin(a) * d),
            });
        }
        cloud.Scale = Vector3.One * 0.2f;
        var t = cloud.CreateTween();
        t.TweenProperty(cloud, "scale", Vector3.One, 1.2).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Quad);
        t.TweenInterval(SmokeSeconds - 3.2);
        t.TweenProperty(mat, "albedo_color:a", 0f, 2.0);
        t.TweenCallback(Callable.From(cloud.QueueFree));
    }

    // ---- the alphorn --------------------------------------------------------------------------

    /// <summary>The <see cref="ItemEventKind.Horn"/> handler: the call, far; a marker for whoever hears it, a radar for the blower.</summary>
    public static void OnHorn(ItemEvents n, ItemEvent e)
    {
        n.Sound3D(e.Position, Horn, 1f, 4f, unitSize: 60f, maxDistance: 1500f);
        if (n.Origin is { } origin) BattleRoyale.BrManager.Instance?.HeardHorn(origin.ToGlobal(e.Position), e.Peer, e.Local);
    }

    // ---- the fondue ---------------------------------------------------------------------------

    /// <summary>The <see cref="ItemEventKind.Fondue"/> handler: this peer's own player eats if it stands near.</summary>
    public static void OnFondue(ItemEvents n, ItemEvent e)
    {
        n.Sound3D(e.Position, Bubble, 1f, -4f, unitSize: 4f, maxDistance: 40f);
        n.LightPulse(e.Position + Vector3.Up * 0.5f, new Color(1f, 0.75f, 0.35f), 3f, 6f, 1.5f);
        // this peer's own player, standing and able to eat
        var me = ItemController.Instance?.UsablePlayer;
        if (me == null || me.KnockedOut || me.GlobalPosition.DistanceTo(e.Position) > FondueReach) return;
        if (me.Heal(FondueHeal)) me.Announce($"FONDUE  +{FondueHeal:F0}", false);
    }

    // ---- sounds -------------------------------------------------------------------------------

    private static AudioStreamWav? _horn, _hiss, _bubble;

    /// <summary>A long low call: B♭2 with its overtones, swelling in and dying away.</summary>
    public static AudioStreamWav Horn => _horn ??= Dsp.OneShot(3.2f, 71, (_, n) =>
    {
        var s = new float[n];
        const float hz = 116.5f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float env = Mathf.Min(1f, t / 0.35f) * Mathf.Clamp((3.2f - t) / 0.9f, 0f, 1f);
            float v = 0f;
            for (int k = 1; k <= 6; k++) v += Mathf.Sin(Mathf.Tau * hz * k * t + k) / (k * 0.9f);
            s[i] = v * env * 0.32f * (1f + 0.04f * Mathf.Sin(Mathf.Tau * 5f * t));
        }
        return s;
    });

    /// <summary>A canister venting: a hiss that thins out.</summary>
    public static AudioStreamWav Hiss => _hiss ??= Dsp.OneShot(2.5f, 72, (rng, n) =>
    {
        var s = new float[n];
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Dsp.Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.35f;
            s[i] = (noise - lp) * 0.45f * Mathf.Exp(-t * 1.1f);
        }
        return s;
    });

    /// <summary>Cheese bubbling: low plops at random.</summary>
    public static AudioStreamWav Bubble => _bubble ??= Dsp.OneShot(1.2f, 73, (rng, n) =>
    {
        var s = new float[n];
        for (int b = 0; b < 7; b++)
        {
            int start = (int)(rng.NextDouble() * (n - Dsp.Rate * 0.12));
            float hz = 180f + (float)rng.NextDouble() * 160f;
            for (int i = 0; i < Dsp.Rate * 0.12 && start + i < n; i++)
            {
                float t = i / (float)Dsp.Rate;
                s[start + i] += Mathf.Sin(Mathf.Tau * hz * t * (1f + t * 6f)) * Mathf.Exp(-t * 35f) * 0.5f;
            }
        }
        return s;
    });
}
