using UnitSport.Items;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Core;

/// <summary>
/// One completion: what the list shows, the whole input line it would produce, and an optional
/// dim note beside it (a command's arguments).
/// </summary>
public readonly record struct Suggestion(string Label, string Text, string Detail = "");

/// <summary>
/// Tab completion for the chat input: command names, sub-commands, towns, players, items and
/// occasions. Pure text in, suggestions out — the lists of towns, players and occasions come in
/// through delegates, so this knows nothing about nodes or the network.
///
/// <para>
/// It offers what the player may actually run: offline only the commands that work without a
/// server, online the admin ones only to an admin. That is a convenience; the server checks every
/// command again.
/// </para>
///
/// <para>
/// Player names complete wherever a word is free text too: in plain chat, after <c>@</c>, and in the
/// text of <c>/me</c>, <c>/say</c> or a kick reason.
/// </para>
/// </summary>
public sealed class ChatCompleter
{
    private const int MaxSuggestions = 14;

    /// <summary>Towns matching a typed query, best first (<see cref="PlaceSearchUi.Search"/>).</summary>
    public Func<string, int, IEnumerable<string>> Places { get; init; } = (_, _) => [];

    public Func<IEnumerable<string>> Occasions { get; init; } = () => [];

    /// <summary>Players online, as last reported by the server.</summary>
    public Func<IEnumerable<string>> Players { get; init; } = () => [];

    /// <summary>Asked for when a player name is about to be completed, so the list is fresh.</summary>
    public Action? PlayersWanted { get; init; }

    /// <summary>The groups <c>/help</c> lists the commands in, in this order (#1173).</summary>
    private static readonly string[] Groups = ["General", "Travel", "World", "Activities", "Concerts & media", "Items & money", "Admin"];

