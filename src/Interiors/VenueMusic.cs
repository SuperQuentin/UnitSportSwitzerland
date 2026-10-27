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
    public const float RoomVolume = 0.5f, DoorVolume = 0.12f;

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
                    Position = new Vector3((r.X0 + r.X1) / 2, l.FloorY(f) + Math.Min(2.3f, l.ClearOf(r) - 0.3f), (r.Z0 + r.Z1) / 2),
                });
            }
    }

    /// <summary>The quiet source just inside a venue's door, for the street; null for a venue that plays nothing.</summary>
    public static VenueMusicSource? AtDoor(DoorSpot d, VenueRecord v)
    {
        var outward = d.Outward with { Y = 0 };
        int station = StationFor(v.Type, v.Cuisine, RegionOf(v.E, v.N));
        if (station == 0 || outward.LengthSquared() < 1e-4f) return null;
        return new VenueMusicSource
        {
            Name = "Music",
            Station = station,
            Volume = DoorVolume,
            Position = d.Position - outward.Normalized() * 0.5f + Vector3.Up * 1.6f,
        };
    }
}
