using Godot;
using UnitSport.Audio.Live;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// What each venue plays (#1244, part 6 of #1232): a live station on the server's relay, chosen by
/// the venue's kind (research in <c>docs/notes/terrain/venue-*.md</c>, the rules in
/// <c>docs/notes/audio/venue-music.md</c>). Sources: one in each venue room, heard as the room's music,
/// and a quiet one just inside each venue's door, the music the street hears through the wall.
/// The tracker-module fallback (offline, or without the relay) is still to come.
/// </summary>
public static class VenueMusic
{
    /// <summary>A room's speaker, and the one behind the door that the street hears.</summary>
    public const float RoomVolume = 0.5f, DoorVolume = 0.2f;

    /// <summary>How far out from the door's wall the street's source stands, m (#1386).</summary>
    public const float DoorOut = 0.25f;

    /// <summary>What a cinema hall plays while its screen is on (#1279): Radio Swiss Classic, the nearest the relay has to film music.</summary>
    public const int FilmStation = 10;

    /// <summary>
    /// Which part of the country a tile lies in, for the stations that speak its language: Romandie
    /// west of about E 2'570'000, Ticino and the Italian valleys south of N 1'170'000 east of E 2'680'000.
    /// </summary>
    public enum Region { German, French, Italian }

    public static Region RegionOf(double e, double n) =>
        e < 2_570_000 ? Region.French : n < 1_170_000 && e > 2_680_000 ? Region.Italian : Region.German;

    /// <summary>A venue's station by its kind (0: none). A Beiz plays its region's SRG first channel.</summary>
    public static int StationFor(VenueType type, string cuisine, Region region)
    {
        string c = cuisine.ToLowerInvariant();
        return type switch
        {
            VenueType.Bowling => 12,                                     // One FM: pop and dance
            VenueType.Arcade => 3,                                       // SRF Virus
            VenueType.Cinema => 8,                                       // Radio Swiss Pop, in the lobby
            VenueType.Restaurant when c.Contains("pizz") || c.Contains("italian") => 6,   // RSI Rete Uno
            VenueType.Restaurant => region switch { Region.French => 4, Region.Italian => 6, _ => 1 },
            VenueType.Cafe => 9,                                         // Radio Swiss Jazz
            VenueType.FastFood => 11,                                    // Energy Bern
            VenueType.Bar => 14,                                         // Radio Paradise
            VenueType.Pub or VenueType.Biergarten => 2,                  // SRF 3: rock and pop
            VenueType.Nightclub => 5,                                    // Couleur 3
            _ => 0,
        };
    }

    /// <summary>A room's station by its kind (0: a room that plays nothing, an auditorium or a kitchen).</summary>
    public static int StationFor(RoomType room, Region region) => room switch
    {
        RoomType.BowlingLanes or RoomType.Concourse or RoomType.Mezzanine => StationFor(VenueType.Bowling, "", region),
        RoomType.ArcadeHall => StationFor(VenueType.Arcade, "", region),
        RoomType.BilliardHall => StationFor(VenueType.Bar, "", region),
        RoomType.CinemaLobby => StationFor(VenueType.Cinema, "", region),
        RoomType.Gaststube => StationFor(VenueType.Restaurant, "", region),
        RoomType.Pizzeria => StationFor(VenueType.Restaurant, "pizza", region),
        RoomType.CafeRoom => StationFor(VenueType.Cafe, "", region),
        RoomType.FastFoodRoom or RoomType.KebabRoom => StationFor(VenueType.FastFood, "", region),
        RoomType.BarRoom => StationFor(VenueType.Bar, "", region),
        RoomType.PubRoom => StationFor(VenueType.Pub, "", region),
        RoomType.ClubRoom => StationFor(VenueType.Nightclub, "", region),
        _ => 0,
    };

    /// <summary>What a venue plays when the relay is silent (#1244), by its kind.</summary>
    public static Audio.Tracker.VenueMood MoodFor(VenueType type, string cuisine)
    {
        string c = cuisine.ToLowerInvariant();
        return type switch
        {
            VenueType.Bowling => Audio.Tracker.VenueMood.Bowling,
            VenueType.Arcade => Audio.Tracker.VenueMood.Arcade,
            VenueType.Cinema => Audio.Tracker.VenueMood.Cinema,
            VenueType.Restaurant when c.Contains("pizz") || c.Contains("italian") => Audio.Tracker.VenueMood.Pizzeria,
            VenueType.Restaurant => Audio.Tracker.VenueMood.Beiz,
            VenueType.Cafe => Audio.Tracker.VenueMood.Cafe,
            VenueType.FastFood => Audio.Tracker.VenueMood.FastFood,
            VenueType.Bar => Audio.Tracker.VenueMood.Bar,
            VenueType.Pub or VenueType.Biergarten => Audio.Tracker.VenueMood.Pub,
            VenueType.Nightclub => Audio.Tracker.VenueMood.Club,
            _ => Audio.Tracker.VenueMood.None,
        };
    }

