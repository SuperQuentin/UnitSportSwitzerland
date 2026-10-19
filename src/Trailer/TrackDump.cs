using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace UnitSport.Trailer;

/// <summary>
/// <c>--trailer-tracks DIR</c> (#924): every actor and staged object of a shot from its actors' go,
/// every frame (the film's frame rate; the pre-roll's frames negative, the roll's from 0), to
/// <c>DIR/shotNN.tracks.csv</c>: the drift measurement's input
/// (<c>tools/trailer-drift.py</c> compares two dumps of the same shot). One row per object a frame:
/// <c>frame,t,id,kind,e,n,alt,yaw,speed,pose,ride,beam</c>: LV95 metres, compass yaw in degrees, m/s;
/// <c>pose</c> the body's pose kind (or a state word), <c>ride</c> what it rides, <c>beam</c> 1 while
/// a beam is on it or from it. Rows are built into one reused buffer (one shot at a time, so no file
/// stays open between shots).
/// </summary>
public sealed class TrackDump : IDisposable
{
    public const string Header = "frame,t,id,kind,e,n,alt,yaw,speed,pose,ride,beam";

    private readonly StreamWriter _w;
    private readonly StringBuilder _row = new(160);
    public string Path { get; }

    private TrackDump(string path)
    {
        Path = path;
        _w = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        _w.WriteLine(Header);
    }

    /// <summary>The dump of shot <paramref name="shot"/> in <paramref name="dir"/>, or null (logged) when it cannot be written.</summary>
    public static TrackDump? Start(string dir, int shot)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return new TrackDump(System.IO.Path.Combine(dir, $"shot{shot:00}.tracks.csv"));
        }
        catch (Exception e)
        {
            Godot.GD.PrintErr($"[trailer] no track dump in {dir}: {e.Message}");
            return null;
        }
    }

    /// <summary>One object at one frame. Millimetres and hundredths of a degree: what the drift is measured in.</summary>
    public void Row(int frame, double t, string id, string kind, double e, double n, double alt, double yaw, double speed,
        string pose, string ride, bool beam)
    {
        var c = CultureInfo.InvariantCulture;
        _row.Clear();
        _row.Append(frame).Append(',').Append(t.ToString("F4", c)).Append(',').Append(id).Append(',').Append(kind).Append(',')
            .Append(e.ToString("F3", c)).Append(',').Append(n.ToString("F3", c)).Append(',').Append(alt.ToString("F3", c)).Append(',')
            .Append(yaw.ToString("F2", c)).Append(',').Append(speed.ToString("F3", c)).Append(',')
            .Append(pose).Append(',').Append(ride).Append(',').Append(beam ? '1' : '0');
        _w.WriteLine(_row);
    }

    public void Dispose() => _w.Dispose();
}
