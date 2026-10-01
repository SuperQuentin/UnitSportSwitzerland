using UnitSport.Audio.Cd;

namespace UnitSport.Items;

/// <summary>
/// What a radio does when its CD ends (#211). Replicated as an int (<c>RadioBody.Mode</c>, the
/// last field of a <see cref="RadioPlay"/>): append only.
/// </summary>
public enum RadioMode { Once = 0, Repeat = 1, All = 2, Shuffle = 3 }

/// <summary>
/// The order the CDs come in and what follows one, the same for every kind of radio. Whoever
/// owns a radio's play state applies it when the CD runs out: the server for a radio in the world
/// (<c>RadioManager</c>), the holder for one in the hand and the driver for a car's
/// (<c>RadioUi</c>, which runs on every client). Nothing else changes: the next CD is a new
/// (CD, start) pair on the shared clock, which every speaker already follows.
/// </summary>
public static class RadioQueue
{
    public static RadioMode Clamp(int mode) => mode is >= 0 and <= (int)RadioMode.Shuffle ? (RadioMode)mode : RadioMode.Once;

    /// <summary>The mode after <paramref name="mode"/>, for the panel's one button.</summary>
    public static RadioMode Cycle(RadioMode mode) => (RadioMode)(((int)mode + 1) % 4);

    public static string Label(RadioMode mode) => mode switch
    {
        RadioMode.Repeat => "Repeat this CD",
        RadioMode.All => "Play the list",
        RadioMode.Shuffle => "Shuffle",
        _ => "Play once",
    };

    /// <summary>
    /// The CDs in play order: the shared ones by id (the order they were burnt), then, when
    /// <paramref name="withPersonal"/>, this player's own by title — the order the panel lists them.
    /// </summary>
    public static List<int> Order(CdLibrary? library, bool withPersonal)
    {
        var order = new List<int>();
        if (library == null) return order;
        order.AddRange(library.All.Keys.OrderBy(id => id));
        if (withPersonal) order.AddRange(library.Personal.OrderBy(kv => kv.Value.Title, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key));
        return order;
    }

    /// <summary>The CD <paramref name="step"/> places from <paramref name="current"/>, round the list; 0 when it is empty.</summary>
    public static int Step(int current, int step, IReadOnlyList<int> order)
    {
        if (order.Count == 0) return 0;
        int i = -1;
        for (int k = 0; k < order.Count; k++) if (order[k] == current) { i = k; break; }
        if (i < 0) return step >= 0 ? order[0] : order[^1];
        return order[((i + step) % order.Count + order.Count) % order.Count];
    }

    /// <summary>What follows <paramref name="current"/> when it ends; 0 = silence.</summary>
    public static int Following(int current, RadioMode mode, IReadOnlyList<int> order, Random random)
    {
        switch (mode)
        {
            case RadioMode.Repeat: return current;
            case RadioMode.All: return Step(current, 1, order);
            case RadioMode.Shuffle:
                var others = order.Where(id => id != current).ToList();
                return others.Count == 0 ? current : others[random.Next(others.Count)];
            default: return 0;
        }
    }

    /// <summary>
    /// The play that follows <paramref name="ended"/> on a radio its owner runs (hand, car), or
    /// null for silence. The next CD starts where the last one stopped on the clock when that is
    /// recent (no gap, no drift between peers), otherwise now.
    /// </summary>
    public static RadioPlay? Continue(RadioPlay ended, double serverNow, CdLibrary? library, Random random)
    {
        int next = Following(ended.CdId, ended.Mode, Order(library, withPersonal: true), random);
        if (next == 0) return null;
        float length = next == ended.CdId ? ended.Length : library?.Find(next)?.Duration ?? 0f;
        if (length <= 0) return null;
        double start = ended.StartedAt + ended.Length;
        if (serverNow - start > 2 || start > serverNow) start = serverNow;
        return new RadioPlay(next, start, length, ended.Mode);
    }
}