    /// <summary>
    /// Name, whether it needs operator rights online, whether it also works without a server, its
    /// arguments, its <c>/help</c> group and what it does in a few words.
    /// </summary>
    private static readonly (string Name, bool Admin, bool Offline, string Args, string Group, string About)[] Commands =
    [
        ("help", false, true, "[command]", "General", "the commands, or one command's arguments"),
        ("who", false, true, "", "General", "who is online"),
        ("me", false, true, "<action>", "General", "say what you do: * you wave"),
        ("name", false, false, "<name>", "General", "change your name"),
        ("login", false, false, "<password>", "General", "become an operator"),
        ("stream", false, false, "", "General", "terrain streaming stats"),
#if PLAYTEST
        ("note", false, true, "<what's wrong>", "General", "a playtest note with a screenshot"),
#endif
        ("city", false, true, "<town>", "Travel", "teleport to a town"),
        ("locate", false, true, "<thing> [+ spire | parking entrance | tall | old | new | <n> floors ...] [in <town>]", "Travel", "the nearest castle, lake, cinema, church + spire..."),
        ("go", false, true, "@E,N", "Travel", "teleport to coordinates (as /city)"),
        ("track", false, true, "@E,N [name] | off", "Travel", "point the way to coordinates"),
        ("map", false, true, "@E,N", "Travel", "show coordinates on the map"),
        ("enter", false, true, "<venue name | building key>", "Travel", "go straight inside a building, no door (offline)"),
        ("stadium", false, true, "[list [stadium | arena | openair] | <name | town | #n>]", "Travel", "the venues: what plays there, go to one"),
        ("tpa", false, false, "<player>", "Travel", "ask to go to a player"),
        ("tphere", false, false, "<player>", "Travel", "ask a player over"),
        ("tp", true, true, "<player> | <E> <N>", "Travel", "go to a player or an LV95 point"),
        ("bring", true, false, "<player>", "Travel", "bring a player to you"),
        ("tpall", true, false, "<town>", "Travel", "send everyone to a town"),
        ("time", false, true, "[query] | set <hh:mm | noon | night ...> | add <hours> | speed <minutes>", "World", "the time of day"),
        ("speed", false, true, "[<0.05 .. 8> | normal]", "World", "how fast the simulation runs"),
        ("weather", false, true, "[snow <cm> | fall <cm> | temp <°C> | clear | cloudy | reroll | hold | auto]", "World", "the weather and snow"),
        ("cinema", false, true, "[play <link> | stop | relay on|off | fit <how> | ratio <r> | pool] | <hall> [lights full|dim|off] [film on|off]", "Concerts & media", "the hall you are in: its film; a hall's lights and film by number (admin)"),
        ("seastate", false, true, "[0..1 | calm | chop | storm | gamey]", "World", "waves on the lakes"),
        ("water", false, true, "[E N]", "World", "the water surface at a point"),
        ("occasion", false, true, "[list | start | stop | auto] [id]", "World", "world events"),
        ("style", false, true, "[ps1 | cartoon | real- | real+ | rebuild]", "World", "the visual style"),
        ("stamina", false, true, "[infinite | on | full]", "World", "your stamina meter (infinite: admin online)"),
        ("inertia", false, false, "[steady | sway | full]", "World", "how standing passengers sway"),
        ("race", false, false, "start | duel <player> | join | leave | list | npc | cancel", "Activities", "races"),
        ("trial", false, false, "leave", "Activities", "an adventure trial"),
        ("br", false, false, "open [town|here] [5|6|7] [short|normal|long] [solo|duos|trios|squads] | join | leave | team [name] | invite <player> | start | bots [n|off] [easy|normal|hard] | cancel | status", "Activities", "Battle Royale"),
        ("vs", false, false, "open [here|nearest|random|<village>] [rounds] [bots off|n] [easy|normal|hard] | join [id] | leave | start | invite <player> | close <id> | list | stats [name]", "Activities", "Versteckis, hide-and-seek in a village"),
        ("fight", false, false, "<player> | accept | decline | leave", "Activities", "a fist fight"),
        ("aura", false, false, "<player> | accept | decline | leave", "Activities", "an aura battle"),
        ("party", false, false, "[list] | invite <player> | leave | kick <player>", "Activities", "your party"),
        ("rope", false, false, "[<player> | hands <player> | off]", "Activities", "rope up or hold hands with a party member"),
        ("alcohol", false, false, "", "Activities", "what alcohol does here; measure your level with a breathalyzer"),
        ("fondue", false, false, "[invite <player> | leave | forfeit 1|2|3]", "Activities", "the shared pot at your table"),
        // invites (#840)
        ("invites", false, false, "", "Activities", "invites open to you and from you"),
        ("accept", false, false, "[player | kind | #id]", "Activities", "accept an invite"),
        ("decline", false, false, "[player | kind | #id]", "Activities", "turn an invite down"),
        ("concert", false, true, "join | host [seed] | release | status | song [list | next | prev | <n | title> | stop | play | auto] | cue <go | section | fx ...> | input artnet|sacn|osc|all [on|off] | patch | debug | export | import <file> | rigs | videos | xchange | view | camera", "Concerts & media", "go to a hosted show, host one, run it"),
        ("cd", false, true, "[analysis | reanalyse [stale | all | <id>]]", "Concerts & media", "the CDs' music analysis"),
        ("clip", false, true, "start <name> [seconds] [delay] | stop", "Concerts & media", "record a clip"),
        ("clear", false, true, "[player]", "Items & money", "empty an inventory (cash kept)"),
        ("transfer", false, false, "me <player> | <from> <to>", "Items & money", "hand every item and the cash over"),
        ("spawn", true, true, "<item> [count]", "Items & money", "spawn an item"),
        ("give", true, false, "<player> <item> [count]", "Items & money", "give a player an item"),
        ("catalogue", true, true, "", "Items & money", "the item catalogue"),
        ("money", true, true, "<amount> [player]", "Items & money", "add or take cash"),
        ("bank", true, true, "[player] [set | add | take <amount>]", "Items & money", "a bank balance"),
        ("say", true, false, "<message>", "Admin", "a server announcement"),
        ("admin", true, false, "list | add <player> | remove <player>", "Admin", "the operators"),
        ("kick", true, false, "<player> [reason]", "Admin", "kick a player"),
        ("pvp", true, false, "on | off", "Admin", "player-on-player damage"),
        ("bot", true, false, "add [n] [easy|normal|hard] | arm <weapon> [rounds] | fire | clear | list", "Admin", "server-run bots"),
        ("despawn", true, true, "<vehicles | items | all> [radius m | everywhere]", "Admin", "remove parked vehicles or dropped items around you"),
        ("debug", true, true, "", "Admin", "the debug menu"),
        ("update", true, false, "", "Admin", "update and restart the server"),
        // the adventure's travel (#1226): last in the table, so the short offline list keeps its first commands; /help groups them under Travel
        ("travel", false, true, "[list] | to <point id> | unlock <id | all> [player] | lock <id | all> [player]", "Travel", "the adventure's travel network (unlock and lock: admins)"),
        ("pin", false, true, "list | add [1-6] [note] | clear", "Travel", "your map pins"),
    ];

