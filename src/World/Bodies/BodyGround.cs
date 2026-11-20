using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// The ground under your feet on another body (#1850, docs/plans/planets.md sections 4, 5 and 7), while the anchor is on
/// the body and the camera within <see cref="NearM"/> of it. Its heights are <see cref="BodyHeights"/>: the pyramid's
/// finest level (the Moon's LOLA, 167 m cells) and the generated craters down to 2.5 m (<see cref="CraterField"/>), the
/// same on every peer.
///
/// <para>
/// <b>Three squares</b>: the near one (512 m of 2 m cells, every crater, solid: what the walker stands on) inside a fine
/// one (#2016: 2 km of 4 m cells, the craters from 10 m, drawn only) inside a medium one (4 km of 16 m cells, the craters
/// from 40 m, drawn only), which reaches a walker's horizon (2.4 km on the Moon, 3.4 on Mars). Each eases at its edge into
/// what lies round it (the near into the fine's ground, the fine into the medium's, the medium into the globe's own
/// cells), and each leaves a hole in what lies under it (the fine under the near one, the medium under the fine, the
/// globe's tiles under the medium: <see cref="Hole"/>), so no coarser cell cuts through finer ground. The drawn squares
/// ease by the camera's distance (a geomorph in <c>body_ground.gdshader</c>), not their own edge's: one built again where
/// the camera is draws what the last one did, with no pop.
/// </para>
///
/// <para>
/// <b>Their cover</b> (#2016, <see cref="BodyCover"/>): per vertex the fresh craters' rays and the dune sand, which only the
/// squares have the relief for; the rest of the colour (mottling, dust, sulphur, polar ice) is the shader's, on the
/// squares and the globe's tiles alike (<see cref="CoverLook"/>).
/// </para>
///
/// <para>
/// <b>Built when it moves, never per frame</b>: on a worker once the camera is a square's move distance from its centre,
/// then swapped in whole (the new one first, so the walker never stands on nothing). Placed from doubles at each
/// re-anchor, as the globe's tiles are.
/// </para>
/// </summary>
public partial class BodyGround : Node, Terrain.IBodyGround
{
    /// <summary>Its meshes shown or hidden (#2051's <c>--skyordercheck</c>: its comparison pictures).</summary>
    internal void SetShown(bool shown)
    {
        foreach (var child in GetChildren())
            if (child is Node3D n) n.Visible = shown;
    }

    /// <summary>The near square's cells a side.</summary>
    public const int Cells = 256;

    /// <summary>A near cell's side, metres: the smallest crater (2.5 m) is a shape at it.</summary>
    public const float CellM = 2f;

    /// <summary>The near square moves when the camera is this far from its centre, metres.</summary>
    public const double MoveM = 96;

    /// <summary>The squares are built while the camera is under this, metres over the body's reference surface.</summary>
    public const double NearM = 25_000;

    /// <summary>The near square's last metres, eased into the medium ground (no step where they meet).</summary>
    public const double BlendM = 64;

    /// <summary>The fine square's cells (#2016): 512 of 4 m, 2 km, the craters from <see cref="FineCraterM"/>.</summary>
    public const int FineCells = 512;

    public const float FineCellM = 4f;

    /// <summary>The fine square moves when the camera is this far from its centre, metres.</summary>
    public const double FineMoveM = 256;

    /// <summary>The fine square's last metres, eased into the medium's ground (behind its geomorph: a camera far off its centre).</summary>
    public const double FineBlendM = 128;

    /// <summary>The fine square holds the craters from this diameter up (two and a half of its cells across), metres.</summary>
    public const double FineCraterM = 10;

    /// <summary>The fine square's geomorph into the medium's ground, metres from the camera: its craters 10-40 m fade out between.</summary>
    public const float FineMorphFrom = 512, FineMorphTo = 768;

    /// <summary>The medium square's cells: 256 of 16 m, 4 km, a walker's horizon and more.</summary>
    public const float MediumCellM = 16f;

    /// <summary>The medium square moves when the camera is this far from its centre, metres.</summary>
    public const double MediumMoveM = 512;

    /// <summary>The medium square's last metres, eased into the globe's own cells (behind its geomorph).</summary>
    public const double MediumBlendM = 256;

    /// <summary>The medium square's geomorph into the globe's own cells, metres from the camera.</summary>
    public const float MediumMorphFrom = 1024, MediumMorphTo = 1536;

    /// <summary>The smallest crater whose rays the near square draws (two and a half of its cells), metres.</summary>
    public const double NearRaysM = 5;

    /// <summary>The medium square holds the craters from this diameter up (two and a half of its cells across), metres.</summary>
    public const double MediumCraterM = 40;

