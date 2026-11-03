using System;

namespace UnitSport.Core;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// Seeds for what a film or a probe must play the same way twice (#924): the birds, the traffic,
/// the occasions' creatures, a crash's tumble, impact dust, vehicle ids. In normal play nothing is
/// pinned and every <see cref="Rng"/> is a fresh unseeded one, as before. A film's shot pins a
/// scope (<c>"story/12"</c>, then <c>"story/12/go"</c> when its actors are let go) with
/// <see cref="Begin"/>: from then each system's stream is seeded from (scope, its own name), and
/// the systems that live across shots (birds, traffic, creatures) start theirs again
/// (<see cref="Reseeded"/>), so a shot never depends on which shots were filmed before it.
/// </summary>
public static class RunSeeds
{
    /// <summary>The scope the seeds are drawn from; null in normal play (nothing pinned).</summary>
    public static string? Scope { get; private set; }

    public static bool Pinned => Scope != null;

    /// <summary>Raised on the main thread by <see cref="Begin"/>: a long-lived system starts its stream again.</summary>
    public static event Action? Reseeded;

    /// <summary>Pins <paramref name="scope"/> and tells the long-lived systems.</summary>
    public static void Begin(string scope)
    {
        Scope = scope;
        _ids = null;
        Reseeded?.Invoke();
    }

    /// <summary>Back to normal play: unseeded streams from the next one asked for.</summary>
    public static void End() => Scope = null;

    /// <summary>A stable seed for <paramref name="what"/> in the current scope (FNV-1a, never <c>string.GetHashCode</c>).</summary>
    public static int Of(string what) => Fnv.Hash((Scope ?? "") + "|" + what);

    /// <summary>A stream for <paramref name="what"/>: seeded from the scope when pinned, else unseeded.</summary>
    public static Random Rng(string what) => Scope == null ? new Random() : new Random(Of(what));

    private static Random? _ids;
    private static string? _idsScope;

    /// <summary>
    /// A fresh 63-bit id (a vehicle's <c>Oid</c>): random in normal play; when pinned, the scope's
    /// own sequence, so a shot's parked cars get the same ids every render.
    /// </summary>
    public static long NewId()
    {
        if (Scope == null) return Random.Shared.NextInt64(1, long.MaxValue);
        if (_ids == null || _idsScope != Scope)
        {
            _ids = new Random(Of("ids"));
            _idsScope = Scope;
        }
        return _ids.NextInt64(1, long.MaxValue);
    }
}