    /// <summary>The commands this player can run right now.</summary>
    public IEnumerable<string> VisibleCommands() => Visible(Permissions.Online, Permissions.IsAdmin);

    /// <summary>The commands someone online or not, operator or not, may run.</summary>
    public static IEnumerable<string> Visible(bool online, bool isAdmin)
    {
        foreach (var (name, admin, offline, _, _, _) in Commands)
        {
            if (!online && !offline) continue;
            if (online && admin && !isAdmin) continue;
            // /login is how a non-admin becomes one; an admin has no use for it
            if (online && name == "login" && isAdmin) continue;
            yield return name;
        }
    }

    /// <summary>
    /// <c>/help</c> (#994, #1173), read off the same table as the completion so it never misses a
    /// command: one line per group of what this player may run.
    /// </summary>
    public static IEnumerable<string> Help(bool online, bool isAdmin)
    {
        var mine = Visible(online, isAdmin).ToHashSet();
        yield return "Commands (Tab completes them; /help <command> for one's arguments):";
        foreach (string group in Groups)
        {
            var names = Commands.Where(c => c.Group == group && mine.Contains(c.Name)).Select(c => "/" + c.Name).ToList();
            if (names.Count > 0) yield return $"  {group}: {string.Join("  ", names)}";
        }
    }

    /// <summary><c>/help &lt;command&gt;</c>: its arguments and what it does; null for one this player can't run.</summary>
    public static string? HelpFor(string command, bool online, bool isAdmin)
    {
        string verb = command.Trim().TrimStart('/').ToLowerInvariant();
        if (!Visible(online, isAdmin).Contains(verb)) return null;
        var c = Commands.First(c => c.Name == verb);
        return (c.Args.Length == 0 ? $"/{verb}" : $"/{verb} {c.Args}") + $"  — {c.About}";
    }

    /// <summary>"/spawn &lt;item&gt; [count]" once the line starts with a command this player can run, else null.</summary>
    public string? Usage(string text)
    {
        if (!text.StartsWith('/') || !text.Contains(' ')) return null;
        string verb = text[1..].Split(' ', 2)[0].ToLowerInvariant();
        if (!VisibleCommands().Contains(verb)) return null;
        string args = ArgsOf(verb);
        return args.Length == 0 ? $"/{verb}" : $"/{verb} {args}";
    }

    private static string ArgsOf(string verb) => Commands.First(c => c.Name == verb).Args;

    /// <summary>Suggestions for the line as typed so far; empty when nothing fits.</summary>
    public IReadOnlyList<Suggestion> Complete(string text)
    {
        if (!text.StartsWith('/')) return PlayerWord(text);

        string body = text[1..];
        bool trailing = body.EndsWith(' ');
        string[] words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0 || (words.Length == 1 && !trailing))
        {
            string typed = words.Length == 0 ? "" : words[0];
            // a bare "/" lists every command, uncapped: the pad's picker (#1277) has no other way to reach them
            return (typed.Length == 0 ? VisibleCommands().ToList() : Match(VisibleCommands(), typed))
                .Select(c => new Suggestion($"/{c}", $"/{c} ", ArgsOf(c)))
                .ToList();
        }

        string verb = words[0].ToLowerInvariant();
        if (!VisibleCommands().Contains(verb)) return [];

