namespace UnitSport.Core;

/// <summary>
/// What joining costs in data (#63), measured, for the metered-connection warning: a client with
/// an empty cache joining a server with the real terrain, until the tiles around the spawn settle.
/// Measured Oct 2026: a fresh desktop client, 150 s after joining a real-terrain server (15 rings, 60 km).
/// </summary>
public static class StreamEstimate
{
    /// <summary>MB received on arrival at the default (Standard) settings.</summary>
    public const int ArrivalMb = 480;

    /// <summary>MB received on arrival with Low data.</summary>
    public const int ArrivalLowMb = 290;
}