    /// <summary>How far each way from the centre the near square holds the true ground (inside its eased band), metres.</summary>
    public const double TrueHalfM = Cells * CellM / 2 - BlendM;

    private static readonly StringName MorphFrom = "morph_from", MorphTo = "morph_to";

    private static readonly StringName AirTau = "air_tau", LightStrength = "light_strength", LightTint = "light_tint", LunarParam = "lunar";

    /// <summary>
    /// The earthshine's colour (#2024): the sunlight the Earth sends back, bluish (its oceans and air); its strength a share
    /// of the day's, <see cref="PlanetshineMin"/> to <see cref="PlanetshineMax"/> from a new to a full Earth.
    /// </summary>
    private static readonly Vector3 PlanetshineTint = new(0.72f, 0.82f, 1f);

    public const float PlanetshineMin = 0.03f, PlanetshineMax = 0.35f;

    /// <summary>The starlight on a night with no planet up (#2024: the Moon's far side), a share of the day's, and its colour.</summary>
    public const float Starlight = 0.06f;

    private static readonly Vector3 StarlightTint = new(0.8f, 0.85f, 1f);

    /// <summary>The scene's light on the ground this frame: how strong, what colour (<see cref="Light"/>).</summary>
    private (float Strength, Vector3 Tint) _light = (1f, Vector3.One);

    /// <summary>
    /// The scene's directional light as the ground takes it (#2024): the sun at the ground's exposure, the planet's light at
    /// night (the Moon's earthshine: bluish, a share of the day's by how much of the Earth is lit), the stars' faint light on
    /// a night with no planet up (the far side).
    /// </summary>
    public static (float Strength, Vector3 Tint) Light(DayNight.SceneLight light, float planetLit, float exposure) => light switch
    {
        DayNight.SceneLight.Sun => (exposure, Vector3.One),
        DayNight.SceneLight.Planet => (exposure * Mathf.Lerp(PlanetshineMin, PlanetshineMax, planetLit), PlanetshineTint),
        DayNight.SceneLight.Stars => (exposure * Starlight, StarlightTint),
        // the Earth's own night (a ground square under /goto Earth): the moonlight's shading, as dim as the far side's stars
        DayNight.SceneLight.Moon => (exposure * Starlight, StarlightTint),
        _ => (0f, Vector3.One),
    };
    private static readonly StringName SunDir = "sun_dir", AlbedoTex = "albedo_tex", SunStrength = "sun_strength",
        HoleCentre = "hole_centre", HoleUp = "hole_up", HoleEast = "hole_east", HoleHalf = "hole_half",
        LampPos = "lamp_pos", LampDir = "lamp_dir", LampCol = "lamp_col", LampCount = "lamp_count";

    // the lamps on the ground (#1942, GroundLamps): one set of arrays, rewritten on a change, a re-anchor or each second
    private static readonly Vector4[] LampPosArr = new Vector4[GroundLamps.Max], LampDirArr = new Vector4[GroundLamps.Max],
        LampColArr = new Vector4[GroundLamps.Max];

    public CelestialBody Body => _globe.Body;

    /// <summary>The ground's heights (the checks read them too); made again when the pyramid deepens (<see cref="Refresh"/>).</summary>
    public BodyHeights Heights { get; private set; }

    /// <summary>The level <see cref="Heights"/> reads (the chain's finest when they were made).</summary>
    public int HeightsLevel { get; private set; }

    // bumped as the heights are made again: a square of older heights is rebuilt where it stands
    private int _heightsVersion;

    /// <summary>The near square in place, with its frame (x east, y up, z south, its origin on the ground under its centre).</summary>
    public EnuFrame? Frame => _near.Frame;

    /// <summary>How many near squares were built; a check waits for a new one.</summary>
    public int Built => _near.Built;

    /// <summary>How many medium squares were built.</summary>
    public int MediumBuilt => _medium.Built;

    /// <summary>How many fine squares were built (#2016).</summary>
    public int FineBuilt => _fine.Built;

    /// <summary>Every square built so far, summed: a picture waits for it to stop changing (<see cref="BodyShot"/>).</summary>
    public int Settled => _near.Built + _fine.Built + _medium.Built;

    /// <summary>The cover shader's state for this body (#2016): the squares and the globe's own tiles take it.</summary>
    public BodyCoverLook CoverLook { get; }

    /// <summary>The anchor body's own (one a landable body with a pyramid; the anchor's claims it), for the checks and the step-in.</summary>
    public static BodyGround? Active { get; private set; }

    private readonly BodyGlobe _globe;
    private readonly WorldOrigin _origin;
    // lit, to take the scene's shadows (#2024): the globe's own shader is unshaded
    private readonly Shader _shader = GD.Load<Shader>("res://shaders/body_ground.gdshader");
    private readonly Layer _near, _fine, _medium;

