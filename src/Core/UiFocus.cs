namespace UnitSport.Core;

/// <summary>
/// Whether an on-screen text field currently owns the keyboard.
///
/// `FootPlayer` and `SpectatorCamera` read *physical keys* directly every frame rather than
/// going through the input map, which is what makes them layout-independent — but it also
/// means a `LineEdit` grabbing focus does not stop them moving. Typing "west" into the chat
/// box would otherwise walk you into a lake. Every text-entry UI registers itself here and
/// the movement controllers check it before reading keys.
///
/// Godot UI all runs on the main thread, so no locking is needed.
/// </summary>
public static class UiFocus
{
    private static readonly HashSet<object> Owners = new();

    /// <summary>True while any registered UI is taking typed input.</summary>
    public static bool TextEntryActive { get; private set; }

    /// <summary>Registers or releases a keyboard capture for one UI.</summary>
    public static void Set(object owner, bool capturing)
    {
        if (capturing) Owners.Add(owner);
        else Owners.Remove(owner);

        TextEntryActive = Owners.Count > 0;
    }

    /// <summary>Drops every capture. Used when a mode tears its UI down.</summary>
    public static void Clear()
    {
        Owners.Clear();
        TextEntryActive = false;
    }
}
