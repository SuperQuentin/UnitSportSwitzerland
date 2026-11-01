using Godot;
using UnitSport.Audio;
using UnitSport.Items;

namespace UnitSport.Occasions.Birthdays;

/// <summary>
/// A birthday wish or an opened gift (#1423): the <see cref="ItemEventKind.BirthdayWish"/> handler, on every peer near. The chime twice, a
/// fifth apart, and the Räppli's paper thrown up over the birthday player's head (<see cref="Festivals.Fasnacht.RaeppliFx.Burst"/>): GPU
/// particles, nothing built per frame.
/// </summary>
public static class BirthdayFx
{
    /// <summary>Wishes and gifts shown on this machine since start (checks).</summary>
    public static int Count { get; private set; }

    public static void OnWish(ItemEvents n, ItemEvent e)
    {
        Count++;
        n.Sound3D(e.Position, SfxSynth.Chime, 1.0f, -6f, unitSize: 5f, maxDistance: 60f);
        n.Sound3D(e.Position, SfxSynth.Chime, 1.5f, -8f, unitSize: 5f, maxDistance: 60f);
        Festivals.Fasnacht.RaeppliFx.Burst(n, e.Position, Vector3.Up);
    }
}
