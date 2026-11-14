using System.Buffers.Binary;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A body's equipotential surface as a radius grid (#1853, H7e of docs/plans/planets.md section 4): Mars' MOLA areoid
/// (MEGDR <c>mega90n000eb.img</c>, 16 px/°, big-endian int16 metres over 3,396,000 m, east longitudes 0-360 from the
/// north row down, pixel centres at half steps). MOLA's topography is measured from it; the game's heights are over the
/// body's IAU ellipsoid, so a height is the topography plus <see cref="Correction"/>. Smooth (its 1.8 m RMS is the fit's,
/// its relief is kilometres over thousands): 16 px/° is exact enough for any level.
/// </summary>
public sealed class Areoid
{
    private readonly short[] _r;
    private readonly int _w, _h;
    private readonly double _offset, _perDeg;

    public Areoid(short[] radii, int width, int height, double offset)
    {
        _r = radii;
        _w = width;
        _h = height;
        _offset = offset;
        _perDeg = width / 360.0;
    }

    /// <summary>A PDS MEGDR areoid file: <paramref name="width"/> × <paramref name="height"/> big-endian int16, offset added.</summary>
    public static Areoid Load(string path, int width = 5760, int height = 2880, double offset = 3_396_000)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != width * height * 2) throw new InvalidDataException($"{path}: {bytes.Length} bytes, not {width} x {height} int16");
        var r = new short[width * height];
        for (int k = 0; k < r.Length; k++) r[k] = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(2 * k, 2));
        return new Areoid(r, width, height, offset);
    }

    /// <summary>The areoid's radius at a planetocentric latitude and longitude (degrees, east), metres: bilinear, longitudes wrapped.</summary>
    public double Radius(double latDeg, double lonDeg)
    {
        double y = (90 - latDeg) * _perDeg - 0.5;
        double x = ((lonDeg % 360 + 360) % 360) * _perDeg - 0.5;
        y = Math.Clamp(y, 0, _h - 1);
        int j0 = (int)Math.Floor(y), j1 = Math.Min(j0 + 1, _h - 1);
        double fy = y - j0;
        int i0 = (int)Math.Floor(x);
        double fx = x - i0;
        int a = ((i0 % _w) + _w) % _w, b = (a + 1) % _w;
        double top = _r[j0 * _w + a] * (1 - fx) + _r[j0 * _w + b] * fx;
        double bottom = _r[j1 * _w + a] * (1 - fx) + _r[j1 * _w + b] * fx;
        return _offset + top * (1 - fy) + bottom * fy;
    }

    /// <summary>
    /// What turns a height over this surface into one over <paramref name="shape"/>, metres, at a planetocentric
    /// latitude and longitude: the areoid's radius less the ellipsoid's along the same ray (the ray and the ellipsoid's
    /// normal part by at most the flattening, 0.3° on Mars: a few millimetres a kilometre of height).
    /// </summary>
    public double Correction(double latDeg, double lonDeg, Ellipsoid shape) => Radius(latDeg, lonDeg) - RadiusOf(shape, latDeg);

    /// <summary>An ellipsoid's radius at a planetocentric latitude.</summary>
    public static double RadiusOf(Ellipsoid shape, double latDeg)
    {
        double a = shape.A, b = shape.A * (1 - shape.F), phi = latDeg * Math.PI / 180;
        double c = Math.Cos(phi), s = Math.Sin(phi);
        return a * b / Math.Sqrt(b * b * c * c + a * a * s * s);
    }
}
