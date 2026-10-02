using System.Globalization;
using System.Xml;
using UnitSport.Terrain.Format;
using UnitSport.Player;

namespace UnitSport.Gpx;

/// <summary>
/// Reads GPX 1.0/1.1 track files. Only what playback needs is taken: track points, their
/// elevation and time. Routes (<c>rte</c>) are accepted as a fallback for files exported
/// as a planned route rather than a recording.
/// </summary>
public static class GpxParser
{
    /// <summary>
    /// Fallback speed for files without timestamps (planned routes, exported courses).
    /// 3.0 m/s is a steady 5:33 min/km — a plausible running pace.
    /// </summary>
    private const double AssumedSpeed = 3.0;

    /// <summary>Climbs below this are GPS noise rather than real ascent.</summary>
    private const double AscentThreshold = 1.5;

    /// <summary>
    /// Half-width, in metres along the track, of the window used to smooth positions.
    ///
    /// Consumer GPS wobbles a couple of metres per fix. At ~1 Hz that wobble is the same
    /// size as the distance actually travelled between fixes, so the raw path weaves and
    /// the runner surges and stalls — a boat-like motion. Averaging over a window of
    /// *track distance* (not point count) fixes dense recordings while leaving sparse
    /// ones, where consecutive points are already tens of metres apart, untouched.
    /// </summary>
    private const double SmoothingWindowM = 18.0;

