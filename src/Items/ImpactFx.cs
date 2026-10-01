using Godot;
using UnitSport.Audio;

namespace UnitSport.Items;

/// <summary>
/// The thud and the puff of dust when a thrown thing hits the ground. It watches its parent's
/// position, not the physics: the thrower's copy moves by simulation, everyone else's by the
/// synchronizer, and both look the same from here — falling fast, then suddenly not. So every peer
/// hears and sees the landing without anything extra on the wire.
/// </summary>
public partial class ImpactFx : Node3D
{
    /// <summary>The thing's largest side, metres: small things click, big things thump.</summary>
    public float Size { get; set; } = 0.2f;

    private Node3D? _body;
    private Vector3 _last;
    private double _since;
    private float _fallSpeed;   // downward speed at the last move, m/s
    private bool _init;
    private double _cooldown;

    private const float HitSpeed = 2.2f;

    public override void _Ready() => _body = GetParent() as Node3D;

    /// <summary>The body was moved, not thrown (a proxy handed over): no landing to read from that jump.</summary>
    public void Rebase()
    {
        _init = false;
        _fallSpeed = 0;
    }

    public override void _Process(double delta)
    {
        if (_body == null) return;
        _cooldown -= delta;
        _since += delta;
        var p = _body.GlobalPosition;
        if (!_init)
        {
            _last = p;
            _init = true;
            return;
        }
        bool moved = (p - _last).LengthSquared() > 1e-8f;
        if (!moved)
        {
            // stopped dead: if it was falling, that was the ground
            if (_since > 0.12 && _fallSpeed > HitSpeed) Hit(_fallSpeed);
            if (_since > 0.12) _fallSpeed = 0;
            return;
        }
        float vy = (p.Y - _last.Y) / (float)Math.Max(_since, 1e-3);
        // falling fast, then rising or barely falling: it bounced off something
        if (_fallSpeed > HitSpeed && -vy < _fallSpeed * 0.35f) Hit(_fallSpeed);
        _fallSpeed = Math.Max(0, -vy);
        _last = p;
        _since = 0;
    }

    private void Hit(float speed)
    {
        _fallSpeed = 0;
        if (_cooldown > 0) return;
        _cooldown = 0.18;
        float strength = Mathf.Clamp((speed - HitSpeed) / 9f, 0.1f, 1f);
        Burst(GetTree().CurrentScene ?? GetParent(), _body!.GlobalPosition, strength, Size);
    }

    /// <summary>A landing at <paramref name="at"/>: a thud pitched by size, a ring of dust scaled by strength (0..1).</summary>
    public static void Burst(Node parent, Vector3 at, float strength, float size)
    {
        var bank = SfxSynth.LandingBank;
        var sound = new AudioStreamPlayer3D
        {
            Stream = bank.Variants[Random.Shared.Next(bank.Variants.Length)],
            PitchScale = Mathf.Clamp(1.9f - size * 1.6f, 0.9f, 2.2f) * (0.92f + 0.16f * Random.Shared.NextSingle()),
            VolumeDb = Mathf.Lerp(-16f, -4f, strength),
            UnitSize = 3f, MaxDistance = 40f, Bus = SfxBus.Name,
            TopLevel = true,
        };
        var dust = new CpuParticles3D
        {
            TopLevel = true,
            Emitting = true, OneShot = true, Explosiveness = 0.95f,
            Amount = 6 + (int)(14 * strength), Lifetime = 0.7f,
            Mesh = new BoxMesh { Size = Vector3.One * 0.035f },
            Direction = Vector3.Up, Spread = 75f,
            InitialVelocityMin = 0.6f + strength, InitialVelocityMax = 1.4f + 2.6f * strength,
            Gravity = new Vector3(0, -6f, 0), DampingMin = 2f, DampingMax = 3f,
            ScaleAmountMin = 0.5f, ScaleAmountMax = 1f + size * 1.5f,
            ScaleAmountCurve = FadeCurve,
            Color = new Color(0.62f, 0.56f, 0.46f),
            MaterialOverride = DustMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(sound);
        parent.AddChild(dust);
        sound.GlobalPosition = at;
        dust.GlobalPosition = at + Vector3.Up * 0.05f;
        sound.Play();
        parent.GetTree().CreateTimer(1.5).Timeout += () =>
        {
            if (IsInstanceValid(sound)) sound.QueueFree();
            if (IsInstanceValid(dust)) dust.QueueFree();
        };
    }

    private static Curve? _fade;
    private static Curve FadeCurve => _fade ??= MakeFade();
    private static Curve MakeFade()
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, 0.4f));
        c.AddPoint(new Vector2(0.15f, 1f));
        c.AddPoint(new Vector2(1, 0f));
        return c;
    }

    private static StandardMaterial3D? _dust;
    private static StandardMaterial3D DustMaterial => _dust ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };
}