    /// <summary>
    /// One square: its size, how it moves and eases (statically at its edge, and by the camera's distance from
    /// <paramref name="morphFrom"/> to <paramref name="morphTo"/>, 0: none), its craters' smallest, its rays' smallest and the
    /// rays of the ground round it (0: none round it), and what is in place.
    /// </summary>
    private sealed class Layer(string name, int cells, float cellM, double moveM, double blendM, float skirtM, bool solid, double smallestCrater,
        double raysFrom, double edgeRaysFrom, float morphFrom, float morphTo)
    {
        public readonly string Name = name;
        public readonly int Cells = cells;
        public readonly float CellM = cellM, SkirtM = skirtM, MorphFrom = morphFrom, MorphTo = morphTo;
        public readonly double MoveM = moveM, BlendM = blendM, SmallestCrater = smallestCrater, RaysFrom = raysFrom, EdgeRaysFrom = edgeRaysFrom;
        public readonly bool Solid = solid;
        public int CoverVersion = -1;
        public StaticBody3D? Node;
        public ShaderMaterial? Material;
        public (float Strength, Vector3 Tint) LightWritten = (-1f, Vector3.Zero);
        public int LampsVersion = -1, LampsEpoch = -1;
        public double LampsAge;
        public EnuFrame? Frame;
        public Task<Square>? Task;
        public int Epoch = -1, Built, HeightsVersion;
        public (Vector3 Centre, Vector3 Up, Vector3 East, float Half) HoleWritten;

        public float Half => Cells * CellM / 2;
    }

    private sealed record Square(EnuFrame Frame, float[] Y, Godot.Collections.Array Arrays, Image? Colour);

    public BodyGround(BodyGlobe globe, WorldOrigin origin)
    {
        _globe = globe;
        _origin = origin;
        Name = $"{globe.Body.Name}Ground";
        Heights = MakeHeights(globe);
        CoverLook = new BodyCoverLook(globe.Body);
        _near = new Layer("Square", Cells, CellM, MoveM, BlendM, 30f, solid: true, smallestCrater: 0,
            raysFrom: NearRaysM, edgeRaysFrom: FineCraterM, 0, 0);
        _fine = new Layer("Fine", FineCells, FineCellM, FineMoveM, FineBlendM, 60f, solid: false, smallestCrater: FineCraterM,
            raysFrom: FineCraterM, edgeRaysFrom: MediumCraterM, FineMorphFrom, FineMorphTo);
        _medium = new Layer("Medium", Cells, MediumCellM, MediumMoveM, MediumBlendM, 200f, solid: false, smallestCrater: MediumCraterM,
            raysFrom: MediumCraterM, edgeRaysFrom: 0, MediumMorphFrom, MediumMorphTo);
    }

    /// <summary>
    /// The chain deepened (the server's pyramid for this body came, #1849): the heights are made again at its new finest
    /// level, and each square rebuilt where it stands (the old one stays under the walker until the new one is in).
    /// </summary>
    public void Refresh()
    {
        if (_globe.MaxLevel == HeightsLevel) return;
        Heights = MakeHeights(_globe);
        _heightsVersion++;
        GD.Print($"[bodies] {Body.Name}'s ground now reads level {HeightsLevel}");
    }

    /// <summary>
    /// The heights at the chain's finest level. A tile missing there (a streamed one that did not come, past the data) is its
    /// deepest ancestor's, resampled (<see cref="GlobeGround.FillFromAncestors"/>): never the reference surface's 0, a step
    /// of kilometres where the data stands high or low (the shipped base alone reaches level 2).
    /// </summary>
    private BodyHeights MakeHeights(BodyGlobe globe)
    {
        HeightsLevel = globe.MaxLevel;
        return HeightsOf(globe.Body, globe.Pyramid, globe.MaxLevel);
    }

    /// <summary>
    /// A body's heights at a point, the same wherever they are asked (#1848, #1852: the clients' ground and the server's,
    /// <c>ServerBodyGround</c>): the chain's <paramref name="level"/> (a missing tile from its ancestor; none at all: the
    /// generated relief alone, on its datum), its craters, the Moon's sites and colonies, its relief family.
    /// </summary>
    public static BodyHeights HeightsOf(CelestialBody body, Terrain.Leaves.PyramidSource? pyramid, int level)
    {
        var cube = body.Cube;
        return new BodyHeights(cube, level,
            key => pyramid == null ? null : pyramid.Heights(key) ?? FromAncestor(pyramid, key),
            body.CraterDensity > 0 ? new CraterField((ulong)body.Id, cube.Shape.A, body.CraterDensity) : null,
            // the Moon's sites (#1939): craters calmed under the landers, the S-IVBs' fresh craters; the colonies' plateaus (#1940)
            body.Id == BodyId.Moon ? new SurfaceStamps(MoonSites.Stamps().Concat(MoonColonies.Stamps()), cube.Shape.A) : null,
            // the body's relief family under its data (#1853, #1854): dunes, ridges, grooves, lava flows
            SurfaceProfile.For(body.Id, cube.Shape.A));
    }

