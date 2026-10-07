using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Items;
using UnitSport.Player;
using static UnitSport.Trailer.TrailerScript;

namespace UnitSport.Trailer;

/// <summary>
/// "I Got a Stick": a night at the Drognens barracks (#717), the music clip of
/// <c>docs/trailer/drognens.md</c> as shots. Recruits play poker in their room (a film set,
/// <see cref="DormSet"/>), take the karts round the barracks, and are caught at dawn. The yards
/// are full of the army's vehicles, as the logistics schools there drive them.
/// </summary>
public static class DrognensScript
{
    private static readonly Song S = Song.IGotAStick;

    // ---- the recruits, and the sergeant-major ------------------------------------------------------

    private static readonly ItemId[] Kit = [ItemId.GreenPolo, ItemId.CargoPants, ItemId.CombatBoots];

    internal static readonly Character Favre = new("FAVRE", new Color("5ad06e"),
        new Appearance(BodyBuild.Slim, 2, 1, 1, HairStyle.Short, 1), Kit);
    internal static readonly Character Muller = new("MÜLLER", new Color("ffb84d"),
        new Appearance(BodyBuild.Broad, 4, 0, 1, HairStyle.Quiff, 4), Kit);
    internal static readonly Character Bernasconi = new("BERNASCONI", new Color("6ec8ff"),
        new Appearance(BodyBuild.Slim, 6, 2, 3, HairStyle.Shaggy, 0), [.. Kit, ItemId.BlackBeanie]);
    internal static readonly Character Krasniqi = new("KRASNIQI", new Color("ff7a45"),
        new Appearance(BodyBuild.Stocky, 8, 0, 4, HairStyle.Short, 0), Kit);
    internal static readonly Character Rochat = new("ROCHAT", new Color("b4b4b4"),
        new Appearance(BodyBuild.Broad, 1, 0, 2, HairStyle.Short, 7), Kit);

    // ---- what they drive: stand-ins until #714 (the army's vehicles) and #715 (the kart) are in ----

    private static readonly RideKind Duro = Heavy(1), GClass = Heavy(5), Lorry = Heavy(8), Kart = Car(Yaris);
    /// <summary>Beer: a stand-in until #716's bottle.</summary>
    private const int Beer = (int)ItemId.WaterBottle;

    // ---- the place ----------------------------------------------------------------------------------

    /// <summary>The barracks: the north block with its ring road, the yard, the pitch (scouted from above).</summary>
    private static readonly Spot Barracks = new(2558330, 1169420);
    private static readonly Spot RingFrom = new(2558392, 1169300), RingTo = new(2558330, 1169515);
    private static readonly Spot RoadIn = new(2558490, 1169560), Gate = new(2558440, 1169470);

    /// <summary>The army's vehicles parked in the yard east of the long block, in rows facing east.</summary>
    private static IEnumerable<Cast> Yard()
    {
        for (int k = 0; k < 6; k++) yield return new() { Ride = Duro, At = new Spot(2558358, 1169328 + 3.3 * k), Heading = 90, Seed = 200 + k };
        for (int k = 0; k < 5; k++) yield return new() { Ride = GClass, At = new Spot(2558358, 1169352 + 3.0 * k), Heading = 90, Seed = 210 + k };
        for (int k = 0; k < 4; k++) yield return new() { Ride = Lorry, At = new Spot(2558377, 1169330 + 3.8 * k), Heading = 90, Seed = 220 + k };
    }

