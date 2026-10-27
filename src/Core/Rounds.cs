using System;
using System.Collections.Generic;

namespace UnitSport.Core;

// Plain C#, no Godot: linked into the unit tests (RoundsTests; docs/notes/core/rounds.md).

/// <summary>
/// One "in a round" registry for the server's game modes (#1268): the Battle Royale and Versteckis register
/// here, and the callers that used to ask the Battle Royale directly ask this instead
/// (<c>AdventureService.InMatch</c>, <c>ChatManager</c>'s refusals, <c>Structures.InMatch</c>,
/// <c>VehicleManager.MayPark</c>). The first mode registered answers first, so the Battle Royale, registered
/// first, keeps its own replies word for word.
/// <para>
/// <b>In a round</b>: an entrant of a lobby or a game, alive or out, until the round ends (the Adventure layer
/// is off for it). <b>Playing</b>: taking part right now, on equal terms (no teleporting, no admin tools on it).
/// </para>
/// </summary>
public static class Rounds
{
    /// <summary>A registered mode: its name, the label a refusal uses ("a Battle Royale match"), its two questions.</summary>
    public sealed record Mode(string Name, string Label, Func<long, bool> InRound, Func<long, bool> Playing)
    {
        /// <summary>Who may chat with whom while a round runs: null lets everyone hear; else the scope's key (spectators of one game).</summary>
        public Func<long, string?>? ChatScope { get; init; }

        /// <summary>The refusal when a player asks to teleport out of it ("No teleporting out of a Battle Royale. /br leave to give up.").</summary>
        public string NoTeleport { get; init; } = "";
    }

    private static readonly List<Mode> Modes = new();

    /// <summary>Every mode, in the order registered.</summary>
    public static IReadOnlyList<Mode> All => Modes;

    /// <summary>
    /// Registers a mode, or replaces the one of the same name in its place (a server world built again in one
    /// process: a check, a hosted game started twice).
    /// </summary>
    public static void Register(Mode mode)
    {
        for (int i = 0; i < Modes.Count; i++)
            if (Modes[i].Name == mode.Name)
            {
                Modes[i] = mode;
                return;
            }
        Modes.Add(mode);
    }

    public static void Register(string name, string label, Func<long, bool> inRound, Func<long, bool> playing) =>
        Register(new Mode(name, label, inRound, playing));

    /// <summary>Drops a mode (its server world is gone).</summary>
    public static void Unregister(string name) => Modes.RemoveAll(m => m.Name == name);

    /// <summary>Drops every mode (tests, a server world torn down).</summary>
    public static void Clear() => Modes.Clear();

    /// <summary>Whether <paramref name="peer"/> is an entrant of any mode's lobby or game, until its round ends.</summary>
    public static bool InRound(long peer)
    {
        foreach (var m in Modes)
            if (m.InRound(peer)) return true;
        return false;
    }

    /// <summary>Whether <paramref name="peer"/> plays any mode's game right now.</summary>
    public static bool Playing(long peer) => PlayingIn(peer) != null;

    /// <summary>The first mode <paramref name="peer"/> plays right now, or null.</summary>
    public static Mode? PlayingIn(long peer)
    {
        foreach (var m in Modes)
            if (m.Playing(peer)) return m;
        return null;
    }

    /// <summary>
    /// The chat scope <paramref name="peer"/> speaks in while a round runs (#1268: a caught hider talks to the game's
    /// spectators only, so nobody calls out the spots), or null: the open chat.
    /// </summary>
    public static string? ChatScope(long peer)
    {
        foreach (var m in Modes)
            if (m.ChatScope?.Invoke(peer) is { } scope) return scope;
        return null;
    }
}