    /// <summary>A tile the chain lacks, from its deepest ancestor (65², bilinear); null where it has none at all.</summary>
    private static CubeTile? FromAncestor(Terrain.Leaves.PyramidSource pyramid, CubeKey key)
    {
        const int bits = 6;
        int n = (1 << bits) + 1;
        var hs = new float[n * n];
        System.Array.Fill(hs, float.NaN);
        GlobeGround.FillFromAncestors(hs, n, key, bits, 1, pyramid.Heights);
        return System.Array.TrueForAll(hs, float.IsNaN) ? null : new CubeTile { Key = key, Bits = bits, Heights = hs };
    }

    /// <summary>This body's ground is the one everything asks while the anchor is on it.</summary>
    private void Claim()
    {
        if (Active == this) return;
        Active = this;
        Terrain.Earth.EarthWorld.OtherBody = this;
    }

    public override void _ExitTree()
    {
        if (Active == this) Active = null;
        if (Terrain.Earth.EarthWorld.OtherBody == this) Terrain.Earth.EarthWorld.OtherBody = null;
    }

    /// <summary>The ground's world Y under a world point the near square covers: the ground there, along the anchor's up.</summary>
    public bool TryGetHeight(Vector3 worldPos, out float height)
    {
        height = 0f;
        if (!Covers(worldPos) || _origin.Frame.Axes is not { } axes) return false;
        var n = Heights.NormalUnder(axes.ToEcef(worldPos.X, worldPos.Y, worldPos.Z));
        var (_, y, _) = axes.FromEcef(Heights.Ground(n));
        height = (float)y;
        return true;
    }

    /// <summary>The near square is solid wherever it covers.</summary>
    public bool HasCollisionAt(Vector3 worldPos) => Covers(worldPos);

    /// <summary>
    /// Where the globe's own tiles step aside, in world space: the outer square in place (the medium, or the near one
    /// before it), its centre, up, east and half side; null while there is none.
    /// </summary>
    public (Vector3 Centre, Vector3 Up, Vector3 East, float Half)? Hole =>
        _origin.Body != Body.Id ? null : HoleOf(_medium) ?? HoleOf(_fine) ?? HoleOf(_near);

    private static (Vector3 Centre, Vector3 Up, Vector3 East, float Half)? HoleOf(Layer l) =>
        l.Node is { } s ? (s.GlobalPosition, s.GlobalBasis.Y.Normalized(), s.GlobalBasis.X.Normalized(), l.Half - 0.25f) : null;

    /// <summary>
    /// The near square is in place on the anchor's body and a world point stands over its true ground (within
    /// <see cref="TrueHalfM"/> each way of its centre, twice <see cref="MoveM"/>: the next square is there before the
    /// walker is past it).
    /// </summary>
    public bool Covers(Vector3 world)
    {
        if (_near.Node == null || _near.Frame is not { } f || _origin.Body != Body.Id || _origin.Frame.Axes is not { } axes) return false;
        var (x, _, z) = f.ToLocal(axes.ToEcef(world.X, world.Y, world.Z));
        return Math.Abs(x) < TrueHalfM && Math.Abs(z) < TrueHalfM;
    }

