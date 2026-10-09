namespace UnitSport.Core;

/// <summary>
/// A static field or property the playtest suite may change while the game runs (#751,
/// docs/notes/general/playtest.md): Claude lists them with <c>list_tunables</c> and sets them with
/// <c>set_tunable</c>, so a "the bus is too floaty" turns into a live change the player feels at once.
/// A <c>const</c> cannot be tuned: turn the one in question into a <c>static</c> field with this tag
/// (numbers, bools and strings). Only the Debug build's playtest server reads the tag, so it costs
/// nothing anywhere else; keep the value the same as the const was.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class TunableAttribute(string? hint = null) : Attribute
{
    /// <summary>What the value does and its sensible range, for whoever turns it.</summary>
    public string? Hint { get; } = hint;
}
