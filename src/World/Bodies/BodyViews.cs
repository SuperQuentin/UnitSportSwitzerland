using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// The other bodies' globes (#1848, docs/plans/planets.md section 6), under <c>--frame earth</c>: a
/// <see cref="BodyGlobe"/> for each body whose pyramid is there, and the Earth's own one for when the anchor is on another
/// body (the Earth seen from the Moon: the Earth's <see cref="Globe"/>, built round the Earth's leaves, steps aside then).
///
/// <para>
/// <b>Where the bodies' data is</b>: <c>--bodies DIR</c>, each body's pyramid in <c>DIR/&lt;body&gt;/cube</c> (the Moon's:
/// <c>DIR/moon/cube</c>, built by <c>--cube-body moon</c>, coloured by <c>--globealbedo --body moon</c>). No path is
/// built in; #1656's delivery (the server, the local install, the shipped base) takes over from it.
/// </para>
///
/// <para>
/// <b>When a body is drawn as a globe</b>: always when the anchor is on it (its ground); from elsewhere within ten of its
/// radii (from the Earth the Moon stays the sky's <see cref="MoonDisc"/>, which steps aside while the globe draws it).
/// </para>
/// </summary>
public partial class BodyViews : Node
{
    /// <summary>Another body is drawn as a globe within this many of its radii.</summary>
    public const double NearRadii = 10;

    private readonly WorldOrigin _origin;
    private readonly Func<Player.FootPlayer?> _local;
    private readonly List<BodyGlobe> _globes = new();
    private BodyGlobe? _earth, _moon;

    /// <summary>The root of the bodies' data (<c>--bodies</c>), or null.</summary>
    public static string? DataRoot => CmdArgs.Value("--bodies");

    public BodyViews(WorldOrigin origin, Func<Player.FootPlayer?> local)
    {
        _origin = origin;
        _local = local;
        Name = "BodyViews";
    }

    public override void _Ready()
    {
        if (DataRoot is { } root)
            foreach (var body in CelestialBody.All)
            {
                if (body.Id == BodyId.Earth) continue;
                string dir = System.IO.Path.Combine(root, body.Name.ToLowerInvariant(), "cube");
                if (!GlobeFiles.HasManifest(dir)) continue;
                var g = new BodyGlobe(body, dir, _origin);
                AddChild(g);
                _globes.Add(g);
                if (body.Id == BodyId.Moon) _moon = g;
                // the ground under your feet where one can stand (#1850)
                if (body.Landable)
                {
                    g.Ground = new BodyGround(g, _origin);
                    AddChild(g.Ground);
                }
            }
        // the ground game's gravity follows the anchor's body: the walker's fall, and the physics space's
        _origin.BodyChanged += OnBodyChanged;
        OnBodyChanged(BodyId.Earth, _origin.Body);
        // the Earth seen from another body: its pyramid as the Earth's globe has it
        if (Globe.DataSource is { Count: > 0 } earthPyramid)
        {
            _earth = new BodyGlobe(CelestialBody.Earth, earthPyramid, earthPyramid.Describe(), _origin);
            AddChild(_earth);
            _globes.Add(_earth);
        }
        if (BodyShot.Requested) AddChild(new BodyShot(_origin, () => _globes.Find(g => g.Body.Id == _origin.Body)));
        if (MoonCheck.Requested) AddChild(new MoonCheck(_origin, _local, () => _moon?.Ground));
    }

    public override void _ExitTree()
    {
        _origin.BodyChanged -= OnBodyChanged;
        BodyGravity.On(BodyId.Earth);
    }

    private void OnBodyChanged(BodyId from, BodyId to)
    {
        BodyGravity.On(to);
        // the space's own gravity (props, wrecks, a ragdoll): the project's on the Earth, untouched there
        if (from == to && to == BodyId.Earth) return;
        float space = to == BodyId.Earth ? (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f) : BodyGravity.Ground;
        if (GetViewport()?.World3D is { } world)
            PhysicsServer3D.AreaSetParam(world.Space, PhysicsServer3D.AreaParameter.Gravity, space);
        if (from != to) GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[bodies] on {to}: the ground's gravity {BodyGravity.Ground:F2} m/s²"));
    }

    public override void _Process(double delta)
    {
        var anchor = _origin.Body;
        var cam = GetViewport()?.GetCamera3D();
        DVec3? eye = null;
        if (cam != null && _origin.Frame.Axes is { } axes)
        {
            var e = axes.ToEcef(cam.GlobalPosition.X, cam.GlobalPosition.Y, cam.GlobalPosition.Z);
            eye = new DVec3(e.X, e.Y, e.Z);
        }
        var sky = SkyClock.Now;
        SkyBodies.HiddenByGlobe.Clear();
        foreach (var g in _globes)
        {
            if (g == _earth) { g.Wanted = anchor != BodyId.Earth; continue; }
            bool near = g.Body.Id == anchor
                || eye is { } p && Frames.Convert(anchor, p, g.Body.Id, sky).Length < NearRadii * g.Body.Shape.A;
            g.Wanted = near;
            // the sky's disc of a body its globe draws steps aside (the Moon's own disc, a planet's in SkyBodies)
            if (g.Drawn && g.Body.Sky is { } skyBody) SkyBodies.HiddenByGlobe.Add(skyBody);
        }
        MoonDisc.HiddenByGlobe = _moon is { Drawn: true };
    }
}