    public override void _Process(double delta)
    {
        Finish(_near);
        Finish(_fine);
        Finish(_medium);
        if (_origin.Body != Body.Id || _origin.Frame.Axes is not { } axes)
        {
            Drop(_near);
            Drop(_fine);
            Drop(_medium);
            _globe.SetGroundShare(1f);
            CoverLook.Update(null, 0, default, default);
            return;
        }
        Claim();
        // the light on the ground this frame (#2024): the exposure grey near it, the sun or the planet's light
        var eye = GetViewport()?.GetCamera3D() is { } c ? (double?)_origin.AltitudeOf(c.GlobalPosition) : null;
        float share = BodyGlobe.GroundShareAt(eye ?? 0);
        _globe.SetGroundShare(share);
        var dn = DayNight.Instance;
        _light = Light(dn?.Light ?? DayNight.SceneLight.Sun, dn?.PlanetLit ?? 0f, _globe.Exposure * share);
        // the cover's body-fixed noise and the season's ice edges (#2016), from the camera
        if (GetViewport()?.GetCamera3D() is { } coverCam) CoverLook.Update(axes, _origin.Epoch, coverCam.GlobalPosition, _globe.Sun);
        Keep(_near, axes);
        Keep(_fine, axes);
        Keep(_medium, axes);
        // each drawn square steps aside under the one inside it: the fine under the near, the medium under the fine (or
        // the near before the fine is in); its hole follows that square (moved, or shifted)
        StepAside(_fine, HoleOf(_near));
        StepAside(_medium, HoleOf(_fine) ?? HoleOf(_near));

        var cam = GetViewport()?.GetCamera3D();
        if (cam == null) return;
        var at = cam.GlobalPosition;
        if (_origin.AltitudeOf(at) > NearM) return;
        var p = axes.ToEcef(at.X, at.Y, at.Z);
        var heights = Heights;
        var globe = _globe;
        // the near square first (what the walker stands on), its edge on the fine's ground; the fine once the near one is
        // in, its edge on the medium's; the medium once the fine is in, its edge on the globe's own cells
        if (Wants(_near, p))
        {
            var near = _near;
            near.HeightsVersion = _heightsVersion;
            near.Task = Task.Run(() => Build(heights, globe, p, near, n => heights.At(n, FineCraterM)));
        }
        else if (_near.Node != null && Wants(_fine, p))
        {
            var fine = _fine;
            fine.HeightsVersion = _heightsVersion;
            fine.Task = Task.Run(() => Build(heights, globe, p, fine, n => heights.At(n, MediumCraterM)));
        }
        else if (_fine.Node != null && Wants(_medium, p))
        {
            var mediumLayer = _medium;
            mediumLayer.HeightsVersion = _heightsVersion;
            mediumLayer.Task = Task.Run(() => Build(heights, globe, p, mediumLayer, n => heights.Coarse(n, BodyGlobe.Stride, ofFullLattice: true)));
        }
    }

    /// <summary>A drawn square's hole over the square inside it, written when that one moves (or shifts).</summary>
    private static void StepAside(Layer l, (Vector3 Centre, Vector3 Up, Vector3 East, float Half)? inside)
    {
        if (l.Material is not { } m) return;
        var hole = inside ?? default;
        if (hole == l.HoleWritten) return;
        l.HoleWritten = hole;
        m.SetShaderParameter(HoleCentre, hole.Centre);
        m.SetShaderParameter(HoleUp, hole.Up);
        m.SetShaderParameter(HoleEast, hole.East);
        m.SetShaderParameter(HoleHalf, hole.Half);
    }

    /// <summary>A square in place: placed again at a re-anchor, lit by the body's sun.</summary>
    private void Keep(Layer l, FrameAxes axes)
    {
        if (l.Node != null && _origin.Epoch != l.Epoch) Place(l, axes);
        // the light: the sun at the ground's exposure, or the planet's at night (#2024), written on a change. The globe's
        // exposure comes once its six faces' colour is in: a square made before kept the shader's default (Phobos' near
        // square at 1.6 in a globe at 0.9, a bright patch, #1853)
        if (l.Material != null && l.LightWritten != _light)
        {
            l.LightWritten = _light;
            l.Material.SetShaderParameter(LightStrength, _light.Strength);
            l.Material.SetShaderParameter(LightTint, _light.Tint);
        }
        if (l.Material != null) WriteLamps(l);
        if (l.Material != null && l.CoverVersion != CoverLook.Version)
        {
            l.CoverVersion = CoverLook.Version;
            CoverLook.Write(l.Material);
        }
    }

    /// <summary>The nearest lamps into a square's shader (#1942): on a change of the set, a re-anchor, or once a second.</summary>
    private void WriteLamps(Layer l)
    {
        l.LampsAge += GetProcessDeltaTime();
        if (l.LampsVersion == GroundLamps.Version && l.LampsEpoch == _origin.Epoch && (l.LampsAge < 1 || GroundLamps.All.Count == 0)) return;
        l.LampsVersion = GroundLamps.Version;
        l.LampsEpoch = _origin.Epoch;
        l.LampsAge = 0;
        var eye = GetViewport()?.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
        int n = GroundLamps.Fill(eye, LampPosArr, LampDirArr, LampColArr);
        l.Material!.SetShaderParameter(LampCount, n);
        if (n == 0) return;
        l.Material.SetShaderParameter(LampPos, LampPosArr);
        l.Material.SetShaderParameter(LampDir, LampDirArr);
        l.Material.SetShaderParameter(LampCol, LampColArr);
    }