        // the word being typed, or the next one when the line ends in a space
        int argIndex = trailing ? words.Length - 1 : words.Length - 2;
        string current = trailing ? "" : words[^1];

        IEnumerable<string> options;
        switch (verb)
        {
            case "city" or "tpall":
            {
                // a town's name has spaces in it, so everything after the verb is one query
                current = text[(1 + words[0].Length)..].TrimStart();
                string head = text[..(text.Length - current.Length)];
                return Places(current.TrimEnd(), MaxSuggestions)
                    .Distinct()
                    .Select(p => new Suggestion(p, head + p))
                    .ToList();
            }

            case "locate" or "find":
                return LocateWords(text, words[0]);

            case "stadium":
            {
                // a venue's name has spaces in it too: everything after the verb is one query
                current = text[(1 + words[0].Length)..].TrimStart();
                string head = text[..(text.Length - current.Length)];
                if (current.StartsWith("list", StringComparison.OrdinalIgnoreCase))
                {
                    if (argIndex == 0) return [new Suggestion("list", head + "list")];
                    if (argIndex != 1) return [];
                    string kind = trailing ? "" : words[^1];
                    return Match(StadiumLookup.KindWords, kind).Select(k => new Suggestion(k, head + "list " + k)).ToList();
                }
                var venues = current.Trim().Length == 0 ? StadiumCatalog.All : StadiumLookup.Find(current);
                var rows = venues.Select(v => new Suggestion(v.Name, head + v.Name, v.Town)).Take(MaxSuggestions);
                return (current.Trim().Length == 0 || "list".StartsWith(current.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? rows.Prepend(new Suggestion("list", head + "list", "every venue, numbered"))
                    : rows).Take(MaxSuggestions).ToList();
            }

            case "concert":
            {
                string sub = words.Length > 1 ? words[1].ToLowerInvariant() : "";
                options = argIndex switch
                {
                    0 => ["join", "host", "release", "status", "song", "cue", "input", "patch", "debug", "export", "import", "rigs", "videos", "xchange", "view", "ndi", "snap", "camera"],
                    1 when sub == "song" => ["list", "next", "prev", "restart", "stop", "play", "auto"],
                    1 when sub == "camera" => ["take", "leave", "list"],
                    1 when sub == "cue" => ["go", "calm", "groove", "chorus", "peak", "blackout", "release", "bpm", "master", "flames", "co2", "confetti", "gerbs", "pyro", "camera"],
                    1 when sub == "input" => ["artnet", "sacn", "osc", "all", "status"],
                    2 when sub == "input" => ["on", "off"],
                    1 when sub == "xchange" => ["on", "off", "list", "pull", "commit", "join", "ws"],
                    _ => [],
                };
                break;
            }

            case "transfer":
                options = argIndex switch
                {
                    0 => PlayerNames().Prepend("me"),
                    1 => PlayerNames(),
                    _ => [],
                };
                break;

            case "inertia":
                options = argIndex == 0 ? ["steady", "sway", "full"] : [];
                break;

            case "spawn":
                if (argIndex != 0) return [];
                options = ItemLookup.Names();
                break;

            case "help":
                options = argIndex == 0 ? VisibleCommands() : [];
                break;

            case "tp" or "bring" or "tpa" or "tphere" or "accept" or "decline":
                if (argIndex != 0) return [];
                options = PlayerNames();
                break;

            case "give":
                options = argIndex switch
                {
                    0 => PlayerNames().Prepend("me"),
                    1 => ItemLookup.Names(),
                    _ => [],
                };
                break;

            case "clear":
                options = argIndex == 0 && Permissions.Online && Permissions.IsAdmin ? PlayerNames().Prepend("me") : [];
                break;

            case "money":
                options = argIndex == 1 && Permissions.Online ? PlayerNames().Prepend("me") : [];
                break;

            case "bank":
            {
                string[] verbs = ["set", "add", "take"];
                bool named = words.Length > 1 && !verbs.Contains(words[1].ToLowerInvariant());
                options = argIndex switch
                {
                    0 => Permissions.Online ? verbs.Concat(PlayerNames()) : verbs,
                    1 when named => verbs,
                    _ => [],
                };
                break;
            }

            case "kick":
                // the reason after the name is free text
                if (argIndex != 0) return PlayerWord(text);
                options = PlayerNames();
                break;

            case "me" or "say":
                return PlayerWord(text);

            case "admin":
                options = argIndex switch
                {
                    0 => ["list", "add", "remove"],
                    1 when words[1].ToLowerInvariant() is "add" or "remove" => PlayerNames(),
                    _ => [],
                };
                break;

            case "br":
                bool admin = Permissions.IsAdmin;
                options = argIndex switch
                {
                    0 => admin ? ["join", "leave", "team", "invite", "status", "open", "start", "zone", "bots", "cancel"] : ["join", "leave", "team", "invite", "status"],
                    1 when words[1].ToLowerInvariant() == "invite" => PlayerNames(),
                    _ when admin && words[1].ToLowerInvariant() == "open" => ["here", "short", "normal", "long", "5", "6", "7"],
                    _ when admin && words[1].ToLowerInvariant() == "bots" => ["off", "10", "20", "easy", "normal", "hard"],
                    _ => [],
                };
                break;

            case "pvp":
                options = argIndex == 0 ? ["on", "off"] : [];
                break;

            case "despawn":   // #1200
                options = argIndex switch
                {
                    0 => ["vehicles", "items", "all"],
                    1 => ["10", "30", "100", "everywhere"],
                    _ => [],
                };
                break;

            case "bot" or "bots":   // #837
                options = argIndex switch
                {
                    0 => ["add", "arm", "fire", "clear", "list"],
                    _ when words[1].ToLowerInvariant() == "add" => ["easy", "normal", "hard"],
                    _ when words[1].ToLowerInvariant() == "arm" => ["pistol", "rifle", "huntingrifle", "shotgun", "knife"],
                    _ => [],
                };
                break;

            case "party":
                options = argIndex switch
                {
                    0 => ["list", "invite", "leave", "kick"],
                    1 when words[1].ToLowerInvariant() is "invite" or "kick" => PlayerNames(),
                    _ => [],
                };
                break;

            case "fight" or "aura":
                options = argIndex == 0 ? [.. PlayerNames(), "accept", "decline", "leave"] : [];
                break;

            case "rope":
                options = argIndex == 0 ? [.. PlayerNames(), "hands", "off"] : argIndex == 1 && words[1].ToLowerInvariant() == "hands" ? PlayerNames() : [];
                break;

            case "race":
                options = argIndex switch
                {
                    0 => ["start", "duel", "join", "leave", "list", "npc", "cancel"],
                    1 when words[1].ToLowerInvariant() == "duel" => PlayerNames(),
                    _ => [],
                };
                break;

            case "time":
                bool mayTime = !Permissions.Online || Permissions.IsAdmin;
                options = argIndex switch
                {
                    0 => mayTime ? World.TimeCommand.Verbs : ["query"],
                    1 when mayTime && words[1].ToLowerInvariant() == "set" => World.TimeCommand.Named.Select(n => n.Name),
                    _ => [],
                };
                break;

            case "style":
                options = argIndex == 0 ? Styles.StyleCommand.Words : [];
                break;

            case "clip":
                options = argIndex == 0 ? ClipRecorder.Words : [];
                break;

            case "seastate":
                options = argIndex == 0 && (!Permissions.Online || Permissions.IsAdmin)
                    ? World.SeaStateCommand.Named.Select(n => n.Name) : [];
                break;

            case "weather":
                options = argIndex == 0 && (!Permissions.Online || Permissions.IsAdmin)
                    ? ["snow", "fall", "temp", "clear", "cloudy", "reroll", "hold", "auto"] : [];
                break;

            case "occasion":
                bool mayChange = !Permissions.Online || Permissions.IsAdmin;
                options = argIndex switch
                {
                    0 => mayChange ? ["list", "start", "stop", "auto"] : ["list"],
                    1 when words[1].ToLowerInvariant() is "start" or "stop" => Occasions(),
                    _ => [],
                };
                break;

            default:
                return [];
        }

        string start = text[..(text.Length - current.Length)];
        return Match(options, current).Select(o => new Suggestion(o, start + o)).ToList();
    }