    /// <summary>A room's mood by its kind.</summary>
    public static Audio.Tracker.VenueMood MoodFor(RoomType room) => room switch
    {
        RoomType.BowlingLanes or RoomType.Concourse or RoomType.Mezzanine => Audio.Tracker.VenueMood.Bowling,
        RoomType.ArcadeHall => Audio.Tracker.VenueMood.Arcade,
        RoomType.BilliardHall => Audio.Tracker.VenueMood.Bar,
        RoomType.CinemaLobby => Audio.Tracker.VenueMood.Cinema,
        RoomType.Gaststube => Audio.Tracker.VenueMood.Beiz,
        RoomType.Pizzeria => Audio.Tracker.VenueMood.Pizzeria,
        RoomType.CafeRoom => Audio.Tracker.VenueMood.Cafe,
        RoomType.FastFoodRoom or RoomType.KebabRoom => Audio.Tracker.VenueMood.FastFood,
        RoomType.BarRoom => Audio.Tracker.VenueMood.Bar,
        RoomType.PubRoom => Audio.Tracker.VenueMood.Pub,
        RoomType.ClubRoom => Audio.Tracker.VenueMood.Club,
        _ => Audio.Tracker.VenueMood.None,
    };

    /// <summary>The region of a plan, from its key's tile (<c>E_N_index</c>, kilometres).</summary>
    public static Region RegionOf(InteriorLayout l)
    {
        var parts = l.Key.Split('_');
        return parts.Length >= 2 && int.TryParse(parts[0], out int e) && int.TryParse(parts[1], out int n)
            ? RegionOf(e * 1000.0 + 500, n * 1000.0 + 500) : Region.German;
    }

    /// <summary>A source in every venue room of a built interior, at its centre, under the ceiling.</summary>
    public static void AddTo(InteriorNode node)
    {
        var l = node.Layout;
        var region = RegionOf(l);
        for (int f = 0; f < l.Floors.Count; f++)
            foreach (var r in l.Floors[f].Rooms)
            {
                int station = StationFor(r.Type, region);
                if (station == 0) continue;
                node.AddChild(new VenueMusicSource
                {
                    Name = $"Music_{r.Type}_{f}",
                    Station = station,
                    Volume = RoomVolume,
                    Mood = MoodFor(r.Type),
                    Salt = (uint)Core.Fnv.Hash(l.Key),
                    Position = new Vector3((r.X0 + r.X1) / 2, l.FloorY(f) + Math.Min(2.3f, l.ClearOf(r) - 0.3f), (r.Z0 + r.Z1) / 2),
                    // the whole room at full level, not only its middle (#1279)
                    Spread = Math.Max(3f, new Vector2(r.Width, r.Depth).Length() / 2),
                });
            }
    }

    /// <summary>The quiet source just inside a venue's door, for the street; null for a venue that plays nothing.</summary>
    /// <param name="building">The building's key (<see cref="BuildingKey"/>), the same salt as its rooms' sources, so the street hears the bar the room plays.</param>
    public static VenueMusicSource? AtDoor(DoorSpot d, VenueRecord v, BuildingKey building)
    {
        var outward = d.Outward with { Y = 0 };
        int station = StationFor(v.Type, v.Cuisine, RegionOf(v.E, v.N));
        if (station == 0 || outward.LengthSquared() < 1e-4f) return null;
        return new VenueMusicSource
        {
            Name = "Music",
            Station = station,
            Volume = DoorVolume,
            Mood = MoodFor(v.Type, v.Cuisine),
            Salt = (uint)Core.Fnv.Hash(building.ToString()),
            // over the door, a hand out from the wall: from the pavement nothing stands between (inside it, Hearing
            // took the building's wall: 10 dB and the highs, on top of its low level, #1386); distance still dulls it
            Position = d.Position + outward.Normalized() * DoorOut + Vector3.Up * 1.8f,
        };
    }
}