    /// <summary>A layer needs a new square round <paramref name="p"/>: none yet, or the camera past its move distance.</summary>
    private bool Wants(Layer l, EcefPos p)
    {
        if (l.Task != null) return false;
        if (l.Frame is not { } f || l.HeightsVersion != _heightsVersion) return true;
        var (x, _, z) = f.ToLocal(p);
        return x * x + z * z >= l.MoveM * l.MoveM;
    }

    private void Finish(Layer l)
    {
        if (l.Task is not { IsCompleted: true } done) return;
        // a square waits for the globe's exposure: put in at the provisional one, it dimmed with the globe a moment later (#1983)
        if (!_globe.ExposureSettled) return;
        l.Task = null;
        if (done.IsCompletedSuccessfully) Commit(l, done.Result);
        else GD.PrintErr($"[bodies] {Body.Name}'s {l.Name.ToLowerInvariant()} ground: {done.Exception?.GetBaseException().Message}");
    }

    /// <summary>A square round <paramref name="near"/>: the heights, the mesh and the colour under it; on a worker.</summary>
    private static Square Build(BodyHeights heights, BodyGlobe globe, EcefPos near, Layer l, Func<EcefPos, double> edge)
    {
        int cells = l.Cells;
        float cellM = l.CellM;
        int n = cells + 1;
        // a drawn square also keeps the ground round it at every vertex: its geomorph's target (#2016)
        var edgeY = l.MorphTo > 0 ? new float[n * n] : null;
        var (frame, y) = heights.Grid(near, cells, cellM, l.BlendM, edge, l.SmallestCrater, edgeY);
        var pos = new Vector3[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                pos[j * n + i] = new Vector3((i - cells / 2) * cellM, y[j * n + i], (j - cells / 2) * cellM);
        Vector3[]? edgePos = null;
        if (edgeY != null)
        {
            edgePos = new Vector3[n * n];
            for (int k = 0; k < n * n; k++) edgePos[k] = pos[k] with { Y = edgeY[k] };
        }
        var cover = Cover(heights, globe.Body, frame, l);

        // the colour: the finest coloured tile over the centre that holds the whole square (on a moon of a few km, #1853,
        // the finest tile is smaller than the 4 km square, whose edges read past it), its UVs from each vertex's place on
        // that tile; where none holds it (across a face's edge), the finest over the centre
        var cube = heights.Cube;
        Image? colour = null;
        CubeKey? colourKey = null;
        var n0 = heights.NormalUnder(frame.Origin);
        double half = cells / 2 * cellM;
        var corners = new EcefPos[4];
        for (int c = 0; c < 4; c++) corners[c] = heights.NormalUnder(frame.ToEcef((c & 1) == 0 ? -half : half, 0, (c & 2) == 0 ? -half : half));
        bool Holds(CubeKey k)
        {
            double side = 1 << k.Level;
            foreach (var pc in corners)
            {
                var (fu, fv) = cube.OnFace(k.Face, pc);
                double tu = (fu + 1) * 0.5 * side - k.X, tv = (fv + 1) * 0.5 * side - k.Y;
                if (!(tu >= 0 && tu <= 1 && tv >= 0 && tv <= 1)) return false;
            }
            return true;
        }
        for (int pass = 0; pass < 2 && colourKey == null; pass++)
        for (int level = globe.MaxLevel; level >= 0 && colourKey == null; level--)
        {
            var (face, u, v) = cube.FromNormal(n0);
            var k = CubeKey.FromFace(face, u, v, level);
            if (pass == 0 && !Holds(k)) continue;
            if (!globe.Pyramid.HasColour(k) || globe.Pyramid.Colour(k, 1) is not { } webp) continue;
            colour = new Image();
            if (colour.LoadWebpFromBuffer(webp) != Error.Ok) { colour = null; continue; }
            colour.GenerateMipmaps();
            colourKey = k;
        }
        var uvs = new Vector2[n * n];
        if (colourKey is { } ck)
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var pn = heights.NormalUnder(frame.ToEcef((i - cells / 2) * cellM, 0, (j - cells / 2) * cellM));
                    var (u, v) = cube.OnFace(ck.Face, pn);
                    double side = 1 << ck.Level;
                    uvs[j * n + i] = new Vector2((float)((u + 1) * 0.5 * side - ck.X), (float)((v + 1) * 0.5 * side - ck.Y));
                }
        return new Square(frame, y, Mesh(pos, uvs, cells, l.SkirtM, edgePos, cover), colour);
    }