    /// <summary>
    /// The last word of free text completed to a player's name: "hi qu" → "hi Quentin ", "@qu" → "@Quentin ".
    /// A bare word needs two letters, so ordinary chatting does not keep popping names up; after
    /// <c>@</c> one is enough, and a bare <c>@</c> lists everybody.
    /// </summary>
    private IReadOnlyList<Suggestion> PlayerWord(string text)
    {
        if (text.Length == 0 || text.EndsWith(' ')) return [];

        int space = text.LastIndexOf(' ');
        string word = text[(space + 1)..];
        bool at = word.StartsWith('@');
        string typed = at ? word[1..] : word;
        if (!at && typed.Length < 2) return [];

        string head = text[..(space + 1)] + (at ? "@" : "");
        // a name only completes from its start: "an" must not offer "Joanna" mid-sentence
        return PlayerNames()
            .Distinct()
            .Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                        && !n.Equals(typed, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(n => new Suggestion(at ? "@" + n : n, head + n + " "))
            .ToList();
    }

    private IEnumerable<string> PlayerNames()
    {
        PlayersWanted?.Invoke();
        return Players();
    }

    /// <summary>Options that start with what was typed, then ones that merely contain it.</summary>
    /// <summary>
    /// <c>/locate</c>'s arguments (#1277): a noun first, then <c>+ selector</c>s, then a town after <c>in</c>. Not
    /// capped at <see cref="MaxSuggestions"/>: the pad's buttons offer every noun.
    /// </summary>
    private List<Suggestion> LocateWords(string text, string verb)
    {
        string args = text[(1 + verb.Length)..].TrimStart();
        string head = text[..(text.Length - args.Length)];
        int inAt = (" " + args).LastIndexOf(" in ", StringComparison.OrdinalIgnoreCase);
        if (inAt >= 0)
        {
            // a town's name has spaces in it: everything after "in" is one query
            string town = args[(inAt + 3)..].TrimStart();
            string before = text[..(text.Length - town.Length)];
            return Places(town.TrimEnd(), MaxSuggestions).Distinct().Select(p => new Suggestion(p, before + p)).ToList();
        }
        int plus = args.LastIndexOf('+');
        if (plus < 0 && !args.EndsWith(' '))
        {
            // the noun, as it is being typed
            string typed = PlaceIndex.Normalize(args);
            return World.Locate.LocateVocabulary.Nouns
                .Where(w => w.StartsWith(typed, StringComparison.Ordinal) || typed.Length > 2 && w.Contains(typed, StringComparison.Ordinal))
                .Select(w => new Suggestion(w, head + w + " "))
                .ToList();
        }
        if (plus < 0)
        {
            // a noun and a space: the selectors that may follow it, and a town
            return World.Locate.LocateVocabulary.ModifierWords
                .Select(m => m == "in" ? new Suggestion("in <town>", text + "in ", "search round a town") : new Suggestion("+ " + m, text + "+ " + m + " "))
                .ToList();
        }
        string sel = PlaceIndex.Normalize(args[(plus + 1)..].TrimStart());
        string selHead = text[..(text.Length - args[(plus + 1)..].TrimStart().Length)];
        if (args.EndsWith(' ') && sel.Length > 0)
            return World.Locate.LocateVocabulary.ModifierWords
                .Select(m => m == "in" ? new Suggestion("in <town>", text + "in ", "search round a town") : new Suggestion("+ " + m, text + "+ " + m + " "))
                .ToList();
        return World.Locate.LocateVocabulary.ModifierWords.Where(m => m != "in" && m.StartsWith(sel, StringComparison.Ordinal))
            .Select(m => new Suggestion(m, selHead + m + " "))
            .ToList();
    }

    private static List<string> Match(IEnumerable<string> options, string typed)
    {
        var all = options.Distinct().ToList();
        if (typed.Length == 0) return all.Take(MaxSuggestions).ToList();

        return all
            .Where(o => o.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(o => o, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .ToList();
    }
}
