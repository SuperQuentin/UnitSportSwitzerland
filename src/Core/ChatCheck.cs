using Godot;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Core;

/// <summary>
/// <c>--chatcheck</c>: tab completion, <c>/spawn</c> and <c>/time</c> parsing, <c>/stadium</c> and <c>/help</c>, and Up/Down through
/// what was sent, offline and without a world.
/// Prints each failure and a RESULT line; exits non-zero if any case is wrong.
/// </summary>
public static class ChatCheck
{
    public static bool Requested => CmdArgs.Has("--chatcheck");

    private static int _failures;

    public static int Run(Node host)
    {
        var completer = new ChatCompleter
        {
            Places = (q, n) => new[] { "Sion", "Sierre", "La Chaux-de-Fonds" }
                .Where(p => p.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(n),
            Occasions = () => ["halloween", "christmas"],
            Players = () => ["Alice", "Albert", "Bob"],
        };

        // commands
        ExpectTexts(completer, "/", ["/help ", "/who ", "/me ", "/city ", "/stadium ", "/occasion "], "offline command list");
        ExpectTexts(completer, "/sp", ["/spawn "], "command prefix");
        ExpectTexts(completer, "/kic", [], "server-only command hidden offline");

        // items
        Expect(completer.Complete("/spawn ene").Any(s => s.Text == "/spawn EnergyBar"), "item prefix");
        Expect(completer.Complete("/spawn bar").Any(s => s.Text == "/spawn EnergyBar"), "item substring");
        Expect(completer.Complete("/spawn EnergyBar ").Count == 0, "no suggestions for the count");
        ExpectTexts(completer, "/despawn ", ["/despawn vehicles", "/despawn items", "/despawn all"], "/despawn completes what");
        ExpectTexts(completer, "/despawn items e", ["/despawn items everywhere"], "/despawn completes everywhere");

        // towns have spaces
        ExpectTexts(completer, "/city La C", ["/city La Chaux-de-Fonds"], "town with spaces");
        ExpectTexts(completer, "/city si", ["/city Sion", "/city Sierre"], "town substring");

        // sub-commands and ids
        ExpectTexts(completer, "/occasion s", ["/occasion start", "/occasion stop"], "occasion sub-commands");
        ExpectTexts(completer, "/occasion start h", ["/occasion start halloween"], "occasion ids");
        ExpectTexts(completer, "/time s", ["/time set", "/time speed"], "time sub-commands");
        ExpectTexts(completer, "/time set n", ["/time set noon", "/time set night"], "named times");
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
        Expect(completer.Usage("/kick x") == null, "no usage for a command hidden offline");

        // parsing
        Expect(Parses("energybar", ItemId.EnergyBar, 1), "id name");
        Expect(Parses("Energy bar 3", ItemId.EnergyBar, 3), "display name with spaces and a count");
        Expect(Parses("swiss-flag 2", ItemId.SwissFlag, 2), "dashes ignored");
        Expect(Parses("bread 100000", ItemId.Bread, ItemLookup.MaxSpawn), "count clamped");
        Expect(Parses("francs 5000", ItemId.Francs, 5000), "francs may exceed a stack");
        Expect(!ItemLookup.TryParse("", out _, out _, out _), "empty is refused");
        Expect(!ItemLookup.TryParse("nosuchitem", out _, out _, out string error) && error.Contains("nosuchitem"), "unknown item is named");
        Expect(!ItemLookup.TryParse("s", out _, out _, out _), "an ambiguous prefix is refused");

        // /time
        Expect(TimeParses([], World.TimeOp.Query, 0), "bare /time asks");
        Expect(TimeParses(["query"], World.TimeOp.Query, 0), "/time query");
        Expect(TimeParses(["set", "noon"], World.TimeOp.Set, 12), "named hour");
        Expect(TimeParses(["SET", "Midnight"], World.TimeOp.Set, 0), "names ignore case");
        Expect(TimeParses(["set", "21:30"], World.TimeOp.Set, 21.5), "hh:mm");
        Expect(TimeParses(["set", "6.25"], World.TimeOp.Set, 6.25), "decimal hour, invariant culture");
        Expect(TimeParses(["set", "24"], World.TimeOp.Set, 0), "24 wraps to midnight");
        Expect(TimeParses(["14:00"], World.TimeOp.Set, 14), "a bare hour is a set");
        Expect(TimeParses(["add", "-2.5"], World.TimeOp.Add, -2.5), "add goes back too");
        Expect(TimeParses(["speed", "0"], World.TimeOp.Speed, 0), "speed 0 stops the clock");
        Expect(!World.TimeCommand.TryParse(["set", "25"], out _, out _, out _), "hour 25 refused");
        Expect(!World.TimeCommand.TryParse(["set", "12:60"], out _, out _, out _), "minute 60 refused");
        Expect(!World.TimeCommand.TryParse(["set"], out _, out _, out _), "set needs an hour");
        Expect(!World.TimeCommand.TryParse(["speed", "-1"], out _, out _, out _), "negative speed refused");
        Expect(!World.TimeCommand.TryParse(["add", "abc"], out _, out _, out string terr) && terr.Contains("abc"), "bad hours named");
        Expect(World.TimeCommand.Format(21.5) == "21:30" && World.TimeCommand.Format(23.9999999) == "23:59", "clock format");
        Expect(Math.Abs(World.TimeCommand.Advance(23, 60, 24) - 0) < 1e-9, "a minute of a 24-minute day is an hour, past midnight");
        Expect(World.TimeCommand.Advance(10, 600, 0) == 10, "a stopped clock stays");
        Expect(World.TimeCommand.Wrap(-1) == 23, "wrap back past midnight");

        // adding what was parsed
        var inv = Inventory.Scratch();
        int before = inv.Room(ItemId.Bread);
        inv.Add(ItemId.Bread, 7);
        Expect(inv.Room(ItemId.Bread) == before - 7, "spawned items take room");

        // the admin item and money commands (#262)
        ExpectTexts(completer, "/cat", ["/catalogue "], "catalogue command offline");
        ExpectTexts(completer, "/deb", ["/debug "], "debug menu command offline (#339)");
        ExpectTexts(completer, "/giv", [], "/give needs a server");
        ExpectTexts(completer, "/bank s", ["/bank set"], "bank verbs");
        Offline(host);

        Venues(completer, host);

        Locate(completer, host);

        History(host);

        GD.Print($"[chatcheck] RESULT {(_failures == 0 ? "PASS" : $"FAIL ({_failures})")}");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// A real <see cref="ChatUi"/> fed key events through the viewport, the way the focused box
    /// gets them: the LineEdit must not keep Up and Down for itself.
    /// </summary>
    private static void History(Node host)
    {
        var chat = ChatManager.CreateClient();
        var ui = ChatUi.Create(chat, new ChatCompleter());
        host.AddChild(chat);
        host.AddChild(ui);
        var input = (LineEdit)ui.FindChildren("*", "LineEdit", true, false)[0];

        void Press(Key key)
        {
            host.GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
            host.GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
        }
        void Send(string text)
        {
            ui.OpenInput(text);
            Press(Key.Enter);
        }
        void Step(Key key, string want, string what)
        {
            Press(key);
            Expect(input.Text == want && input.CaretColumn == want.Length,
                $"history {what}: wanted \"{want}\", got \"{input.Text}\" (caret {input.CaretColumn})");
        }

        Send("one");
        Expect(!ui.IsTyping, "Enter sends and closes");
        Send("two");
        Send("two");   // a repeat is one step
        Send("three");

        ui.OpenInput("dra");
        Step(Key.Up, "three", "first Up: newest");
        Step(Key.Up, "two", "a repeat is one step");
        Step(Key.Up, "one", "oldest");
        Step(Key.Up, "one", "stops at the oldest");
        Step(Key.Down, "two", "Down");
        Step(Key.Down, "three", "Down to the newest");
        Step(Key.Down, "dra", "past the newest: the draft is back");
        Step(Key.Down, "dra", "stays on the draft");
        ui.CloseInput(recaptureMouse: false);

        // past the log's cap (#837): trimming with QueueFree alone spun forever once a line had to go
        for (int i = 0; i < 100; i++) ui.Append($"line {i}", ChatKind.Say);
        Expect(ui.LogLines <= 80, $"the log keeps 80 lines (has {ui.LogLines})");

        ui.QueueFree();
        chat.QueueFree();
    }

    /// <summary>
    /// The item and money commands typed with no server: they act on this machine's inventory,
    /// and <c>/catalogue</c> asks for the panel (#262).
    /// </summary>
    private static void Offline(Node host)
    {
        var chat = ChatManager.CreateClient();
        var inv = Inventory.Scratch();
        chat.Inventory = inv;
        host.AddChild(chat);
        string last = "";
        chat.LineReceived += (line, _) => last = line;
        int opened = 0;
        chat.CatalogueRequested += () => opened++;
        int debugOpened = 0;
        chat.DebugRequested += () => debugOpened++;

        chat.Send("/spawn bread 3");
        Expect(inv.Contains(ItemId.Bread), $"/spawn puts it in the pack: {last}");
        int cash = inv.Cash;
        chat.Send("/money 2.5k");
        Expect(inv.Cash == cash + 2500, $"/money adds cash: {last}");
        chat.Send("/money -1000000");
        Expect(inv.Cash == 0, $"/money takes no more than there is: {last}");
        chat.Send("/money lots");
        Expect(last.StartsWith("Usage"), $"/money with no amount: {last}");
        inv.Add(ItemId.Francs, 40);
        chat.Send("/clear");
        Expect(!inv.Contains(ItemId.Bread) && inv.Cash == 40, $"/clear empties the pack, cash kept: {last}");
        chat.Send("/give Bob bread");
        Expect(last.Contains("/spawn"), $"/give offline points at /spawn: {last}");
        chat.Send("/catalogue");
        chat.Send("/ITEMS");
        Expect(opened == 2, $"/catalogue and /items open the panel offline ({opened})");
        chat.Send("/debug");
        Expect(debugOpened == 1, $"/debug opens the debug menu offline ({debugOpened}): {last}");

        // /despawn (#1200): measured from the camera, the near stone goes, the far one only "everywhere"
        chat.Send("/despawn items");
        Expect(last.StartsWith("Nowhere to measure from"), $"/despawn with no camera wants everywhere: {last}");
        var camera = new Camera3D();
        host.AddChild(camera);
        camera.MakeCurrent();
        var dropped = DroppedItems.Create(host, WorldOrigin.SwissDefault());
        dropped.Drop(new ItemStack(ItemId.Stone, 1), new Vector3(5, 0, 0), Vector3.Zero, Vector3.Zero, Vector3.Zero);
        dropped.Drop(new ItemStack(ItemId.Stone, 1), new Vector3(200, 0, 0), Vector3.Zero, Vector3.Zero, Vector3.Zero);
        chat.Send("/despawn trees");
        Expect(last.StartsWith("Usage: /despawn"), $"/despawn of something else: {last}");
        chat.Send("/despawn items");
        Expect(last == "Despawned 1 item within 30 m.", $"/despawn items takes the near stone: {last}");
        chat.Send("/despawn all everywhere");
        Expect(last == "Despawned 0 vehicles and 1 item everywhere.", $"/despawn all everywhere takes the far one: {last}");
        Expect(dropped.Items.All(i => i.IsQueuedForDeletion()), "no stone left");
        dropped.QueueFree();
        camera.QueueFree();

        chat.QueueFree();
    }

    /// <summary>
    /// <c>/stadium</c> (#994): its completion and its replies offline, <c>/tp</c> to coordinates,
    /// <c>/concert</c>'s completion and the help built from the command table.
    /// </summary>
    private static void Venues(ChatCompleter completer, Node host)
    {
        // finding a venue is unit-tested (StadiumLookupTests); here, what the chat makes of it
        ExpectTexts(completer, "/sta", ["/stadium "], "/stadium is offered offline");
        ExpectTexts(completer, "/stadium ", ["/stadium list", "/stadium Stade de Genève"], "/stadium offers the list and the venues");
        ExpectTexts(completer, "/stadium wank", ["/stadium Stadion Wankdorf"], "a venue completes from part of its name");
        ExpectTexts(completer, "/stadium list a", ["/stadium list arena"], "the list's kinds complete");
        ExpectTexts(completer, "/concert c", ["/concert cue", "/concert camera"], "/concert sub-commands complete");
        ExpectTexts(completer, "/concert cue fl", ["/concert cue flames"], "/concert cues complete");
        ExpectTexts(completer, "/concert input ", ["/concert input artnet", "/concert input osc"], "/concert input protocols complete");

        ExpectTexts(completer, "/concert j", ["/concert join"], "/concert join completes");
        ExpectTexts(completer, "/help sta", ["/help stadium"], "/help completes a command");

        // /help (#1173): one line per group, each command in exactly one of them
        static string Group(List<string> help, string name) => help.FirstOrDefault(l => l.TrimStart().StartsWith(name + ":")) ?? "";
        var offline = ChatCompleter.Help(online: false, isAdmin: true).ToList();
        Expect(Group(offline, "Travel").Contains("/stadium") && Group(offline, "Concerts & media").Contains("/concert") && Group(offline, "World").Contains("/water")
            && !offline.Any(l => l.Contains("/kick")), $"offline help: {string.Join(" | ", offline)}");
        var player = ChatCompleter.Help(online: true, isAdmin: false).ToList();
        Expect(Group(player, "Activities").Contains("/fight") && Group(player, "Travel").Contains("/stadium") && !player.Any(l => l.Contains("/kick"))
            && Group(player, "Admin") == "", $"a player's help: {string.Join(" | ", player)}");
        var admin = ChatCompleter.Help(online: true, isAdmin: true).ToList();
        Expect(Group(admin, "Admin").Contains("/kick") && Group(admin, "Travel").Contains("/tp "), $"an operator's help has the admin commands: {string.Join(" | ", admin)}");
        var all = admin.Skip(1).SelectMany(l => l[(l.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        Expect(all.Count == all.Distinct().Count() && all.Count == ChatCompleter.Visible(true, true).Count(), $"every command in one group: {all.Count} listed");
        string? stadiumHelp = ChatCompleter.HelpFor("/stadium", online: true, isAdmin: false);
        Expect(stadiumHelp?.StartsWith("/stadium [list") == true && stadiumHelp.Contains("— "), $"/help stadium: {stadiumHelp}");
        Expect(ChatCompleter.HelpFor("kick", online: true, isAdmin: false) is null, "/help kick is nothing for a player");

        var chat = ChatManager.CreateClient();
        host.AddChild(chat);
        var lines = new List<string>();
        chat.LineReceived += (line, _) => lines.Add(line);
        chat.Send("/stadium list arena");
        Expect(lines.Count == 1 && lines[0].StartsWith("Arenas (") && lines[0].Contains("Hallenstadion"), $"/stadium list arena offline: {string.Join(" | ", lines)}");
        lines.Clear();
        chat.Send("/stadium basel");
        Expect(lines.Count == 1 && lines[0].Contains("could be") && lines[0].Contains("St. Jakobshalle"), $"an ambiguous venue lists its matches: {string.Join(" | ", lines)}");
        lines.Clear();
        chat.Send("/stadium letzigrund");
        Expect(lines.Count == 2 && lines[0].Contains("tonight") && lines[1].Contains("Nothing to teleport"), $"a venue says what plays, then goes there: {string.Join(" | ", lines)}");
        lines.Clear();
        chat.Send("/tp 2600000 1200000");
        Expect(lines.Count == 1 && lines[0].Contains("Nothing to teleport"), $"/tp to coordinates works offline: {string.Join(" | ", lines)}");
        lines.Clear();
        chat.Send("/tp Bob");
        Expect(lines.Count == 1 && lines[0].Contains("multiplayer"), $"/tp to a player needs a server: {string.Join(" | ", lines)}");
        lines.Clear();
        chat.Send("/concert join");
        Expect(lines.Count == 1 && lines[0].StartsWith("No set is hosted"), $"/concert join with nothing hosted: {string.Join(" | ", lines)}");
        lines.Clear();
        chat.Send("/help concert");
        Expect(lines.Count == 1 && lines[0].StartsWith("/concert join |"), $"/help concert offline: {string.Join(" | ", lines)}");
        lines.Clear();
        chat.Send("/help");
        Expect(lines.Count > 3 && lines[0].StartsWith("Commands") && lines.Any(l => l.Contains("Travel:")), $"/help offline: {string.Join(" | ", lines)}");
        chat.QueueFree();
    }

    private static bool TimeParses(string[] args, World.TimeOp op, double hour) =>
        World.TimeCommand.TryParse(args, out var got, out double value, out _) && got == op && Math.Abs(value - hour) < 1e-9;

    private static bool Parses(string text, ItemId id, int count) =>
        ItemLookup.TryParse(text, out var def, out int n, out _) && def.Id == id && n == count;

    /// <summary>
    /// <c>/locate</c> and the chat's coordinates (#1277): the completion of nouns and selectors, the buttons under a
    /// line carrying <c>@E,N</c>, and the pad's picker walked from "/" to a whole command by its buttons alone.
    /// </summary>
    private static void Locate(ChatCompleter completer, Node host)
    {
        ExpectTexts(completer, "/loc", ["/locate "], "/locate is offered offline");
        ExpectTexts(completer, "/locate cas", ["/locate castle "], "a noun completes");
        ExpectTexts(completer, "/locate church ", ["/locate church + spire ", "/locate church in "], "the selectors after a noun");
        ExpectTexts(completer, "/locate church + par", ["/locate church + parking entrance "], "a selector completes");
        ExpectTexts(completer, "/locate castle in La", ["/locate castle in La Chaux-de-Fonds"], "a town after in");
        Expect(completer.Complete("/").Count > 14, $"a bare / lists every command ({completer.Complete("/").Count})");
        Expect(ChatUi.BackOneWord("/locate church + spire") == "/locate church + ", "Back takes a word");
        Expect(ChatUi.BackOneWord("/locate") == "/" && ChatUi.BackOneWord("/") == "", "Back down to nothing");

        var chat = ChatManager.CreateClient();
        var ui = ChatUi.Create(chat, completer);
        host.AddChild(chat);
        host.AddChild(ui);
        var sent = new List<string>();
        chat.LineReceived += (line, _) => sent.Add(line);

        // a /locate answer's line: no buttons under it; its pin and coordinates are a link that opens the choices
        ui.OpenInput();
        ui.Append("  Château Saint-Maire (fortress, VD) · 580 m NE · @2538395,1152919", ChatKind.Private);
        var labels = ui.FindChildren("*", "RichTextLabel", true, false).OfType<RichTextLabel>().ToList();
        var located = labels.LastOrDefault(l => l.FocusMode == Control.FocusModeEnum.All);
        Expect(located != null && located.GetParsedText().Replace("⁠", "").Contains("@2538395,1152919"), "the coordinates stay in the line, which a pad can focus");
        Expect(!ui.FindChildren("*", "Button", true, false).OfType<Button>().Any(b => b.Text == "Track" && b.IsVisibleInTree()), "no buttons until the link is clicked");
        located?.EmitSignal(RichTextLabel.SignalName.MetaClicked, "2538395,1152919|Château Saint-Maire");
        var choices = ui.ChoiceLabels.ToList();
        Expect(choices.Contains("Track") && choices.Contains("Show on map") && choices.Contains("Copy"), $"the link opens the choices [{string.Join(", ", choices)}]");
        Expect(choices.Contains("Teleport") == Permissions.CanTeleport, "Teleport only where teleporting is allowed");
        host.GetViewport().PushInput(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
        Expect(!ui.ChoiceLabels.Any() && ui.IsTyping, "Esc closes the choices first, the chat stays open");
        ui.CloseInput(recaptureMouse: false);
        Expect(World.Locate.LocateCoords.LabelOf("  Château Saint-Maire (fortress, VD) · 580 m NE · @2538395,1152919",
            World.Locate.LocateCoords.FindAll("  Château Saint-Maire (fortress, VD) · 580 m NE · @2538395,1152919")[0]) == "Château Saint-Maire",
            "the choices name the place");

        // the picker: "/" then locate, castle, Send, by pressing its buttons
        ui.OpenForPad();
        Expect(ui.IsTyping && ui.PickerLabels.Contains("/locate"), $"the pad opens the chat on the commands [{string.Join(", ", ui.PickerLabels.Take(8))}...]");
        void Press(string label)
        {
            var b = ui.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(x => x.Text == label && x.IsVisibleInTree() && !x.IsQueuedForDeletion());
            Expect(b != null, $"the picker has '{label}'");
            b?.EmitSignal(BaseButton.SignalName.Pressed);
        }
        Press("/locate");
        Expect(ui.PickerLabels.Contains("castle"), "after /locate the picker offers the nouns");
        Press("castle");
        Expect(ui.PickerLabels.Contains("+ spire") && ui.PickerLabels.Contains("Send"), "after a noun the selectors and Send");
        Press("Back");
        Expect(ui.PickerLabels.Contains("castle"), "Back returns to the nouns");
        ui.CloseInput(recaptureMouse: false);
        Expect(!ui.IsTyping, "Close shuts the chat");

        ui.QueueFree();
        chat.QueueFree();
    }

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
