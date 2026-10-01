using UnitSport.Items;

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
    private const int MaxSuggestions = 12;

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
        ("spawn", true, true, "<item> [count]"),
        ("name", false, false, "<name>"),
        ("login", false, false, "<password>"),
        ("stream", false, false, ""),
        ("race", false, false, "start | duel <player> | join | leave | list | npc | cancel"),
        ("say", true, false, "<message>"),
        ("admin", true, false, "list | add <player> | remove <player>"),
        ("tp", true, false, "<player>"),
        ("bring", true, false, "<player>"),
        ("tpall", true, false, "<town>"),
        ("kick", true, false, "<player> [reason]"),
    ];

    /// <summary>The commands this player can run right now.</summary>
    public IEnumerable<string> VisibleCommands()
    {
        bool online = Permissions.Online;
        foreach (var (name, admin, offline, _) in Commands)
        {
            if (!online && !offline) continue;
            if (online && admin && !Permissions.IsAdmin) continue;
            // /login is how a non-admin becomes one; an admin has no use for it
            if (online && name == "login" && Permissions.IsAdmin) continue;
            yield return name;
        }
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

            case "spawn":
                if (argIndex != 0) return [];
                options = ItemLookup.Names();
                break;

            case "tp" or "bring":
                if (argIndex != 0) return [];
                options = PlayerNames();
                break;

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

            case "race":
                options = argIndex switch
                {
                    0 => ["start", "duel", "join", "leave", "list", "npc", "cancel"],
                    1 when words[1].ToLowerInvariant() == "duel" => PlayerNames(),
                    _ => [],
                };
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