    /// <summary>The four karts on the ring road, staggered, MÜLLER in front.</summary>
    private static Cast[] Karts(float arc) =>
    [
        new() { Who = Muller, Ride = Kart, At = RingFrom, Toward = RingTo, Drive = Drive.Road, Route = "ring", Arc = arc, Skill = 1.1f, Aggression = 1f },
        new() { Who = Krasniqi, Ride = Kart, At = RingFrom, Toward = RingTo, Drive = Drive.Road, Route = "ring", Arc = arc - 7, Skill = 1.15f, Aggression = 1f },
        new() { Who = Favre, Ride = Kart, At = RingFrom, Toward = RingTo, Drive = Drive.Road, Route = "ring", Arc = arc - 14, Skill = 1.05f, Aggression = 0.8f },
        new() { Who = Bernasconi, Ride = Kart, At = RingFrom, Toward = RingTo, Drive = Drive.Road, Route = "ring", Arc = arc - 21, Skill = 1.0f, Aggression = 0.6f },
    ];

    // ---- the room -----------------------------------------------------------------------------------

    /// <summary>A shot in the dormitory, under the barracks.</summary>
    private static Shot Room(int number, string name, int bar, int bars) => new()
    {
        Number = number, Name = name, FromBar = bar, Bars = bars, Song = S, Hour = 22.8,
        Set = () => DormSet.Build(), SetAt = Barracks,
        Keys = [],
    };

    /// <summary>Who sits round the table (chairs 0-3 of <see cref="DormSet.Chairs"/>, null an empty one).</summary>
    private static Prop[] Table(params Character?[] seated)
    {
        var props = new List<Prop>
        {
            new(default, 0, default, default) { InSet = DormSet.Table + new Vector3(0.25f, 0.05f, 0.45f), Item = ItemId.Radio, Scale = 1.2f },
            new(default, 40, default, default) { InSet = DormSet.Table + new Vector3(-0.2f, 0.05f, -0.45f), Item = ItemId.Cheese },
            new(default, 0, default, default) { InSet = DormSet.Bed(-1, 3) + new Vector3(0.3f, 0.1f, 0f), Item = ItemId.Biberli },
        };
        for (int i = 0; i < seated.Length && i < DormSet.Chairs.Length; i++)
            if (seated[i] is { } who) props.Add(new(default, DormSet.Chairs[i].Bearing, default, default) { InSet = DormSet.Chairs[i].Hip, Seated = who });
        return [.. props];
    }

