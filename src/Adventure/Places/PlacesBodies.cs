using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Adventure.Places;

/// <summary>
/// A player body as the places rules see it (#1224): where it is and how it travels, from the replicated
/// state every peer has (<see cref="FootPlayer.RideKindId"/>, the host's for a passenger, a deck or a ski
/// lift as <see cref="PlacesRules.Aboard"/>, <see cref="FootPlayer.Down"/>). The server reads its proxies'
/// published positions; offline the local body.
/// </summary>
public static class PlacesBodies
{
    public static PlacesBody? Of(FootPlayer? me, GlobalPos at, Func<long, FootPlayer?> player, Func<double, double, float?> groundAt)
    {
        if (me == null || !GodotObject.IsInstanceValid(me)) return null;
        int ride = me.RideKindId;
        if (me.RidingWith != 0) ride = player(me.RidingWith) is { } host && GodotObject.IsInstanceValid(host) ? host.RideKindId : PlacesRules.Aboard;
        else if (me.DeckOn.Length > 0 || me.LiftLine != 0) ride = PlacesRules.Aboard;
        var lv = at.Lv95;   // the discovery rules work on the LV95 grid: lake outlines, tiles
        float agl = groundAt(lv.E, lv.N) is { } ground ? (float)(at.Alt - ground) : float.NaN;
        return new PlacesBody(lv.E, lv.N, at.Alt, agl, ride, me.Down != 0);
    }
}
