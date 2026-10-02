using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Audio.Cd;

/// <summary>
/// The broad feel of a track, decided once by <see cref="BeatAnalyzer"/> when the CD is burnt.
/// Picks which dance moves the figures draw from (<c>Avatar.HumanMeshBuilder.ApplyDance</c>).
/// Replicated as an int: append only.
/// </summary>
public enum MusicStyle { Pop = 0, Rock = 1, Electronic = 2, HipHop = 3, Chill = 4, Folk = 5 }

/// <summary>
/// One burnt CD: the facts every peer needs to play it in time and dance to it. The audio itself
/// is <c>&lt;Id&gt;.ogg</c> beside <c>&lt;Id&gt;.json</c> in the server's CD directory
/// (<see cref="CdLibrary"/>), streamed to clients through <see cref="CdCache"/>.
/// </summary>
/// <param name="Duration">Seconds.</param>
/// <param name="Bpm">Tempo.</param>
/// <param name="BeatOffset">Seconds from the start of the file to the first beat.</param>
/// <param name="Energy">0..1, loudness of the track overall.</param>
public sealed record CdInfo(int Id, string Title, float Duration, float Bpm, float BeatOffset, MusicStyle Style, float Energy)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static CdInfo? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<CdInfo>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>For an RPC argument: everything as plain variants.</summary>
    public Godot.Collections.Dictionary ToDict() => new()
    {
        ["id"] = Id, ["title"] = Title, ["duration"] = Duration, ["bpm"] = Bpm,
        ["offset"] = BeatOffset, ["style"] = (int)Style, ["energy"] = Energy,
    };

    public static CdInfo FromDict(Godot.Collections.Dictionary d) => new(
        d["id"].AsInt32(), d["title"].AsString(), d["duration"].AsSingle(), d["bpm"].AsSingle(),
        d["offset"].AsSingle(), (MusicStyle)d["style"].AsInt32(), d["energy"].AsSingle());

    /// <summary>One line for the UI and the logs: "Title · 128 bpm · Electronic · 3:42".</summary>
    public string Describe() =>
        $"{Title} · {Bpm.ToString("F0", CultureInfo.InvariantCulture)} bpm · {Style} · {(int)Duration / 60}:{(int)Duration % 60:D2}";
}
