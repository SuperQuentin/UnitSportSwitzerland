using System;
using System.Globalization;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// One satellite's two-line elements (TLE), as CelesTrak publishes them (#1444): the epoch, the
/// orbit's mean elements and the drag term SGP4 reads.
/// </summary>
public sealed record Tle(string Name, int Number, DateTime Epoch, double InclinationDeg, double NodeDeg,
    double Eccentricity, double PerigeeDeg, double MeanAnomalyDeg, double RevsPerDay, double BStar)
{
    /// <summary>Parses a TLE's two lines (and an optional name line above them).</summary>
    public static Tle? Parse(string name, string line1, string line2)
    {
        try
        {
            if (line1.Length < 69 || line2.Length < 69 || line1[0] != '1' || line2[0] != '2') return null;
            var inv = CultureInfo.InvariantCulture;
            int number = int.Parse(line1.Substring(2, 5).Trim(), inv);
            int yy = int.Parse(line1.Substring(18, 2), inv);
            double day = double.Parse(line1.Substring(20, 12), inv);
            var epoch = new DateTime(yy < 57 ? 2000 + yy : 1900 + yy, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day - 1);
            double bstar = Implied(line1.Substring(53, 8));
            return new Tle(name.Trim(), number, epoch,
                double.Parse(line2.Substring(8, 8), inv), double.Parse(line2.Substring(17, 8), inv),
                double.Parse("0." + line2.Substring(26, 7).Trim(), inv), double.Parse(line2.Substring(34, 8), inv),
                double.Parse(line2.Substring(43, 8), inv), double.Parse(line2.Substring(52, 11), inv), bstar);
        }
        catch (FormatException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>A TLE field with an implied decimal point and exponent: " 28098-4" is 0.28098e-4.</summary>
    private static double Implied(string field)
    {
        field = field.Trim();
        if (field.Length == 0) return 0;
        int sign = 1;
        if (field[0] is '-' or '+')
        {
            if (field[0] == '-') sign = -1;
            field = field[1..];
        }
        int e = field.LastIndexOfAny(['-', '+']);
        double mantissa = double.Parse("0." + (e > 0 ? field[..e] : field), CultureInfo.InvariantCulture);
        int exponent = e > 0 ? int.Parse(field[e..], CultureInfo.InvariantCulture) : 0;
        return sign * mantissa * Math.Pow(10, exponent);
    }

    /// <summary>Its period, minutes.</summary>
    public double PeriodMinutes => 1440.0 / RevsPerDay;
}

/// <summary>
/// SGP4, the propagator TLEs are made for (#1444): Spacetrack Report #3 as Vallado, Crawford, Hujsak
/// and Kelso revised it (AIAA 2006-6753), WGS-72 constants, the near-Earth branch only (periods under
/// 225 minutes: the ISS, Starlink, the bright satellites; deep-space SDP4 is left out). Gives the
/// position in TEME, km, minutes after the TLE's epoch. Checked against Vallado's verification run
/// (<c>EphemerisTests</c>).
/// </summary>
public sealed class Sgp4
{
    private const double Mu = 398600.8, Re = 6378.135, J2 = 0.001082616, J3 = -0.00000253881, J4 = -0.00000165597;
    private static readonly double Xke = 60.0 / Math.Sqrt(Re * Re * Re / Mu);
    private const double J3oJ2 = J3 / J2, TwoPi = 2 * Math.PI, X2o3 = 2.0 / 3.0;

    public Tle Elements { get; }

    /// <summary>A deep-space orbit (a period over 225 minutes): not propagated here.</summary>
    public bool DeepSpace { get; }

    private readonly double _ecco, _inclo, _nodeo, _argpo, _mo, _no, _bstar;
    private readonly double _con41, _x1mth2, _x7thm1, _cc1, _cc4, _cc5, _d2, _d3, _d4, _delmo, _eta, _argpdot, _omgcof,
        _sinmao, _t2cof, _t3cof, _t4cof, _t5cof, _xmcof, _mdot, _nodedot, _nodecf, _xlcof, _aycof;
    private readonly bool _isimp;

    public Sgp4(Tle tle)
    {
        Elements = tle;
        const double deg = Math.PI / 180;
        _ecco = tle.Eccentricity;
        _inclo = tle.InclinationDeg * deg;
        _nodeo = tle.NodeDeg * deg;
        _argpo = tle.PerigeeDeg * deg;
        _mo = tle.MeanAnomalyDeg * deg;
        _bstar = tle.BStar;
        double noKozai = tle.RevsPerDay * TwoPi / 1440.0;

        // initl: recover the original mean motion and semi-major axis
        double eccsq = _ecco * _ecco, omeosq = 1 - eccsq, rteosq = Math.Sqrt(omeosq);
        double cosio = Math.Cos(_inclo), cosio2 = cosio * cosio;
        double ak = Math.Pow(Xke / noKozai, X2o3);
        double d1 = 0.75 * J2 * (3 * cosio2 - 1) / (rteosq * omeosq);
        double del = d1 / (ak * ak);
        double adel = ak * (1 - del * del - del * (1.0 / 3.0 + 134 * del * del / 81));
        del = d1 / (adel * adel);
        _no = noKozai / (1 + del);
        double ao = Math.Pow(Xke / _no, X2o3);
        double sinio = Math.Sin(_inclo);
        double po = ao * omeosq;
        double con42 = 1 - 5 * cosio2;
        _con41 = -con42 - cosio2 - cosio2;
        double posq = po * po;
        double rp = ao * (1 - _ecco);
        DeepSpace = TwoPi / _no >= 225;

        // sgp4init
        double ss = 78 / Re + 1, qzms2t = Math.Pow((120 - 78) / Re, 4);
        _isimp = rp < 220 / Re + 1;
        double sfour = ss, qzms24 = qzms2t, perige = (rp - 1) * Re;
        if (perige < 156)
        {
            sfour = perige - 78;
            if (perige < 98) sfour = 20;
            qzms24 = Math.Pow((120 - sfour) / Re, 4);
            sfour = sfour / Re + 1;
        }
        double pinvsq = 1 / posq;
        double tsi = 1 / (ao - sfour);
        _eta = ao * _ecco * tsi;
        double etasq = _eta * _eta, eeta = _ecco * _eta, psisq = Math.Abs(1 - etasq);
        double coef = qzms24 * Math.Pow(tsi, 4), coef1 = coef / Math.Pow(psisq, 3.5);
        double cc2 = coef1 * _no * (ao * (1 + 1.5 * etasq + eeta * (4 + etasq))
            + 0.375 * J2 * tsi / psisq * _con41 * (8 + 3 * etasq * (8 + etasq)));
        _cc1 = _bstar * cc2;
        double cc3 = _ecco > 1e-4 ? -2 * coef * tsi * J3oJ2 * _no * sinio / _ecco : 0;
        _x1mth2 = 1 - cosio2;
        _cc4 = 2 * _no * coef1 * ao * omeosq * (_eta * (2 + 0.5 * etasq) + _ecco * (0.5 + 2 * etasq)
            - J2 * tsi / (ao * psisq) * (-3 * _con41 * (1 - 2 * eeta + etasq * (1.5 - 0.5 * eeta))
                + 0.75 * _x1mth2 * (2 * etasq - eeta * (1 + etasq)) * Math.Cos(2 * _argpo)));
        _cc5 = 2 * coef1 * ao * omeosq * (1 + 2.75 * (etasq + eeta) + eeta * etasq);
        double cosio4 = cosio2 * cosio2;
        double temp1 = 1.5 * J2 * pinvsq * _no, temp2 = 0.5 * temp1 * J2 * pinvsq, temp3 = -0.46875 * J4 * pinvsq * pinvsq * _no;
        _mdot = _no + 0.5 * temp1 * rteosq * _con41 + 0.0625 * temp2 * rteosq * (13 - 78 * cosio2 + 137 * cosio4);
        _argpdot = -0.5 * temp1 * con42 + 0.0625 * temp2 * (7 - 114 * cosio2 + 395 * cosio4) + temp3 * (3 - 36 * cosio2 + 49 * cosio4);
        double xhdot1 = -temp1 * cosio;
        _nodedot = xhdot1 + (0.5 * temp2 * (4 - 19 * cosio2) + 2 * temp3 * (3 - 7 * cosio2)) * cosio;
        _omgcof = _bstar * cc3 * Math.Cos(_argpo);
        _xmcof = _ecco > 1e-4 ? -X2o3 * coef * _bstar / eeta : 0;
        _nodecf = 3.5 * omeosq * xhdot1 * _cc1;
        _t2cof = 1.5 * _cc1;
        _xlcof = Math.Abs(cosio + 1) > 1.5e-12
            ? -0.25 * J3oJ2 * sinio * (3 + 5 * cosio) / (1 + cosio)
            : -0.25 * J3oJ2 * sinio * (3 + 5 * cosio) / 1.5e-12;
        _aycof = -0.5 * J3oJ2 * sinio;
        _delmo = Math.Pow(1 + _eta * Math.Cos(_mo), 3);
        _sinmao = Math.Sin(_mo);
        _x7thm1 = 7 * cosio2 - 1;
        if (!_isimp)
        {
            double cc1sq = _cc1 * _cc1;
            _d2 = 4 * ao * tsi * cc1sq;
            double temp = _d2 * tsi * _cc1 / 3;
            _d3 = (17 * ao + sfour) * temp;
            _d4 = 0.5 * temp * ao * tsi * (221 * ao + 31 * sfour) * _cc1;
            _t3cof = _d2 + 2 * cc1sq;
            _t4cof = 0.25 * (3 * _d3 + _cc1 * (12 * _d2 + 10 * cc1sq));
            _t5cof = 0.2 * (3 * _d4 + 12 * _cc1 * _d3 + 6 * _d2 * _d2 + 15 * cc1sq * (2 * _d2 + cc1sq));
        }
    }

    /// <summary>The position in TEME (km) <paramref name="minutes"/> after the epoch, or null when the orbit has decayed or is deep space.</summary>
    public (double X, double Y, double Z)? Position(double minutes)
    {
        if (DeepSpace) return null;
        double t = minutes;
        double xmdf = _mo + _mdot * t, argpdf = _argpo + _argpdot * t, nodedf = _nodeo + _nodedot * t;
        double argpm = argpdf, mm = xmdf, t2 = t * t;
        double nodem = nodedf + _nodecf * t2;
        double tempa = 1 - _cc1 * t, tempe = _bstar * _cc4 * t, templ = _t2cof * t2;
        if (!_isimp)
        {
            double delomg = _omgcof * t;
            double delmtemp = 1 + _eta * Math.Cos(xmdf);
            double delm = _xmcof * (delmtemp * delmtemp * delmtemp - _delmo);
            double shift = delomg + delm;
            mm = xmdf + shift;
            argpm = argpdf - shift;
            double t3 = t2 * t, t4 = t3 * t;
            tempa = tempa - _d2 * t2 - _d3 * t3 - _d4 * t4;
            tempe += _bstar * _cc5 * (Math.Sin(mm) - _sinmao);
            templ += _t3cof * t3 + t4 * (_t4cof + t * _t5cof);
        }
        double am = Math.Pow(Xke / _no, X2o3) * tempa * tempa;
        double nm = Xke / Math.Pow(am, 1.5);
        double em = _ecco - tempe;
        if (em >= 1 || em < -0.001 || am < 0.95) return null;   // decayed
        if (em < 1e-6) em = 1e-6;
        mm += _no * templ;
        double xlm = mm + argpm + nodem;
        nodem %= TwoPi;
        argpm %= TwoPi;
        xlm %= TwoPi;
        mm = (xlm - argpm - nodem) % TwoPi;

        double sinip = Math.Sin(_inclo), cosip = Math.Cos(_inclo);
        double axnl = em * Math.Cos(argpm);
        double temp0 = 1 / (am * (1 - em * em));
        double aynl = em * Math.Sin(argpm) + temp0 * _aycof;
        double xl = mm + argpm + nodem + temp0 * _xlcof * axnl;

        // Kepler's equation for the eccentric longitude
        double u = (xl - nodem) % TwoPi;
        double eo1 = u, sineo1 = 0, coseo1 = 0;
        for (int k = 0; k < 10; k++)
        {
            sineo1 = Math.Sin(eo1);
            coseo1 = Math.Cos(eo1);
            double tem5 = 1 - coseo1 * axnl - sineo1 * aynl;
            tem5 = (u - aynl * coseo1 + axnl * sineo1 - eo1) / tem5;
            tem5 = Math.Clamp(tem5, -0.95, 0.95);
            eo1 += tem5;
            if (Math.Abs(tem5) < 1e-12) break;
        }

        double ecose = axnl * coseo1 + aynl * sineo1, esine = axnl * sineo1 - aynl * coseo1;
        double el2 = axnl * axnl + aynl * aynl, pl = am * (1 - el2);
        if (pl < 0) return null;
        double rl = am * (1 - ecose);
        double betal = Math.Sqrt(1 - el2);
        double temp = esine / (1 + betal);
        double sinu = am / rl * (sineo1 - aynl - axnl * temp);
        double cosu = am / rl * (coseo1 - axnl + aynl * temp);
        double su = Math.Atan2(sinu, cosu);
        double sin2u = (cosu + cosu) * sinu, cos2u = 1 - 2 * sinu * sinu;
        temp = 1 / pl;
        double temp1 = 0.5 * J2 * temp, temp2 = temp1 * temp;

        double mrt = rl * (1 - 1.5 * temp2 * betal * _con41) + 0.5 * temp1 * _x1mth2 * cos2u;
        su -= 0.25 * temp2 * _x7thm1 * sin2u;
        double xnode = nodem + 1.5 * temp2 * cosip * sin2u;
        double xinc = _inclo + 1.5 * temp2 * cosip * sinip * cos2u;
        if (mrt < 1) return null;

        double sinsu = Math.Sin(su), cossu = Math.Cos(su), snod = Math.Sin(xnode), cnod = Math.Cos(xnode);
        double sini = Math.Sin(xinc), cosi = Math.Cos(xinc);
        double xmx = -snod * cosi, xmy = cnod * cosi;
        double ux = xmx * sinsu + cnod * cossu, uy = xmy * sinsu + snod * cossu, uz = sini * sinsu;
        return (mrt * ux * Re, mrt * uy * Re, mrt * uz * Re);
    }

    /// <summary>
    /// The position in the Earth-fixed frame (m) at <paramref name="utc"/>: TEME turned by sidereal
    /// time (the true equinox's; TEME's own is within a few metres of it here), polar motion ignored.
    /// </summary>
    public Terrain.Format.EcefPos? Ecef(DateTime utc)
    {
        if (Position((utc - Elements.Epoch).TotalMinutes) is not { } p) return null;
        double g = Ephemeris.SiderealTime(utc), c = Math.Cos(g), s = Math.Sin(g);
        return new Terrain.Format.EcefPos((c * p.X + s * p.Y) * 1000, (-s * p.X + c * p.Y) * 1000, p.Z * 1000);
    }
}
