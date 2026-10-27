using System;
using System.Collections.Generic;
using System.Text;

namespace UnitSport.Adventure.Navigation;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/pins.md).

/// <summary>One map pin: LV95 metres, an icon (<see cref="PinIcon"/>), a short note, shared with the party or not.</summary>
public sealed class Pin
{
    public int Id { get; set; }
    public double E { get; set; }
    public double N { get; set; }
    public byte Icon { get; set; }
    public string Note { get; set; } = "";
    public bool Shared { get; set; }
    /// <summary>Unix seconds.</summary>
    public long Created { get; set; }
}

/// <summary>The six pin icons, drawn in code (<c>Ui/NavIcons</c>).</summary>
public enum PinIcon : byte { Flag, Star, Edelweiss, House, Camera, Warning }

/// <summary>
/// The <c>pins</c> section (#1226, navigation plan 2): up to <see cref="Max"/> pins, ids that only grow (never
/// reused within a book), notes of at most <see cref="NoteMax"/> characters with control characters removed.
/// The server changes it on the owner's request and touches it; shared pins also go to the party, never into
/// another player's book.
/// </summary>
public sealed class PinBook
{
    public static readonly SectionKey<PinBook> Section = new("pins", Version: 1);

    public const int Max = 30, NoteMax = 24, Icons = 6;

    public List<Pin> Pins { get; set; } = new();
    public int NextId { get; set; } = 1;

    public Pin? Find(int id) => Pins.Find(p => p.Id == id);

    /// <summary>Adds a pin; null and why not when full or the icon is unknown.</summary>
    public Pin? Add(double e, double n, byte icon, string? note, bool shared, long now, out string? why)
    {
        why = Pins.Count >= Max ? $"{Max} pins at most: remove one first." : icon >= Icons ? "No such pin." : null;
        if (why != null || double.IsNaN(e) || double.IsNaN(n)) { why ??= "No such place."; return null; }
        var pin = new Pin { Id = NextId++, E = e, N = n, Icon = icon, Note = Sanitize(note), Shared = shared, Created = now };
        Pins.Add(pin);
        return pin;
    }

    /// <summary>Changes a pin's icon, note and sharing; false when there is no such pin or icon.</summary>
    public bool Edit(int id, byte icon, string? note, bool shared)
    {
        if (Find(id) is not { } pin || icon >= Icons) return false;
        pin.Icon = icon;
        pin.Note = Sanitize(note);
        pin.Shared = shared;
        return true;
    }

    public bool Remove(int id) => Pins.RemoveAll(p => p.Id == id) > 0;

    /// <summary>The pins the party sees, into <paramref name="into"/> (cleared first).</summary>
    public void SharedPins(List<Pin> into)
    {
        into.Clear();
        foreach (var p in Pins) if (p.Shared) into.Add(p);
    }

    /// <summary>Trimmed, control characters removed, at most <see cref="NoteMax"/> characters.</summary>
    public static string Sanitize(string? note)
    {
        if (string.IsNullOrEmpty(note)) return "";
        var sb = new StringBuilder(Math.Min(note.Length, NoteMax));
        foreach (char c in note)
            if (!char.IsControl(c) && !char.IsSurrogate(c)) sb.Append(c);
        string s = sb.ToString().Trim();
        return s.Length > NoteMax ? s[..NoteMax].TrimEnd() : s;
    }
}
