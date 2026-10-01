namespace UnitSport.Audio.Live;

/// <summary>One live station a car radio can tune to. Ids are append-only: they are replicated.</summary>
public readonly record struct Station(int Id, string Name, string Url);

/// <summary>
/// The stations a car radio offers, live Icecast/HTTP MP3 streams that answered when the list was
/// made (Oct 2026). Only the server ever opens a URL (<see cref="StationTap"/>); clients see ids
/// and names. 0 is "off". Append new stations at the end, never renumber.
/// </summary>
public static class Stations
{
    public static readonly Station[] All =
    {
        new(1, "SRF 1", "https://stream.srg-ssr.ch/srgssr/srf1/mp3/128"),
        new(2, "SRF 3", "https://stream.srg-ssr.ch/srgssr/srf3/mp3/128"),
        new(3, "SRF Virus", "https://stream.srg-ssr.ch/srgssr/srfvirus/mp3/128"),
        new(4, "RTS La 1ère", "https://stream.srg-ssr.ch/srgssr/la-1ere/mp3/128"),
        new(5, "RTS Couleur 3", "https://stream.srg-ssr.ch/srgssr/couleur3/mp3/128"),
        new(6, "RSI Rete Uno", "https://stream.srg-ssr.ch/srgssr/reteuno/mp3/128"),
        new(7, "RTR Radio Rumantsch", "https://stream.srg-ssr.ch/srgssr/rr/mp3/128"),
        new(8, "Radio Swiss Pop", "https://stream.srg-ssr.ch/srgssr/rsp/mp3/128"),
        new(9, "Radio Swiss Jazz", "https://stream.srg-ssr.ch/srgssr/rsj/mp3/128"),
        new(10, "Radio Swiss Classic", "https://stream.srg-ssr.ch/srgssr/rsc_de/mp3/128"),
        new(11, "Energy Bern", "https://energybern.ice.infomaniak.ch/energybern-high.mp3"),
        new(12, "One FM", "https://onefm.ice.infomaniak.ch/onefm-high.mp3"),
        new(13, "FIP", "https://icecast.radiofrance.fr/fip-midfi.mp3"),
        new(14, "Radio Paradise", "https://stream.radioparadise.com/mp3-128"),
    };

    public static Station? For(int id) => id >= 1 && id <= All.Length ? All[id - 1] : null;

    public static string Name(int id) => For(id)?.Name ?? "Radio off";

    /// <summary>The next station after <paramref name="id"/> (or before, <paramref name="step"/> -1), through "off".</summary>
    public static int Step(int id, int step)
    {
        int n = All.Length + 1;
        return ((Math.Clamp(id, 0, All.Length) + step) % n + n) % n;
    }
}
