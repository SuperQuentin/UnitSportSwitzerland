using UnitSport.Items;
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

    /// <summary>Name, whether it needs operator rights online, whether it also works without a server, its arguments.</summary>
    private static readonly (string Name, bool Admin, bool Offline, string Args)[] Commands =
    [
        ("help", false, true, ""),
        ("who", false, true, ""),
        ("me", false, true, "<action>"),
        ("city", false, true, "<town>"),
        ("occasion", false, true, "[list | start | stop | auto] [id]"),
        ("time", false, true, "[query] | set <hh:mm | noon | night ...> | add <hours> | speed <minutes>"),
        ("speed", false, true, "[<0.05 .. 8> | normal]"),
        ("seastate", false, true, "[0..1 | calm | chop | storm | gamey]"),
        ("weather", false, true, "[snow <cm> | fall <cm> | temp <°C> | clear | cloudy | reroll | hold | auto]"),
        ("water", false, true, "[E N]"),
        ("stadium", false, true, "[list [stadium | arena | openair] | <name | town | #n>]"),
        ("cd", false, true, "[analysis | reanalyse [stale | all | <id>]]"),
        ("style", false, true, "[ps1 | cartoon | real- | real+ | rebuild]"),
        ("clip", false, true, "start <name> [seconds] [delay] | stop"),
        ("spawn", true, true, "<item> [count]"),
        ("catalogue", true, true, ""),
        ("debug", true, true, ""),
#if PLAYTEST
        ("note", false, true, "<what's wrong>"),
#endif
        ("clear", false, true, "[player]"),
        ("transfer", false, false, "me <player> | <from> <to>"),
        ("inertia", false, false, "[steady | sway | full]"),
        ("money", true, true, "<amount> [player]"),
        ("bank", true, true, "[player] [set | add | take <amount>]"),
        ("give", true, false, "<player> <item> [count]"),
        ("name", false, false, "<name>"),
        ("login", false, false, "<password>"),
        ("stream", false, false, ""),
        ("race", false, false, "start | duel <player> | join | leave | list | npc | cancel"),
        ("fight", false, false, "<player> | accept | decline | leave"),
        ("aura", false, false, "<player> | accept | decline | leave"),
        ("concert", false, true, "host [seed] | release | status | song [list | next | prev | <n | title> | stop | play | auto] | cue <go | section | fx ...> | input artnet|sacn|osc|all [on|off] | patch | debug | export | import <file> | rigs | videos | xchange | view | camera"),
        ("br", false, false, "open [town|here] [5|6|7] [short|normal|long] [solo|duos|trios|squads] | join | leave | team [name] | invite <player> | start | bots [n|off] [easy|normal|hard] | cancel | status"),
        // invites (#840)
        ("invites", false, false, ""),
        ("accept", false, false, "[player | kind | #id]"),
        ("decline", false, false, "[player | kind | #id]"),
        ("tpa", false, false, "<player>"),
        ("tphere", false, false, "<player>"),
        ("party", false, false, "[list] | invite <player> | leave | kick <player>"),
        ("bot", true, false, "add [n] [easy|normal|hard] | arm <weapon> [rounds] | fire | clear | list"),
        ("say", true, false, "<message>"),
        ("admin", true, false, "list | add <player> | remove <player>"),
        ("tp", true, true, "<player> | <E> <N>"),
        ("bring", true, false, "<player>"),
        ("tpall", true, false, "<town>"),
        ("kick", true, false, "<player> [reason]"),
        ("pvp", true, false, "on | off"),
        ("update", true, false, ""),
    ];

    /// <summary>The commands this player can run right now.</summary>
    public IEnumerable<string> VisibleCommands() => Visible(Permissions.Online, Permissions.IsAdmin);

    /// <summary>The commands someone online or not, operator or not, may run.</summary>
    public static IEnumerable<string> Visible(bool online, bool isAdmin)
    {
        foreach (var (name, admin, offline, _) in Commands)
        {
            if (!online && !offline) continue;
            if (online && admin && !isAdmin) continue;
            // /login is how a non-admin becomes one; an admin has no use for it
            if (online && name == "login" && isAdmin) continue;
            yield return name;
        }
    }

    /// <summary>
    /// <c>/help</c> (#994), read off the same table as the completion so it never misses a command:
    /// what anyone may run, then an operator's own; offline it is all one list.
    /// </summary>
    public static IEnumerable<string> Help(bool online, bool isAdmin)
    {
        var mine = Visible(online, isAdmin).ToHashSet();
        string Line(bool admin) => string.Join("  ", Commands
            .Where(c => mine.Contains(c.Name) && (!online || c.Admin == admin))
            .Select(c => "/" + c.Name));
        yield return Line(admin: false);
        if (online && isAdmin) yield return "admin: " + Line(admin: true);
        yield return "Tab completes a command and shows its arguments; /stadium list for the venues.";
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
            return Match(VisibleCommands(), typed)
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
                    0 => ["host", "release", "status", "song", "cue", "input", "patch", "debug", "export", "import", "rigs", "videos", "xchange", "view", "ndi", "snap", "camera"],
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
