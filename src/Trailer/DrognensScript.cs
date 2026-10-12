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

    /// <summary>The TAZ (#716): the camouflage jacket and trousers, boots; off duty, the olive T-shirt.</summary>
    private static readonly ItemId[] Kit = [ItemId.TazJacket, ItemId.TazTrousers, ItemId.CombatBoots];
    private static readonly ItemId[] OffDuty = [ItemId.ArmyTee, ItemId.TazTrousers, ItemId.CombatBoots];

    internal static readonly Character Favre = new("FAVRE", new Color("5ad06e"),
        new Appearance(BodyBuild.Slim, 2, 1, 1, HairStyle.Short, 1), OffDuty);
    internal static readonly Character Muller = new("MÜLLER", new Color("ffb84d"),
        new Appearance(BodyBuild.Broad, 4, 0, 1, HairStyle.Quiff, 4), Kit);
    internal static readonly Character Bernasconi = new("BERNASCONI", new Color("6ec8ff"),
        new Appearance(BodyBuild.Slim, 6, 2, 3, HairStyle.Shaggy, 0), [.. OffDuty, ItemId.BlackBeanie]);
    internal static readonly Character Krasniqi = new("KRASNIQI", new Color("ff7a45"),
        new Appearance(BodyBuild.Stocky, 8, 0, 4, HairStyle.Short, 0), Kit);
    internal static readonly Character Rochat = new("ROCHAT", new Color("b4b4b4"),
        new Appearance(BodyBuild.Broad, 1, 0, 2, HairStyle.Short, 7), Kit);

    // ---- what they drive: the army's (#714) and the go-kart (#715) in its army skin --------------

    /// <summary>The Mowag Duro II, the Mercedes G 300 and the Iveco Trakker 6x6 (<see cref="HeavyCatalog"/>, #714).</summary>
    private static readonly RideKind Duro = (RideKind)106, GClass = (RideKind)107, Lorry = (RideKind)108;
    /// <summary>The rental kart (#715), in the Army preset: olive, an M plate on the nose.</summary>
    private static readonly RideKind Kart = CarCatalog.Kart.Kind;
    private const int Army = CarSetups.ArmyId;
    private const int Beer = (int)ItemId.BeerBottle, Cards = (int)ItemId.PlayingCards;

    /// <summary>
    /// The race's hour: first light, after a night of poker, before the reveille (the game's night
    /// leaves olive karts black on black tarmac, its low morning sun shows them off).
    /// </summary>
    private const double Race = 7.7;
    /// <summary>The moonlight of the yard at night.</summary>
    private const float Moonlight = 1.2f;

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
        new() { Who = Muller, Ride = Kart, Setup = Army, At = RingFrom, Toward = RingTo, Drive = Drive.Road, Route = "ring", Arc = arc, Skill = 1.1f, Aggression = 1f },
        new() { Who = Krasniqi, Ride = Kart, Setup = Army, At = RingFrom, Toward = RingTo, Drive = Drive.Road, Route = "ring", Arc = arc - 7, Skill = 1.15f, Aggression = 1f },
        new() { Who = Favre, Ride = Kart, Setup = Army, At = RingFrom, Toward = RingTo, Drive = Drive.Road, Route = "ring", Arc = arc - 14, Skill = 1.05f, Aggression = 0.8f },
        new() { Who = Bernasconi, Ride = Kart, Setup = Army, At = RingFrom, Toward = RingTo, Drive = Drive.Road, Route = "ring", Arc = arc - 21, Skill = 1.0f, Aggression = 0.6f },
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
            new(default, 30, default, default) { InSet = DormSet.Bed(1, 3) + new Vector3(-0.4f, 0.1f, 0.1f), Item = ItemId.Gamelle },
            new(default, 10, default, default) { InSet = DormSet.Table + new Vector3(0.05f, 0.05f, 0.1f), Item = ItemId.PokerChips },
            new(default, 75, default, default) { InSet = DormSet.Table + new Vector3(-0.15f, 0.05f, -0.15f), Item = ItemId.PlayingCards, Scale = 0.8f },
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
            Number = 1, Name = "Lights out", FromBar = 1, Bars = 2, Song = S, Hour = 22.8, Moon = Moonlight, FadeIn = 1.2,
            Cast = [.. Yard()],
            Keys =
            [
                // low along the rows, the vehicles dark against the barracks' lit windows
                new(0, Pt.At(2558392, 1169306, 6.5f), Pt.At(2558352, 1169352, 3f), 28),
                new(3.97, Pt.At(2558389, 1169318, 4.5f), Pt.At(2558350, 1169360, 3f), 28),
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
                new() { Who = Krasniqi, At = default, InSet = new Vector3(0.95f, 0f, -1.4f), Heading = 270, Dance = Cheer, Item = Cards },
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
                new() { Who = Muller, At = default, InSet = new Vector3(-0.95f, 0f, -0.75f), Heading = 100, Dance = 27, Item = Beer },
            ],
            Preroll = 0.4,
            Chat = [new(0.2, Muller, "karts in garage 4. who's u-booting?"), new(1.2, Bernasconi, "polenta-fraktion is in")],
        },

        // ---- the race round the barracks ------------------------------------------------------------
        new()
        {
            Number = 6, Name = "Out", FromBar = 8, Bars = 2, Song = S, Hour = Race, Traffic = 0,
            Supers = [new(0.2, 2.6, "05:58")],
            Cast = [.. Karts(40), .. Yard()],
            Keys =
            [
                new(0, Pt.Road("ring", 75, -2.8f, 0.7f), Pt.On(0, 0f, 0.6f), 24),
                new(3.97, Pt.Road("ring", 75, -2.8f, 0.7f), Pt.On(3, 0f, 0.6f), 24),
            ],
            Smooth = 0.08f, Preroll = 3,
        },
        new()
        {
            Number = 7, Name = "From above", FromBar = 10, Bars = 1, Song = S, Hour = Race, Traffic = 0,
            Cast = [.. Karts(150)],
            Keys =
            [
                new(0, Pt.On(1, 0f, 14f, 5f), Pt.On(1, 0f, 0f, -4f), 28),
                new(1.98, Pt.On(1, 0f, 15f, 3f), Pt.On(1, 0f, 0f, -6f), 28),
            ],
            Smooth = 0.15f, Preroll = 3,
        },
        new()
        {
            Number = 8, Name = "Onboard", FromBar = 11, Bars = 1, Song = S, Hour = Race, Traffic = 0,
            Cast = [.. Karts(40), .. Yard()],
            Keys =
            [
                new(0, Pt.Cockpit(3, -0.8f, 0.45f), Pt.Cockpit(3, 20f, -0.6f), 24),
                new(1.98, Pt.Cockpit(3, -0.8f, 0.45f), Pt.Cockpit(3, 20f, -0.6f), 24),
            ],
            Preroll = 3,
        },
        new()
        {
            Number = 9, Name = "The pitch corner", FromBar = 12, Bars = 1, Song = S, Hour = Race, Traffic = 0,
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
            Number = 10, Name = "Caught", FromBar = 13, Bars = 1, Song = S, Hour = Race, Traffic = 0,
            Cast =
            [
                // ROCHAT's G-Class rolls in from the east, lights on, and stops; the karts have stopped dead
                new()
                {
                    Who = Rochat, Ride = GClass, At = new Spot(2558434, 1169508), Heading = 270, Lights = true,
                    Drive = Drive.Controls, Controls = t => t < 1.9 ? Pedal(0.25f) : Pedal(0f, 0f, 1f),
                },
                new() { Who = Muller, Ride = Kart, Setup = Army, At = new Spot(2558419, 1169506.5), Heading = 92 },
                new() { Who = Krasniqi, Ride = Kart, Setup = Army, At = new Spot(2558414, 1169510.5), Heading = 80 },
                new() { Who = Favre, Ride = Kart, Setup = Army, At = new Spot(2558410, 1169504), Heading = 100 },
                new() { Who = Bernasconi, Ride = Kart, Setup = Army, At = new Spot(2558405, 1169509), Heading = 86 },
            ],
            Keys =
            [
                new(0, Pt.At(2558397, 1169507.5, 1.1f), Pt.At(2558470, 1169508, 1.3f), 35),
                new(1.98, Pt.At(2558399, 1169507.5, 1.0f), Pt.At(2558470, 1169508, 1.3f), 35),
            ],
            Preroll = 1,
            Chat = [new(0.3, Rochat, "RECRUITS. LANDSCHADEN. ZS.")],
        },

        // ---- dawn: the photo ------------------------------------------------------------------------
        new()
        {
            Number = 11, Name = "Souvenir", FromBar = 14, Bars = 2, Song = S, Hour = 8.1, Traffic = 0,
            Supers = [new(0.0, 0.9, "07:30 · HV")],
            Cast =
            [
                new() { Who = Muller, At = new Spot(2558396, 1169338), Heading = 270, Dance = Salute },
                new() { Who = Krasniqi, At = new Spot(2558396, 1169339.2), Heading = 270, Dance = Salute },
                new() { Who = Favre, At = new Spot(2558396, 1169340.4), Heading = 270, Dance = Salute },
                new() { Who = Bernasconi, At = new Spot(2558396, 1169341.6), Heading = 270, Dance = Salute },
                new() { Who = Rochat, Ride = GClass, At = new Spot(2558404, 1169336), Heading = 0 },
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
