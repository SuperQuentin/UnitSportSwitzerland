using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// The cover shader's uniforms for one body (#2016, <c>shaders/common/body_cover.gdshaderinc</c>): its family, the body's
/// axes and a lattice point near the camera in world space (so its noise is body-fixed and precise), its centre and pole,
/// and the polar ice's edges by the season (<see cref="BodyCover.IceEdges"/>). Worked out each frame by the anchor body's
/// <see cref="BodyGround"/>, written to the materials only when it changes (<see cref="Version"/>): the camera crossing a
/// 4096 m lattice cell, a re-anchor, the sun's latitude moving a tenth of a degree.
/// </summary>
public sealed class BodyCoverLook
{
    /// <summary>The noise's period, metres (the shader's <c>COVER_PERIOD</c>).</summary>
    public const double Period = 4096;

    private static readonly StringName KindParam = "cover_kind", Origin = "cover_origin", BasisParam = "cover_basis",
        Centre = "cover_centre", Pole = "cover_pole", Edges = "ice_edges", Radius = "cover_radius";

    private readonly CelestialBody _body;
    private readonly BodyCover.Kind _kind;
    private (long, long, long) _cell = (long.MinValue, 0, 0);
    private int _epoch = int.MinValue;
    private double _sunLat = double.NaN;
    private Vector3 _origin, _centre, _pole;
    private Basis _basis = Basis.Identity;
    private Vector2 _edges = new(90, 90);

    /// <summary>Bumped on every change: a material written at an older one is written again.</summary>
    public int Version { get; private set; }

    /// <summary>Whether the cover shows now (the anchor on the body, its axes known).</summary>
    public bool On { get; private set; }

    public BodyCover.Kind Kind => _kind;

    public BodyCoverLook(CelestialBody body)
    {
        _body = body;
        _kind = BodyCover.For(body.Id);
    }

    /// <summary>
    /// This frame's state from the world's axes, the camera (world) and the sun's direction (world, unit; zero: unknown).
    /// Off (<paramref name="axes"/> null): the anchor is elsewhere.
    /// </summary>
    public void Update(FrameAxes? axes, int epoch, Vector3 camera, Vector3 sun)
    {
        bool on = axes != null && _kind != BodyCover.Kind.None;
        if (on != On) { On = on; Version++; }
        if (!on) return;
        var a = axes!;
        var e = a.ToEcef(camera.X, camera.Y, camera.Z);
        var cell = ((long)System.Math.Floor(e.X / Period), (long)System.Math.Floor(e.Y / Period), (long)System.Math.Floor(e.Z / Period));
        bool moved = cell != _cell || epoch != _epoch;
        if (moved)
        {
            _cell = cell;
            _epoch = epoch;
            var (ox, oy, oz) = a.FromEcef(new EcefPos(cell.Item1 * Period, cell.Item2 * Period, cell.Item3 * Period));
            _origin = new Vector3((float)ox, (float)oy, (float)oz);
            var (cx, cy, cz) = a.FromEcef(new EcefPos(0, 0, 0));
            _centre = new Vector3((float)cx, (float)cy, (float)cz);
            // world to body-fixed: the world's axes in the body's frame are the columns
            _basis = new Basis(V(a.X), V(a.Y), V(a.Z));
            _pole = new Vector3((float)a.X.Z, (float)a.Y.Z, (float)a.Z.Z).Normalized();
        }
        // the season: the sun's latitude over the body
        double sunLat = sun == Vector3.Zero ? 0 : Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(sun.Dot(_pole), -1f, 1f)));
        bool season = double.IsNaN(_sunLat) || System.Math.Abs(sunLat - _sunLat) > 0.1;
        if (season)
        {
            _sunLat = sunLat;
            var (n, s) = BodyCover.IceEdges(_body.Id, sunLat);
            _edges = new Vector2((float)n, (float)s);
        }
        if (moved || season) Version++;
    }

    private static Vector3 V(EcefPos p) => new((float)p.X, (float)p.Y, (float)p.Z);

    /// <summary>The state into a material (the near ground's squares, the globe's tiles of the body).</summary>
    public void Write(ShaderMaterial m)
    {
        m.SetShaderParameter(KindParam, On ? (int)_kind : 0);
        if (!On) return;
        m.SetShaderParameter(Origin, _origin);
        m.SetShaderParameter(BasisParam, _basis);
        m.SetShaderParameter(Centre, _centre);
        m.SetShaderParameter(Pole, _pole);
        m.SetShaderParameter(Edges, _edges);
        m.SetShaderParameter(Radius, (float)_body.Shape.A);
    }
}