    /// <summary>
    /// A square's cover per vertex (#2016): its own rays and sand (eased at its edge into the ground round it, as its
    /// heights are), and the ground round it's (the geomorph's target): rays of the craters it has, sand only where the
    /// ground round it is a square too. On the worker.
    /// </summary>
    private static Color[] Cover(BodyHeights heights, CelestialBody body, EnuFrame frame, Layer l)
    {
        int cells = l.Cells, n = cells + 1;
        var cover = new Color[n * n];
        if (BodyCover.For(body.Id) == BodyCover.Kind.None) return cover;
        double half = cells / 2.0 * l.CellM, radius = heights.Cube.Shape.A;
        var region = new BodyCover.Region(heights.Craters, heights.Profile, heights.NormalUnder(frame.Origin), half * 1.5, l.RaysFrom);
        bool edgeIsSquare = l.EdgeRaysFrom > 0;
        System.Threading.Tasks.Parallel.For(0, n, j =>
        {
            double z = (j - cells / 2.0) * l.CellM;
            for (int i = 0; i < n; i++)
            {
                double x = (i - cells / 2.0) * l.CellM;
                var nn = heights.NormalUnder(frame.ToEcef(x, 0, z));
                var own = region.At(nn, radius);
                var round = edgeIsSquare ? region.At(nn, radius, l.EdgeRaysFrom) : default;
                double toEdge = half - Math.Max(Math.Abs(x), Math.Abs(z));
                float t = 0;
                if (l.BlendM > 0 && toEdge < l.BlendM)
                {
                    t = (float)(1 - toEdge / l.BlendM);
                    t = t * t * (3 - 2 * t);
                }
                cover[j * n + i] = new Color(Mathf.Lerp(own.Rays, round.Rays, t), Mathf.Lerp(own.Sand, round.Sand, t), round.Rays, round.Sand);
            }
        });
        return cover;
    }

    /// <summary>A grid's normals (up, whichever way its triangles wind).</summary>
    private static Vector3[] Normals(Vector3[] pos, int m)
    {
        int n = m + 1;
        var normals = new Vector3[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var dx = pos[j * n + System.Math.Min(i + 1, m)] - pos[j * n + System.Math.Max(i - 1, 0)];
                var dz = pos[System.Math.Min(j + 1, m) * n + i] - pos[System.Math.Max(j - 1, 0) * n + i];
                var nn = dz.Cross(dx);
                normals[j * n + i] = nn.LengthSquared() > 0 ? nn.Normalized() : Vector3.Up;
                if (normals[j * n + i].Y < 0) normals[j * n + i] = -normals[j * n + i];
            }
        return normals;
    }

    /// <summary>
    /// A square's mesh: the grid, its normals, and a skirt round it (no gap against lower ground round it); its cover per
    /// vertex (COLOR), and for a drawn square the ground round it (#2016: UV2.x its height less the square's, CUSTOM0 its
    /// normal), which the shader's geomorph turns it into by the camera's distance.
    /// </summary>
    private static Godot.Collections.Array Mesh(Vector3[] pos, Vector2[] uv, int m, float skirtM, Vector3[]? edgePos, Color[] cover)
    {
        int n = m + 1;
        var normals = Normals(pos, m);
        var edgeNormals = edgePos != null ? Normals(edgePos, m) : normals;
        var edge = new System.Collections.Generic.List<int>(4 * m);
        for (int i = 0; i < m; i++) edge.Add(i);
        for (int j = 0; j < m; j++) edge.Add(j * n + m);
        for (int i = m; i > 0; i--) edge.Add(m * n + i);
        for (int j = m; j > 0; j--) edge.Add(j * n);
        int total = n * n + edge.Count;
        var verts = new Vector3[total];
        var norms = new Vector3[total];
        var uvs = new Vector2[total];
        var uv2 = new Vector2[total];
        var custom = new float[total * 4];
        var colours = new Color[total];
        System.Array.Copy(pos, verts, n * n);
        System.Array.Copy(normals, norms, n * n);
        System.Array.Copy(uv, uvs, n * n);
        System.Array.Copy(cover, colours, n * n);
        for (int k = 0; k < n * n; k++)
        {
            uv2[k] = new Vector2(edgePos != null ? edgePos[k].Y - pos[k].Y : 0, 0);
            custom[k * 4] = edgeNormals[k].X; custom[k * 4 + 1] = edgeNormals[k].Y; custom[k * 4 + 2] = edgeNormals[k].Z;
        }
        for (int e = 0; e < edge.Count; e++)
        {
            int v = edge[e];
            int k = n * n + e;
            verts[k] = pos[v] - Vector3.Up * skirtM;
            norms[k] = normals[v];
            uvs[k] = uv[v];
            uv2[k] = uv2[v];
            colours[k] = cover[v];
            custom[k * 4] = custom[v * 4]; custom[k * 4 + 1] = custom[v * 4 + 1]; custom[k * 4 + 2] = custom[v * 4 + 2];
        }
        var idx = new int[m * m * 6 + edge.Count * 6];
        int w = 0;
        for (int j = 0; j < m; j++)
            for (int i = 0; i < m; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                idx[w++] = a; idx[w++] = c; idx[w++] = b;
                idx[w++] = b; idx[w++] = c; idx[w++] = d;
            }
        for (int e = 0; e < edge.Count; e++)
        {
            int top0 = edge[e], top1 = edge[(e + 1) % edge.Count];
            int bot0 = n * n + e, bot1 = n * n + (e + 1) % edge.Count;
            idx[w++] = top0; idx[w++] = bot0; idx[w++] = top1;
            idx[w++] = top1; idx[w++] = bot0; idx[w++] = bot1;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Godot.Mesh.ArrayType.Normal] = norms;
        arrays[(int)Godot.Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Godot.Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Godot.Mesh.ArrayType.Color] = colours;
        arrays[(int)Godot.Mesh.ArrayType.Custom0] = custom;
        arrays[(int)Godot.Mesh.ArrayType.Index] = idx;
        return arrays;
    }