    public static GpxTrack Parse(string path)
    {
        var lats = new List<double>();
        // the nose's yaw per fix, when the recorder wrote one (drivecheck does): NaN otherwise
        var yaws = new List<float>();
        bool exact = false;
        var lons = new List<double>();
        var eles = new List<double>();
        var times = new List<DateTime?>();
        string? activityType = null;

        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true };
        using (var reader = XmlReader.Create(path, settings))
        {
            double? pendingEle = null;
            DateTime? pendingTime = null;
            bool inPoint = false;
            bool inTrack = false;

            // NOTE: ReadElementContentAsString() already advances past the element, so the
            // loop must not call Read() again on those branches — doing so silently skips
            // the following sibling, which is how <time> after <ele> went missing.
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                switch (reader.LocalName)
                {
                    // A track this game recorded (drivecheck) has no GPS noise: its fixes are the
                    // simulated car's own positions, and smoothing them only cuts the corners,
                    // putting the replay off the road the car actually stayed on.
                    case "gpx":
                        exact = reader.GetAttribute("creator")?.Contains("UnitSportSwitzerland") == true;
                        reader.Read();
                        break;

                    case "trk":
                    case "rte":
                        inTrack = true;
                        reader.Read();
                        break;

                    // <type> is the standard GPX field for what the activity was (Strava, Garmin
                    // and most other exporters all write it). Only the FIRST one counts - it must
                    // be read before the first trkpt sets inTrack's sibling state moot, and a
                    // route/track can carry only one anyway.
                    case "type" when inTrack && activityType == null:
                        activityType = reader.ReadElementContentAsString();
                        break;

                    case "trkpt":
                    case "rtept":
                    {
                        if (inPoint) Commit(eles, times, ref pendingEle, ref pendingTime);
                        string? lat = reader.GetAttribute("lat");
                        string? lon = reader.GetAttribute("lon");
                        inPoint = lat != null && lon != null;
                        if (inPoint)
                        {
                            yaws.Add(float.NaN);
                            lats.Add(double.Parse(lat!, CultureInfo.InvariantCulture));
                            lons.Add(double.Parse(lon!, CultureInfo.InvariantCulture));
                        }
                        reader.Read();
                        break;
                    }

                    case "ele" when inPoint:
                        if (double.TryParse(reader.ReadElementContentAsString(),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out double ev))
                            pendingEle = ev;
                        break;

                    case "yaw" when inPoint:
                        if (float.TryParse(reader.ReadElementContentAsString(),
                                NumberStyles.Float, CultureInfo.InvariantCulture, out float yv))
                            yaws[^1] = yv;
                        break;

                    case "time" when inPoint:
                        if (DateTime.TryParse(reader.ReadElementContentAsString(),
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                out var tv))
                            pendingTime = tv;
                        break;

                    default:
                        reader.Read();
                        break;
                }
            }
            if (inPoint) Commit(eles, times, ref pendingEle, ref pendingTime);
        }

        if (lats.Count == 0)
            throw new InvalidDataException("No track or route points found in the GPX file");

        var track = Build(Path.GetFileNameWithoutExtension(path), lats, lons, eles, times, ParseKind(activityType), smooth: !exact);
        return yaws.Any(y => !float.IsNaN(y)) ? track.WithYaw(yaws) : track;
    }

    /// <summary>
    /// GPX's &lt;type&gt; is free text - exporters write "cycling", "Cycling", "biking",
    /// "mountain biking", "road biking", "1" (Strava's numeric type for a ride) and no doubt
    /// others. Matched by substring rather than exact value for exactly that reason. Anything
    /// not recognised as a ride plays as a runner, which is what every track already did before
    /// this existed - a GPX this cannot classify is not a regression, it is silence.
    /// </summary>
    private static RideKind ParseKind(string? type)
    {
        if (string.IsNullOrEmpty(type)) return RideKind.OnFoot;
        string t = type.ToLowerInvariant();
        // "car:N" — CarCatalog.All[N], as the drive check records it
        if (t.StartsWith("car:") && int.TryParse(t[4..], out int car)
            && CarCatalog.For((RideKind)(CarCatalog.First + car)) is { } spec) return spec.Kind;
        if (t.Contains("cycl") || t.Contains("bik") || t == "1") return RideKind.RoadBike;
        return RideKind.OnFoot;
    }

    private static void Commit(List<double> eles, List<DateTime?> times,
        ref double? ele, ref DateTime? time)
    {
        eles.Add(ele ?? double.NaN);
        times.Add(time);
        ele = null;
        time = null;
    }

    /// <summary>
    /// Replaces each position with a weighted average of its neighbours within
    /// <see cref="SmoothingWindowM"/>, then rebuilds cumulative distance from the smoothed
    /// path. Endpoints keep their weight so the track still starts and finishes where it
    /// was recorded. Uses a sliding window, so cost stays linear on long recordings.
    /// </summary>
    private static void Smooth(List<TrackPoint> points)
    {
        int n = points.Count;
        if (n < 3) return;

        var outE = new double[n];
        var outN = new double[n];
        int lo = 0, hi = 0;

        for (int i = 0; i < n; i++)
        {
            double d = points[i].Distance;
            while (lo < i && points[i].Distance - points[lo].Distance > SmoothingWindowM) lo++;
            while (hi < n - 1 && points[hi + 1].Distance - d <= SmoothingWindowM) hi++;

            double sumE = 0, sumN = 0, weight = 0;
            for (int j = lo; j <= hi; j++)
            {
                // triangular weighting: nearby fixes count for more than the window edge
                double w = 1.0 - Math.Abs(points[j].Distance - d) / SmoothingWindowM;
                if (w <= 0) continue;
                sumE += points[j].E * w;
                sumN += points[j].N * w;
                weight += w;
            }
            outE[i] = weight > 0 ? sumE / weight : points[i].E;
            outN[i] = weight > 0 ? sumN / weight : points[i].N;
        }

        double distance = 0;
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
                distance += Math.Sqrt(
                    (outE[i] - outE[i - 1]) * (outE[i] - outE[i - 1]) +
                    (outN[i] - outN[i - 1]) * (outN[i] - outN[i - 1]));
            points[i] = points[i] with { E = outE[i], N = outN[i], Distance = distance };
        }
    }

    private static GpxTrack Build(string name, List<double> lats, List<double> lons,
        List<double> eles, List<DateTime?> times, RideKind kind, bool smooth = true)
    {
        int n = lats.Count;
        var points = new List<TrackPoint>(n);

        bool hasTiming = times.Count(t => t.HasValue) > n / 2;
        DateTime? start = times.FirstOrDefault(t => t.HasValue);

        double distance = 0;
        double minEle = double.MaxValue, maxEle = double.MinValue, ascent = 0;
        double lastCountedEle = double.NaN;
        double prevE = 0, prevN = 0;

        for (int i = 0; i < n; i++)
        {
            var (e, nn) = SwissProjection.ToLv95(lats[i], lons[i]);
            if (i > 0)
                distance += Math.Sqrt((e - prevE) * (e - prevE) + (nn - prevN) * (nn - prevN));
            prevE = e;
            prevN = nn;

            double ele = eles[i];
            if (!double.IsNaN(ele))
            {
                minEle = Math.Min(minEle, ele);
                maxEle = Math.Max(maxEle, ele);
                if (double.IsNaN(lastCountedEle)) lastCountedEle = ele;
                else if (ele - lastCountedEle > AscentThreshold)
                {
                    ascent += ele - lastCountedEle;
                    lastCountedEle = ele;
                }
                else if (ele < lastCountedEle) lastCountedEle = ele;
            }

            double seconds = hasTiming && times[i].HasValue && start.HasValue
                ? (times[i]!.Value - start.Value).TotalSeconds
                : distance / AssumedSpeed;

            points.Add(new TrackPoint(e, nn, double.IsNaN(ele) ? 0 : ele, seconds, distance));
        }

        if (smooth) Smooth(points);

        // recorded time can stall or jump backwards; force it to advance so binary search
        // over the timeline stays valid
        for (int i = 1; i < points.Count; i++)
            if (points[i].Seconds <= points[i - 1].Seconds)
                points[i] = points[i] with { Seconds = points[i - 1].Seconds + 0.001 };

        return new GpxTrack
        {
            Name = name,
            Points = points,
            HasTiming = hasTiming,
            MinElevation = minEle == double.MaxValue ? 0 : minEle,
            MaxElevation = maxEle == double.MinValue ? 0 : maxEle,
            Ascent = ascent,
            Kind = kind,
        };
    }
}
