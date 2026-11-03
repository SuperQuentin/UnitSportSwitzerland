// Plain C#, no Godot: linked into the unit tests.

namespace UnitSport.Concert;

/// <summary>What the show is doing now: the section of a song, or between them.</summary>
public enum ShowSection : byte { WalkIn, Calm, Groove, Chorus, Peak, Blackout }
