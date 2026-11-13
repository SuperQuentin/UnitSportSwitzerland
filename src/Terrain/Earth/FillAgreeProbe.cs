using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// <c>--fillagree E,N</c> (#1682, <c>tools/fillagreenetcheck.sh</c>): the ground's ellipsoidal height at a 5 × 5 grid of
/// Earth positions 150 m apart round a point, read off the loaded leaves (<see cref="EarthWorld.EllipsoidalHeightAt"/>, no
/// frame in it), printed by a client and by the server once every point has ground. The script compares the two (the
/// client stays, its body holding the server's leaves, until the script stops it): they
/// stand on the same fill leaves when every point agrees to the centimetre. Each line says whether the point's leaf is
/// the region's or the fill's.
/// </summary>
public partial class FillAgreeProbe : Node
{
    public const int Side = 5;
    public const double StepM = 150;
    private const double TimeoutS = 240;

    public static (double E, double N)? Point => SpawnPoint.ParseLv95("--fillagree");

    private readonly EarthWorld _earth;
    private readonly bool _server;
    private readonly EcefPos[] _points;
    private double _since, _poll;
    private bool _done;

    public FillAgreeProbe(EarthWorld earth, bool server, double e, double n)
    {
        Name = "FillAgreeProbe";
        _earth = earth;
        _server = server;
        var centre = GlobalPos.FromLv95(e, n, 0);
        _points = new EcefPos[Side * Side];
        for (int j = 0; j < Side; j++)
            for (int i = 0; i < Side; i++)
                _points[j * Side + i] = centre.Moved((i - Side / 2) * StepM, (j - Side / 2) * StepM).ToEcef();
    }

    private string Role => _server ? "server" : "client";

    public override void _Process(double delta)
    {
        if (_done) return;
        _since += delta;
        if ((_poll += delta) < 0.2) return;
        _poll = 0;
        var heights = new double[_points.Length];
        for (int k = 0; k < _points.Length; k++)
        {
            // the fill's leaves stand round a body: on the server, round the client's player
            if (_earth.EllipsoidalHeightAt(_points[k], out heights[k])) continue;
            if (_since < TimeoutS) return;
            _done = true;
            GD.Print($"[fillagree {Role}] RESULT: FAILED (point {k} has no ground after {TimeoutS:F0} s; {_earth.Status})");
            return;
        }
        _done = true;
        int fill = 0;
        for (int k = 0; k < _points.Length; k++)
        {
            bool real = _earth.Leaves.IsReal(CubeKey.FromEcef(_points[k], LeafFrames.LeafLevel));
            if (!real) fill++;
            GD.Print(FormattableString.Invariant($"[fillagree {Role}] point {k} {(real ? "real" : "fill")} {heights[k]:F3}"));
        }
        GD.Print($"[fillagree {Role}] {fill} of {_points.Length} points on the fill's leaves, after {_since:F1} s");
    }
}
