using Godot;

namespace UnitSport.Occasions;

/// <summary>
/// UFO sightings (#863, docs/notes/occasions/ufo-sightings.md): on the first Friday of every month
/// (and the morning after), from 21:00 to 05:00 game time, saucers cross the night sky, come down
/// over the herds and beam a cow up, land in a meadow with their ramp down (anyone may board one and
/// fly it away), and now and then the mothership hangs over a valley. The sightings themselves are
/// <see cref="UfoSightings"/> (planned by the server, played back by every peer from the shared
/// clock); this class is the occasion's look and sound: a faint green cast in the night grade
/// (the Atmosphere facet) and an eerie theremin when a saucer is near (the Audio facet). Seeing the
/// saucers at all is the Decorations facet, so a player who turns the occasion off sees none.
/// </summary>
public sealed class UfoOccasion : Occasion
{
    public override string Id => OccasionIds.Ufo;
    public override string Title => "UFO sightings";

    /// <summary>An ordinary sun; the colour is <see cref="Grade"/>'s.</summary>
    public override OccasionAtmosphere? Atmosphere { get; } = new();

    /// <summary>Night only: the sky and the light take a faint green cast, as under a saucer's glow.</summary>
    public override (Color Tint, Color Sky) Grade(float el, Color tint, Color sky)
    {
        float night = 1f - Mathf.SmoothStep(-12f, -4f, el);
        tint = tint.Lerp(tint * new Color(0.86f, 1.06f, 0.92f), 0.6f * night);
        sky = sky.Lerp(new Color(0.10f, 0.26f, 0.20f), 0.22f * night);
        return (tint, sky);
    }

    private double _wailNext;

    /// <summary>A saucer within 400 m at night: now and then an eerie wail drifts down from it.</summary>
    public override void Ambience(OccasionAudio a)
    {
        if (a.Night < 0.5f || a.Now < _wailNext) return;
        _wailNext = a.Now + a.Rand(9f, 18f);
        if (UfoSightings.Instance?.NearestCraft(a.Ears, 400f) is not { } craft) return;
        if (a.Bank("ufo-wail", OccasionSounds.UfoWail, 3) is { } wail)
            a.Speak(wail, craft + Vector3.Down * 3f, a.Rand(0.94f, 1.06f), -6f, 40f, 700f);
    }
}
