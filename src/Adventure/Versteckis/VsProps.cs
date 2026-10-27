using System;
using System.Collections.Generic;

namespace UnitSport.Adventure.Versteckis;

// Plain C#, no Godot: linked into the unit tests (VersteckisTests; docs/notes/adventure/versteckis.md).

/// <summary>What a hider can be (#1268): the seven village props. Sent as a byte: <b>append only</b>.</summary>
public enum VsProp : byte
{
    None = 0,
    HayBale = 1,
    Woodpile = 2,
    MilkCan = 3,
    Gnome = 4,
    Bench = 5,
    Cow = 6,
    FlowerBox = 7,
}

/// <summary>The sounds a game makes (Moo, the poof, the sprint's thumps): a byte on the wire, <b>append only</b>.</summary>
public enum VsVoice : byte
{
    Rustle = 0, Knock = 1, Clank = 2, Squeak = 3, Creak = 4, Moo = 5, Buzz = 6,
    /// <summary>A disguise changed, a decoy tagged: the poof.</summary>
    Poof = 7,
    /// <summary>A disguised sprint: the thumps the seeker hears within 25 m.</summary>
    Thump = 8,
    /// <summary>The church bell rung (an objective).</summary>
    Bell = 9,
}

/// <summary>
/// One prop (#1268): its name, its footprint box in metres (width across, depth along its facing, height), the voice its
/// Moo makes and the most a body disguised as it walks.
/// </summary>
public readonly record struct VsPropInfo(VsProp Kind, string Name, float Width, float Depth, float Height, VsVoice Voice, float WalkCap);

/// <summary>The seven props' table (#1268, plan section 3): the meshes are <c>VsPropMeshes</c>', the rules here.</summary>
public static class VsProps
{
    /// <summary>A disguised body's walk, m/s (no run, no jump); a cow is slower.</summary>
    public const float WalkCap = 2.5f, CowWalk = 1.5f;

    public static readonly VsPropInfo[] All =
    {
        new(VsProp.HayBale, "Hay bale", 1.2f, 1.2f, 1.25f, VsVoice.Rustle, WalkCap),
        new(VsProp.Woodpile, "Woodpile", 1.6f, 0.6f, 1.1f, VsVoice.Knock, WalkCap),
        new(VsProp.MilkCan, "Milk can", 0.4f, 0.4f, 0.7f, VsVoice.Clank, WalkCap),
        new(VsProp.Gnome, "Garden gnome", 0.35f, 0.35f, 0.6f, VsVoice.Squeak, WalkCap),
        new(VsProp.Bench, "Bench", 1.6f, 0.6f, 0.85f, VsVoice.Creak, WalkCap),
        new(VsProp.Cow, "Cow", 0.8f, 2.3f, 1.4f, VsVoice.Moo, CowWalk),
        new(VsProp.FlowerBox, "Flower box", 1.0f, 0.4f, 0.84f, VsVoice.Buzz, WalkCap),
    };

    public const int Count = 7;

    /// <summary>A prop's row; the hay bale's for <see cref="VsProp.None"/> or an unknown byte (never thrown on a forged one).</summary>
    public static VsPropInfo Of(VsProp kind) => kind is > VsProp.None and <= VsProp.FlowerBox ? All[(int)kind - 1] : All[0];

    public static bool Valid(int kind) => kind is >= 1 and <= Count;

    /// <summary>"a milk can", for the lines a player reads.</summary>
    public static string A(VsProp kind)
    {
        string name = Of(kind).Name.ToLowerInvariant();
        return (name[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an " : "a ") + name;
    }
}