    /// <summary>Main thread: the new square in, then the old one out.</summary>
    private void Commit(Layer l, Square sq)
    {
        if (_origin.Body != Body.Id || _origin.Frame.Axes is not { } axes) return;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, sq.Arrays, flags:
            (Godot.Mesh.ArrayFormat)((long)Godot.Mesh.ArrayCustomFormat.RgbaFloat << (int)Godot.Mesh.ArrayFormat.FormatCustom0Shift));
        var material = new ShaderMaterial { Shader = _shader };
        // the geomorph by the camera's distance (#2016): the drawn squares only
        material.SetShaderParameter(MorphFrom, l.MorphFrom);
        material.SetShaderParameter(MorphTo, l.MorphTo);
        CoverLook.Write(material);
        l.CoverVersion = CoverLook.Version;
        if (sq.Colour != null) material.SetShaderParameter(AlbedoTex, ImageTexture.CreateFromImage(sq.Colour));
        material.SetShaderParameter(LightStrength, _light.Strength);
        material.SetShaderParameter(LightTint, _light.Tint);
        material.SetShaderParameter(AirTau, BodyGlobe.AirTauOf(_globe.Body));
        // the regolith's own light on an airless body's ground (#2024): lunar-Lambert
        material.SetShaderParameter(LunarParam, _globe.LunarAt(BodyGlobe.GroundGrey));
        var body = new StaticBody3D { Name = l.Name };
        if (l.Solid)
        {
            int side = l.Cells + 1;
            body.AddChild(new CollisionShape3D
            {
                Shape = new HeightMapShape3D { MapWidth = side, MapDepth = side, MapData = sq.Y },
                // a height map's cells are one unit, centred: scaled to the grid, centred as the grid is
                Scale = new Vector3(l.CellM, 1f, l.CellM),
            });
        }
        else
        {
            // drawn only: nothing collides with it (the near square is the solid ground)
            body.CollisionLayer = 0;
            body.CollisionMask = 0;
        }
        body.AddChild(new MeshInstance3D
        {
            Name = "Mesh", Mesh = mesh, MaterialOverride = material,
            // the near square casts (#2024): crater rims and slopes shade the ground beyond them, within the sun's shadow
            // distance; the medium square, kilometres out, no
            CastShadow = l.Solid ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            // its box is round its centre: ground all round a camera inside it is never culled
            ExtraCullMargin = l.Half,
        });
        var old = l.Node;
        l.Node = body;
        l.Material = material;
        l.Frame = sq.Frame;
        l.HoleWritten = default;
        AddChild(body);
        Place(l, axes);
        old?.QueueFree();
        l.Built++;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[bodies] {Body.Name}'s {l.Name.ToLowerInvariant()} ground: {l.Built} at {Body.Shape.ToGeodetic(sq.Frame.Origin).H:F0} m over the reference, {l.Cells * l.CellM:F0} m a side"));
    }

    /// <summary>A square from its frame into world space, in doubles from the anchor (once a re-anchor).</summary>
    private void Place(Layer l, FrameAxes axes)
    {
        if (l.Node == null || l.Frame is not { } f) return;
        l.Epoch = _origin.Epoch;
        Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
        var (ox, oy, oz) = axes.FromEcef(f.Origin);
        var south = new EcefPos(-f.North.X, -f.North.Y, -f.North.Z);
        l.Node.GlobalTransform = new Transform3D(new Basis(Turn(f.East), Turn(f.Up), Turn(south)), new Vector3((float)ox, (float)oy, (float)oz));
    }

    private static void Drop(Layer l)
    {
        l.Node?.QueueFree();
        l.Node = null;
        l.Material = null;
        l.Frame = null;
    }
}
