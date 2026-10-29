using System.Text;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Combat;

/// <summary>
/// A training dummy (#1371, <c>/dummy</c>): a straw man on a post that takes every blow of the sword, shows the damage, goes down
/// at zero and stands up again. One built mesh on a pivot that turns (a rigid part: nothing is rebuilt as it moves); the
/// server's list is the truth (<see cref="SwordService"/>), this is what a peer draws: the same numbers and the same recoil
/// from the same <see cref="Struck"/> call, so every peer sees it sway alike.
/// </summary>
public partial class TrainingDummy : Node3D
{
    public int Id { get; private set; }

    /// <summary>Up, and a target. A dummy that is down cannot be hit until it stands.</summary>
    public bool Standing => _hp > 0f;

    /// <summary>Health left, as the server last said.</summary>
    public float Hp => _hp;

    /// <summary>How far it is leaning now, radians (the probes read the knockback off it).</summary>
    public float Lean => _angle;

    /// <summary>The blows this peer has seen it take, and their total (reset after a quiet moment).</summary>
    public int Blows { get; private set; }

    private WorldOrigin _origin = null!;
    private GlobalPos _at;
    private int _epoch = -1;
    private float _yaw, _hp, _hpMax = SwordService.DummyHp;
    private Node3D _pivot = null!;
    private Label3D _label = null!;
    private readonly StringBuilder _text = new();
    private string _shown = "";
    private float _total, _last, _quiet;
    // the lean: a damped spring about the axis across the push
    private Vector3 _dir = Vector3.Forward;
    private float _angle, _velocity, _rest;
    private float _wobbleScale = 1f;

    private static ArrayMesh? _mesh;

    public static TrainingDummy Create(int id, WorldOrigin origin, GlobalPos at, float yaw, float hp) =>
        new() { Id = id, _origin = origin, _at = at, _yaw = yaw, _hp = hp, Name = "Dummy" + id };

