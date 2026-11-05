using System;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Movie;

/// <summary>
/// A machine crossing a filmed shot (#1430: the street-to-Earth film's airliner and saucer): a
/// <see cref="FootPlayer.Puppet"/> riding <see cref="RideKind"/>, moved in a straight line at a steady
/// speed, posed as the movie studio poses a recording's (<see cref="MovieStage"/>): nothing simulated,
/// nothing replicated, never solid. A shot queue's <c>puppet</c> line (<see cref="ShotRunner"/>):
/// <c>puppet a320 E,N,alt,heading,speed[,climb]</c>, LV95 metres, the height above sea level, the
/// compass heading in degrees, metres a second along it and up.
/// </summary>
public partial class FilmPuppet : Node
{
    private FootPlayer _puppet = null!;
    private RideKind _kind;
    private GlobalPos _from;
    private double _heading, _speed, _climb, _t, _stamp;
    private static int _count;

    /// <summary>The puppet a <c>puppet</c> line asks for, put under <paramref name="parent"/>; null (logged) when the line is not one.</summary>
    public static FilmPuppet? Spawn(Node parent, WorldOrigin origin, string spec)
    {
        var parts = spec.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var p = parts.Length == 2 ? parts[1].Split(',') : [];
        var inv = CultureInfo.InvariantCulture;
        var v = new double[6];
        RideKind kind = default;
        bool ok = parts.Length == 2 && Enum.TryParse(parts[0], true, out kind) && p.Length is 5 or 6;
        for (int i = 0; ok && i < p.Length; i++) ok = double.TryParse(p[i], NumberStyles.Float, inv, out v[i]);
        if (!ok)
        {
            GD.PrintErr($"[puppet] not a ride and E,N,alt,heading,speed[,climb]: {spec}");
            return null;
        }
        var film = new FilmPuppet { Name = "FilmPuppet", _kind = kind, _from = GlobalPos.FromLv95(v[0], v[1], v[2]), _heading = v[3], _speed = v[4], _climb = v[5] };
        // under the studio's names, so neither meets a peer's nor the other's
        int id = MovieStage.PuppetBase - 1 - _count++;
        film._puppet = new FootPlayer { Name = id.ToString(), Origin = origin, Puppet = true };
        // not us: it takes the remote path, as the studio's
        film._puppet.SetMultiplayerAuthority(id);
        film.AddChild(film._puppet);
        parent.AddChild(film);
        GD.Print(string.Create(inv, $"[puppet] {kind} from {v[0]:F0},{v[1]:F0} at {v[2]:F0} m, heading {v[3]:F0}, {v[4]:F0} m/s"));
        // where the camera is and looks, to place the next one by
        if (parent.GetViewport()?.GetCamera3D() is { } cam)
        {
            var (ce, cn) = origin.ToLv95(cam.GlobalPosition);
            var (ae, an) = origin.ToLv95(cam.GlobalPosition - cam.GlobalBasis.Z);
            GD.Print(string.Create(inv, $"[puppet] camera at {ce:F0},{cn:F0}, {origin.AltitudeOf(cam.GlobalPosition):F0} m, looking {Mathf.RadToDeg(Math.Atan2(ae - ce, an - cn)):F0}°"));
        }
        return film;
    }

    public override void _Process(double delta)
    {
        _t += delta;
        double h = Mathf.DegToRad(_heading);
        double e = Math.Sin(h) * _speed, n = Math.Cos(h) * _speed;
        _puppet.RideKindId = (int)_kind;
        // a body's yaw turns its forward (-Z) from north toward east the other way round; a heading on
        // the LV95 grid, given in GlobalPos's axes (the wire's ENU and true north are worked out from them)
        _puppet.SetNetState(_from.Moved(e * _t, n * _t, _climb * _t), new Vector3((float)e, (float)_climb, (float)-n), (float)-h);
        // last: its setter applies the state
        _puppet.NetTime = ++_stamp;
        // every 2 s: where it is from the camera, to frame a shot by
        if ((int)(_t / 2) != (int)((_t - delta) / 2) && GetViewport()?.GetCamera3D() is { } cam)
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[puppet] {_kind} t={_t:F1} s: {_puppet.GlobalPosition.DistanceTo(cam.GlobalPosition):F0} m from the camera, {(_puppet.GlobalPosition - cam.GlobalPosition).Y:F0} m up"));
    }
}