    public static readonly IReadOnlyList<Shot> Shots = new List<Shot>
    {
        // ---- the yard at night ----------------------------------------------------------------------
        new()
        {
            Number = 1, Name = "Lights out", FromBar = 1, Bars = 2, Song = S, Hour = 22.8, FadeIn = 1.2,
            Cast = [.. Yard()],
            Keys =
            [
                new(0, Pt.At(2558412, 1169296, 24), Pt.At(2558362, 1169345, 1.5f), 35),
                new(3.97, Pt.At(2558404, 1169322, 16), Pt.At(2558360, 1169352, 1.5f), 35),
            ],
            Preroll = 0.5,
            Chat = [new(0.6, Rochat, "lichterlöschen 22:00. tagwache 06:00, hä-si-be")],
            Supers = [new(0.3, 3.4, "PLACE D'ARMES DE DROGNENS · 22:47")],
        },

        // ---- the room: the door, the aisle, the table -----------------------------------------------
        Room(2, "61-405", 3, 1) with
        {
            Keys =
            [
                new(0, Pt.Set(0.3f, 1.65f, 9.4f), Pt.Set(-3.0f, 1.75f, 7.0f), 24),
                new(1.98, Pt.Set(-1.2f, 1.6f, 8.3f), Pt.Set(-2.9f, 1.3f, 5.0f), 24),
            ],
            Props = Table(Favre, Muller, null, Krasniqi),
            Cast =
            [
                new() { Who = Bernasconi, At = default, InSet = new Vector3(-1.1f, 0f, 0.3f), Heading = 250, Item = Beer, Dance = 29, Use = t => t % 2.4 < 0.8 },
            ],
            Preroll = 0.5,
        },
        Room(3, "Poker night", 4, 2) with
        {
            Keys =
            [
                new(0, Pt.Set(0.4f, 1.55f, 5.6f), Pt.Set(0f, 1.0f, -1.2f), 24),
                new(3.97, Pt.Set(0.2f, 1.4f, 1.6f), Pt.Set(0f, 0.9f, -1.3f), 28),
            ],
            Props = Table(Favre, Muller, null, Krasniqi),
            Cast =
            [
                new() { Who = Bernasconi, At = default, InSet = new Vector3(-1.1f, 0f, 0.3f), Heading = 250, Item = Beer, Dance = 29, Use = t => t % 2.4 < 0.8 },
            ],
            Smooth = 0.2f, Preroll = 0.5,
        },
        Room(4, "All in", 6, 1) with
        {
            Keys =
            [
                new(0, Pt.Set(-0.35f, 1.3f, 0.35f), Pt.Set(0.55f, 1.05f, -1.45f), 24),
                new(1.98, Pt.Set(-0.45f, 1.35f, 0.45f), Pt.Set(0.6f, 1.2f, -1.45f), 24),
            ],
            Props = Table(Favre, Muller, null, null),
            Cast =
            [
                new() { Who = Krasniqi, At = default, InSet = new Vector3(0.95f, 0f, -1.4f), Heading = 270, Dance = Cheer },
            ],
            Preroll = 0.4,
            Chat = [new(0.3, Krasniqi, "all in. SABTA"), new(1.1, Favre, "all in??")],
        },
        Room(5, "The idea", 7, 1) with
        {
            Keys =
            [
                new(0, Pt.Set(0.5f, 1.4f, -0.1f), Pt.Set(-0.85f, 1.5f, -0.9f), 28),
                new(1.98, Pt.Set(0.45f, 1.45f, 0.05f), Pt.Set(-0.85f, 1.6f, -0.9f), 28),
            ],
            Props = Table(Favre, null, null, Krasniqi),
            Cast =
            [
                new() { Who = Muller, At = default, InSet = new Vector3(-0.95f, 0f, -0.75f), Heading = 100, Dance = 27 },
            ],
            Preroll = 0.4,
            Chat = [new(0.2, Muller, "karts in garage 4. who's u-booting?"), new(1.2, Bernasconi, "polenta-fraktion is in")],
        },

        // ---- the race round the barracks ------------------------------------------------------------
        new()
        {
            Number = 6, Name = "Out", FromBar = 8, Bars = 2, Song = S, Hour = 22.8, Traffic = 0,
            Cast = [.. Karts(60), .. Yard()],
            Keys =
            [
                new(0, Pt.Road("ring", 75, -2.8f, 0.7f), Pt.On(0, 0f, 0.6f), 24),
                new(3.97, Pt.Road("ring", 75, -2.8f, 0.7f), Pt.On(3, 0f, 0.6f), 24),
            ],
            Smooth = 0.08f, Preroll = 3,
        },
        new()
        {
            Number = 7, Name = "From above", FromBar = 10, Bars = 1, Song = S, Hour = 22.8, Traffic = 0,
            Cast = [.. Karts(150)],
            Keys =
            [
                new(0, Pt.On(1, 0f, 30f, 6f), Pt.On(1, 0f, 0f, -4f), 35),
                new(1.98, Pt.On(1, 0f, 32f, 4f), Pt.On(1, 0f, 0f, -6f), 35),
            ],
            Smooth = 0.15f, Preroll = 3,
        },
        new()
        {
            Number = 8, Name = "Onboard", FromBar = 11, Bars = 1, Song = S, Hour = 22.8, Traffic = 0,
            Cast = [.. Karts(40), .. Yard()],
            Keys =
            [
                new(0, Pt.Cockpit(0, -0.6f, 0.25f), Pt.Cockpit(0, 20f, -0.5f), 24),
                new(1.98, Pt.Cockpit(0, -0.6f, 0.25f), Pt.Cockpit(0, 20f, -0.5f), 24),
            ],
            Preroll = 3,
        },
        new()
        {
            Number = 9, Name = "The pitch corner", FromBar = 12, Bars = 1, Song = S, Hour = 22.8, Traffic = 0,
            Cast = [.. Karts(100)],
            Keys =
            [
                new(0, Pt.Road("ring", 125, 4f, 1.2f), Pt.On(0, 0f, 0.5f), 28),
                new(1.98, Pt.Road("ring", 125, 4f, 1.2f), Pt.On(2, 0f, 0.5f), 28),
            ],
            Smooth = 0.06f, Preroll = 3,
        },
        new()
        {
            Number = 10, Name = "Caught", FromBar = 13, Bars = 1, Song = S, Hour = 22.8, Traffic = 0,
            Cast =
            [
                // ROCHAT's G-Class rolls in from the east, lights on, and stops; the karts have stopped dead
                new()
                {
                    Who = Rochat, Ride = GClass, At = new Spot(2558480, 1169508), Heading = 270, Lights = true,
                    Drive = Drive.Controls, Controls = t => t < 2.1 ? Pedal(0.3f) : Pedal(0f, 0f, 1f),
                },
                new() { Who = Muller, Ride = Kart, At = new Spot(2558419, 1169506.5), Heading = 92 },
                new() { Who = Krasniqi, Ride = Kart, At = new Spot(2558414, 1169510.5), Heading = 80 },
                new() { Who = Favre, Ride = Kart, At = new Spot(2558410, 1169504), Heading = 100 },
                new() { Who = Bernasconi, Ride = Kart, At = new Spot(2558405, 1169509), Heading = 86 },
            ],
            Keys =
            [
                new(0, Pt.At(2558397, 1169507.5, 1.1f), Pt.At(2558470, 1169508, 1.3f), 35),
                new(1.98, Pt.At(2558399, 1169507.5, 1.0f), Pt.At(2558470, 1169508, 1.3f), 35),
            ],
            Preroll = 1,
            Chat = [new(0.4, Rochat, "RECRUITS. landschaden. ZS at 06:00")],
        },

        // ---- dawn: the photo ------------------------------------------------------------------------
        new()
        {
            Number = 11, Name = "Souvenir", FromBar = 14, Bars = 2, Song = S, Hour = 6.4, Traffic = 0,
            Supers = [new(0.0, 0.9, "06:00 · HV")],
            Cast =
            [
                new() { Who = Muller, At = new Spot(2558396, 1169338), Heading = 270, Dance = Salute },
                new() { Who = Krasniqi, At = new Spot(2558396, 1169339.2), Heading = 270, Dance = Salute },
                new() { Who = Favre, At = new Spot(2558396, 1169340.4), Heading = 270, Dance = Salute },
                new() { Who = Bernasconi, At = new Spot(2558396, 1169341.6), Heading = 270, Dance = Salute },
                new() { Who = Rochat, Ride = GClass, At = new Spot(2558404, 1169336), Heading = 0 },
                new() { Ride = Kart, At = new Spot(2558393, 1169343.5), Heading = 200, Seed = 31 },
                new() { Ride = Kart, At = new Spot(2558392, 1169335.5), Heading = 160, Seed = 32 },
            ],
            Keys =
            [
                new(0, Pt.At(2558387, 1169339.8, 1.6f), Pt.At(2558396, 1169339.8, 1.3f), 28),
                new(3.97, Pt.At(2558384, 1169339.8, 2.6f), Pt.At(2558396, 1169339.8, 1.1f), 28),
            ],
            Preroll = 0.5,
            Photo = 0.3,
            PhotoFrom = new Key(0, Pt.At(2558389, 1169339.8, 1.5f), Pt.At(2558396, 1169339.8, 1.2f), 28),
            FadeOut = 1.6,
            Captions =
            [
                new(1.0, 2.97, "UNITSPORT SWITZERLAND", "Drognens · u-booting since 1972", Title: true),
                new(1.4, 2.57, "", S.Credit),
            ],
        },
    };
}