    public override void _Ready()
    {
        _pivot = new Node3D { Name = "Pivot" };
        AddChild(_pivot);
        _pivot.AddChild(new MeshInstance3D
        {
            Name = "Body", Mesh = Body(), MaterialOverride = HumanMeshBuilder.Material(),
        });
        _label = new Label3D
        {
            Name = "Readout", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 28, PixelSize = 0.0042f,
            OutlineSize = 8, Modulate = new Color(1f, 1f, 1f, 0.9f), OutlineModulate = new Color(0.1f, 0.1f, 0.12f),
            Shaded = false, Position = new Vector3(0, 2.2f, 0), NoDepthTest = false,
        };
        AddChild(_label);
        // solid for walkers: a post's width, as tall as a man; it does not lean with the straw
        var body = new StaticBody3D { Name = "Solid" };
        body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = 0.36f, Height = 1.9f }, Position = new Vector3(0, 0.95f, 0) });
        AddChild(body);
        Place();
        Refresh(true);
    }

    /// <summary>Puts it where its LV95 spot is in this origin, and on the ground when the ground is already drawn.</summary>
    private void Place()
    {
        _epoch = _origin.Epoch;
        var p = _origin.ToWorld(_at);
        Rotation = new Vector3(0, _yaw, 0);
        GlobalPosition = p;
        if (!IsInsideTree()) return;
        var query = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 2.5f, p + Vector3.Down * 3f, 1u);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0 && hit["collider"].AsGodotObject() is not UnitSport.Player.FootPlayer) GlobalPosition = hit["position"].AsVector3();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (_origin.Epoch != _epoch) Place();
        // the spring: pushed, it leans away and swings back; down, it lies; up, it stands
        float target = _hp > 0f ? 0f : 1.45f;
        _rest = Mathf.MoveToward(_rest, target, dt * (_hp > 0f ? 2.2f : 7f));
        _velocity += (-90f * (_angle - _rest) - 7f * _velocity) * dt;
        _angle += _velocity * dt;
        _angle = Mathf.Clamp(_angle, -0.2f, 1.6f);
        if (Mathf.Abs(_angle) < 1e-4f && Mathf.Abs(_velocity) < 1e-3f) { _angle = 0f; _velocity = 0f; }
        var axis = new Vector3(_dir.Z, 0, -_dir.X);
        _pivot.Basis = axis.LengthSquared() > 1e-6f && Mathf.Abs(_angle) > 1e-5f ? new Basis(axis.Normalized(), _angle) : Basis.Identity;
        // the readout forgets the last combo after a quiet moment
        if (_total > 0f && (_quiet += dt) > 4f)
        {
            _total = _last = 0f;
            Blows = 0;
            Refresh(false);
        }
    }

    /// <summary>
    /// A blow landed (every peer, from the server's word): <paramref name="knock"/> is the push in world axes, <paramref name="hp"/>
    /// the health left, <paramref name="down"/> that it fell.
    /// </summary>
    public void Struck(Vector3 knock, float damage, float hp, bool down)
    {
        _hp = hp;
        _last = damage;
        _total += damage;
        _quiet = 0f;
        Blows++;
        var local = GlobalBasis.Inverse() * knock;
        local.Y = 0;
        if (local.LengthSquared() > 1e-4f)
        {
            // tip away along the push: its direction swings to the new blow, the angle keeps what it had
            _dir = local.Normalized();
            _velocity += local.Length() * 0.55f * _wobbleScale;
        }
        Refresh(false);
    }

    /// <summary>Back on its feet at <paramref name="hp"/> (the server's word after a pause).</summary>
    public void Reset(float hp)
    {
        _hp = hp;
        _total = _last = 0f;
        Blows = 0;
        Refresh(false);
    }

    private void Refresh(bool first)
    {
        _text.Clear();
        _text.Append(Mathf.CeilToInt(_hp)).Append(" / ").Append(Mathf.RoundToInt(_hpMax));
        if (_total > 0f) _text.Append("\nlast ").Append(Mathf.RoundToInt(_last)).Append("   total ").Append(Mathf.RoundToInt(_total));
        // text only when it changed
        if (!first && _text.Equals(_shown.AsSpan())) return;
        _shown = _text.ToString();
        _label.Text = _shown;
        _label.Modulate = _hp <= 0f ? new Color(1f, 0.45f, 0.4f, 0.9f) : new Color(1f, 1f, 1f, 0.9f);
    }

    // ---- the model ---------------------------------------------------------------------------------------

    /// <summary>The dummy's one mesh: post, crossbar, straw torso with a bullseye, a sack head, on a round base. Front is -Z.</summary>
    [Showcase("Combat", "Training dummy")]
    public static ArrayMesh Body()
    {
        if (_mesh != null) return _mesh;
        var wood = new Color(0.50f, 0.34f, 0.19f);
        var woodDark = new Color(0.36f, 0.24f, 0.13f);
        var straw = new Color(0.86f, 0.72f, 0.36f);
        var sack = new Color(0.78f, 0.66f, 0.46f);
        var red = new Color(0.80f, 0.14f, 0.14f);
        var s = new MeshScratch();
        s.Tube(new Vector3(0, 0, 0), new Vector3(0, 0.14f, 0), 0.40f, 0.34f, woodDark, 12);                       // base
        s.Tube(new Vector3(0, 0.1f, 0), new Vector3(0, 1.55f, 0), 0.07f, wood, 8);                                // post
        s.Box(new Vector3(0, 1.30f, 0), new Vector3(0.95f, 0.09f, 0.09f), wood);                                  // arms
        s.Tube(new Vector3(0, 0.72f, 0), new Vector3(0, 1.42f, 0), 0.27f, 0.23f, straw, 10);                      // torso
        s.Tube(new Vector3(0, 0.99f, 0), new Vector3(0, 1.07f, 0), 0.285f, red, 10);                              // belt
        s.Box(new Vector3(0, 1.17f, -0.245f), new Vector3(0.28f, 0.28f, 0.02f), red);                             // bullseye
        s.Box(new Vector3(0, 1.17f, -0.252f), new Vector3(0.18f, 0.18f, 0.02f), new Color(0.96f, 0.95f, 0.90f));
        s.Box(new Vector3(0, 1.17f, -0.259f), new Vector3(0.08f, 0.08f, 0.02f), red);
        s.Box(new Vector3(0, 1.68f, 0), new Vector3(0.28f, 0.30f, 0.28f), sack);                                  // head
        s.Box(new Vector3(0, 1.84f, 0), new Vector3(0.30f, 0.05f, 0.30f), red);                                   // a band round it
        foreach (float x in new[] { -0.06f, 0.06f })
            s.Box(new Vector3(x, 1.70f, -0.145f), new Vector3(0.04f, 0.04f, 0.015f), new Color(0.12f, 0.1f, 0.1f));   // stitched eyes
        s.Box(new Vector3(0, 1.62f, -0.145f), new Vector3(0.12f, 0.015f, 0.015f), new Color(0.12f, 0.1f, 0.1f));      // mouth
        foreach (float x in new[] { -0.47f, 0.47f })
            s.Tube(new Vector3(x, 1.22f, 0), new Vector3(x, 1.40f, 0), 0.07f, straw, 8);                          // the straw hands
        return _mesh = s.Build();
    }
}
