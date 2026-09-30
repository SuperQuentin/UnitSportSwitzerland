using Godot;

namespace UnitSport.Occasions;

public static class OccasionIds
{
    public const string Halloween = "halloween";
    public const string Christmas = "christmas";
}

/// <summary>
/// How an occasion changes the day: the sun's timetable, and the snow, mist and lights the world
/// shaders read as globals. Everything defaults to an ordinary day, so an occasion only states
/// what it changes.
/// </summary>
public sealed record OccasionAtmosphere
{
    /// <summary>Hours. The default 6 / 18 / 62° is <see cref="World.DayNight"/>'s own summer day.</summary>
    public float Sunrise { get; init; } = 6f;
    public float Sunset { get; init; } = 18f;
    public float NoonElevation { get; init; } = 62f;

    /// <summary>0..1: how white upward-facing ground, roofs and crowns turn.</summary>
    public float Snow { get; init; }

    /// <summary>0..1: festive lights (eaves, trees) — they still only shine at night.</summary>
    public float Lights { get; init; }

    /// <summary>Low-lying mist, authored as a colour you would see (sRGB).</summary>
    public Color MistColor { get; init; } = new(0.7f, 0.72f, 0.76f);

    /// <summary>Extinction per metre inside the mist, by day and at night.</summary>
    public float MistDay { get; init; }
    public float MistNight { get; init; }

    /// <summary>Metres above the ground under the camera where the mist thins out to nothing.</summary>
    public float MistHeight { get; init; } = 60f;

    /// <summary>0..1: falling snow around the camera (<see cref="OccasionPrecip"/>); heavier at altitude.</summary>
    public float Snowfall { get; init; }
}

/// <summary>
/// The content of one occasion: what it looks and sounds like. When it runs, and with which
/// facets, is not decided here but by its <see cref="OccasionEntry"/> in <c>occasions.json</c>.
/// Every hook defaults to "no change", so an occasion overrides only what it has.
/// </summary>
public abstract class Occasion
{
    public abstract string Id { get; }
    public abstract string Title { get; }

    /// <summary>Null keeps the ordinary day.</summary>
    public virtual OccasionAtmosphere? Atmosphere => null;

    /// <summary>
    /// Re-grades <see cref="World.DayNight"/>'s colour script. Both colours are sRGB, as
    /// authored there; the result is converted to linear with them.
    /// </summary>
    public virtual (Color Tint, Color Sky) Grade(float sunElevationDeg, Color tint, Color sky) => (tint, sky);

    /// <summary>Dresses one tile (the Decorations facet). Main thread; must be deterministic in the tile.</summary>
    public virtual void Decorate(TileContext tile, DecorBuilder into) { }

    /// <summary>Puts this occasion's hunt spots on one tile (the Hunt facet). Deterministic, like <see cref="Decorate"/>.</summary>
    public virtual void PlaceHunt(TileContext tile, DecorBuilder into) { }

    /// <summary>The extra loot roll this occasion adds to a kind of furniture (the Loot facet), or null.</summary>
    public virtual (float Chance, Items.ItemId[] Items)? Treats(Interiors.FurnitureType type) => null;

    /// <summary>What a claimed hunt spot gives: mostly treats, now and then the rare hat.</summary>
    public virtual (Items.ItemId Id, int Count) HuntReward(Random rng) => (Items.ItemId.None, 0);

    /// <summary>The hat everyone wears while this occasion runs (the Hats facet).</summary>
    public virtual Avatar.Headwear Hat => Avatar.Headwear.None;

    /// <summary>Creatures in the air around the player (part of the Atmosphere facet).</summary>
    public virtual Flock[] Flocks => [];

    /// <summary>Schedules this occasion's sounds; called every frame while its Audio facet runs.</summary>
    public virtual void Ambience(OccasionAudio audio) { }

    /// <summary>The menu's chip-tune jingle (the Audio facet), as samples, or null.</summary>
    public virtual float[]? Jingle() => null;
}

/// <summary>
/// An occasion that exists only in <c>occasions.json</c> — a community event, say — with no
/// content of its own yet. It still runs, replicates and appears in the settings; it simply
/// changes nothing on screen until a class is written for it.
/// </summary>
public sealed class GenericOccasion : Occasion
{
    public GenericOccasion(string id)
    {
        Id = id;
        Title = id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..].Replace('-', ' ');
    }

    public override string Id { get; }
    public override string Title { get; }
}

/// <summary>Content classes by id. An id with no class gets a <see cref="GenericOccasion"/>.</summary>
public static class OccasionRegistry
{
    private static readonly Dictionary<string, Occasion> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [OccasionIds.Halloween] = new HalloweenOccasion(),
        [OccasionIds.Christmas] = new ChristmasOccasion(),
    };

    private static readonly Dictionary<string, Occasion> Generic = new(StringComparer.OrdinalIgnoreCase);

    public static Occasion Get(string id)
    {
        if (Known.TryGetValue(id, out var o)) return o;
        if (!Generic.TryGetValue(id, out o)) Generic[id] = o = new GenericOccasion(id);
        return o;
    }
}

/// <summary>One occasion running right now, with the facets this viewer actually gets.</summary>
public sealed record ActiveOccasion(Occasion Content, OccasionEntry Entry, OccasionFacets Facets, string Instance)
{
    public string Id => Entry.Id;
    public int Priority => Entry.Priority;
    public bool Has(OccasionFacets facet) => (Facets & facet) == facet;
}
