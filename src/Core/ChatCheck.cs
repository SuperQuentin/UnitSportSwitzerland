using Godot;
using UnitSport.Items;

namespace UnitSport.Core;

/// <summary>
/// <c>--chatcheck</c>: tab completion and <c>/spawn</c> parsing, offline and without a world.
/// Prints each failure and a RESULT line; exits non-zero if any case is wrong.
/// </summary>
public static class ChatCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--chatcheck") >= 0;

    private static int _failures;

    public static int Run()
    {
        var completer = new ChatCompleter
        {
            Places = (q, n) => new[] { "Sion", "Sierre", "La Chaux-de-Fonds" }
                .Where(p => p.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(n),
            Occasions = () => ["halloween", "christmas"],
            Players = () => ["Alice", "Albert", "Bob"],
        };

        // commands
        ExpectTexts(completer, "/", ["/help ", "/who ", "/me ", "/city ", "/occasion ", "/spawn "], "offline command list");
        ExpectTexts(completer, "/sp", ["/spawn "], "command prefix");
        ExpectTexts(completer, "/tp", [], "server-only command hidden offline");

        // items
        Expect(completer.Complete("/spawn ene").Any(s => s.Text == "/spawn EnergyBar"), "item prefix");
        Expect(completer.Complete("/spawn bar").Any(s => s.Text == "/spawn EnergyBar"), "item substring");
        Expect(completer.Complete("/spawn EnergyBar ").Count == 0, "no suggestions for the count");

        // towns have spaces
        ExpectTexts(completer, "/city La C", ["/city La Chaux-de-Fonds"], "town with spaces");
        ExpectTexts(completer, "/city si", ["/city Sion", "/city Sierre"], "town substring");

        // sub-commands and ids
        ExpectTexts(completer, "/occasion s", ["/occasion start", "/occasion stop"], "occasion sub-commands");
        ExpectTexts(completer, "/occasion start h", ["/occasion start halloween"], "occasion ids");
        Expect(completer.Complete("hello").Count == 0, "plain chat with no name in it is not completed");

        // player names in free text
        ExpectTexts(completer, "hi bo", ["hi Bob "], "a name in plain chat");
        ExpectTexts(completer, "hi al", ["hi Albert ", "hi Alice "], "names sharing a start");
        ExpectTexts(completer, "hi a", [], "one bare letter is not enough");
        ExpectTexts(completer, "@b", ["@Bob "], "an @mention");
        Expect(completer.Complete("@").Count == 3, "a bare @ lists everybody");
        ExpectTexts(completer, "/me waves at al", ["/me waves at Albert ", "/me waves at Alice "], "a name inside /me");
        ExpectTexts(completer, "hi Bob", [], "a name already whole");

        // command list carries the arguments, usage once a command is typed
        Expect(completer.Complete("/sp").FirstOrDefault().Detail == "<item> [count]", "command arguments shown");
        Expect(completer.Usage("/spawn ") == "/spawn <item> [count]", "usage of a typed command");
        Expect(completer.Usage("/sp") == null, "no usage while the command is still typed");
        Expect(completer.Usage("/tp x") == null, "no usage for a command hidden offline");

        // parsing
        Expect(Parses("energybar", ItemId.EnergyBar, 1), "id name");
        Expect(Parses("Energy bar 3", ItemId.EnergyBar, 3), "display name with spaces and a count");
        Expect(Parses("swiss-flag 2", ItemId.SwissFlag, 2), "dashes ignored");
        Expect(Parses("bread 100000", ItemId.Bread, ItemLookup.MaxSpawn), "count clamped");
        Expect(Parses("francs 5000", ItemId.Francs, 5000), "francs may exceed a stack");
        Expect(!ItemLookup.TryParse("", out _, out _, out _), "empty is refused");
        Expect(!ItemLookup.TryParse("nosuchitem", out _, out _, out string error) && error.Contains("nosuchitem"), "unknown item is named");
        Expect(!ItemLookup.TryParse("s", out _, out _, out _), "an ambiguous prefix is refused");

        // adding what was parsed
        var inv = Inventory.Scratch();
        int before = inv.Room(ItemId.Bread);
        inv.Add(ItemId.Bread, 7);
        Expect(inv.Room(ItemId.Bread) == before - 7, "spawned items take room");

        GD.Print($"[chatcheck] RESULT {(_failures == 0 ? "PASS" : $"FAIL ({_failures})")}");
        return _failures == 0 ? 0 : 1;
    }

    private static bool Parses(string text, ItemId id, int count) =>
        ItemLookup.TryParse(text, out var def, out int n, out _) && def.Id == id && n == count;

    private static void ExpectTexts(ChatCompleter c, string input, string[] want, string what)
    {
        var got = c.Complete(input).Select(s => s.Text).ToArray();
        // the exact list for the short ones; for the command list, the wanted ones must all be there
        bool ok = want.Length == 0 ? got.Length == 0 : want.All(w => got.Contains(w));
        Expect(ok, $"{what}: wanted [{string.Join(", ", want)}], got [{string.Join(", ", got)}]");
    }

    private static void Expect(bool ok, string what)
    {
        if (ok) return;
        _failures++;
        GD.PrintErr($"[chatcheck] FAILED: {what}");
    }
}
