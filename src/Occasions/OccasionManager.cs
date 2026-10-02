using System.Globalization;
using System.Text.Json;
using Godot;
using UnitSport.Net;
using UnitSport.Core;

namespace UnitSport.Occasions;

/// <summary>A player's standing choice for one occasion. Saved by name in <c>settings.json</c>.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum OccasionPreference
{
    /// <summary>Whatever the calendar (or the server) says.</summary>
    Auto = 0,
    /// <summary>Hide its cosmetic part, unless the server locks it on. Loot and the hunt stay.</summary>
    Off = 1,
    /// <summary>Offline: run it whatever the date. Online: show its cosmetic part even if the server does not run it.</summary>
    Always = 2,
}

/// <summary>
/// Which occasions are running, at <c>World/Occasions</c> on the server and on every client —
/// RPCs route by node path, so the name matches on both sides, like <c>World/Loot</c>.
///
/// <para>
/// <b>Two lists.</b> The <i>authority</i> list is what runs in this world: the dedicated server
/// evaluates it from its own local clock and replicates it, and a client with no server evaluates
/// it itself. The <see cref="Active"/> list is what this viewer gets: the authority list with the
/// cosmetic facets removed from anything the player turned off (unless the server locked it), plus
/// the cosmetic part of anything the player forced on. Loot and the hunt are never touched by
/// the player's preferences, because they are gameplay and the world has to agree on them.
/// </para>
///
/// <para>
/// Everything that reacts — the sky, the props, the ambience, the loot tables — reads
/// <see cref="Active"/> and listens to <see cref="Changed"/>; nothing caches its own copy of the
/// calendar.
/// </para>
/// </summary>
public partial class OccasionManager : Node
{
    public const string NodeName = "Occasions";

    /// <summary>How often the authority re-reads the clock: often enough to flip at midnight.</summary>
    private const double EvaluateEvery = 60;

    public static OccasionManager? Instance { get; private set; }

    /// <summary>Raised on the main thread whenever <see cref="Active"/> changes.</summary>
    public static event Action? Changed;

    private List<OccasionEntry> _config = new();

    // session-only overrides on the authority
    private HashSet<string>? _cliSet;   // --occasion: replaces the calendar entirely
    private readonly HashSet<string> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stopped = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly? _dateOverride;

    private List<Wire> _authority = new();
    private string _authorityJson = "[]";
    private bool _fromServer;
    private double _untilEvaluate;
    private string _signature = "";

    /// <summary>Running now, for this viewer, highest priority first.</summary>
    public IReadOnlyList<ActiveOccasion> Active { get; private set; } = Array.Empty<ActiveOccasion>();

    /// <summary>Every occasion the config knows, running or not — for the settings menu.</summary>
    public IReadOnlyList<OccasionEntry> Known => _config;

    /// <summary>The date the calendar is read at: today, or <c>--date</c>.</summary>
    public DateOnly Today => _dateOverride ?? DateOnly.FromDateTime(DateTime.Now);

    /// <summary>What crosses the wire: the authority's decision, not the content.</summary>
    private sealed record Wire(string Id, int Facets, int Priority, bool AllowOptOut, string Instance);

    private static readonly JsonSerializerOptions WireJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static OccasionManager Create(Node world)
    {
        var m = new OccasionManager { Name = NodeName };
        world.AddChild(m);
        return m;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        GameSettings.Changed -= OnSettingsChanged;
        if (Instance == this)
        {
            Instance = null;
            Loot.LootTables.Seasonal = null;
        }
    }

    public override void _Ready()
    {
        _config = OccasionConfig.Load();
        ParseCommandLine(OS.GetCmdlineUserArgs());
        GameSettings.Changed += OnSettingsChanged;
        EvaluateAuthority(force: true);

        // Whoever rolls loot here — the server, or an offline client — adds the running
        // occasions' treats. A client in a server's world never rolls, so its view cannot matter.
        Loot.LootTables.Seasonal = type =>
        {
            foreach (var a in Active)
                if (a.Has(OccasionFacets.Loot) && a.Content.Treats(type) is { } treats) return treats;
            return null;
        };
    }

    /// <summary>
    /// A preference changed. Offline, "Always" is part of the authority list, so that is rebuilt;
    /// in a server's world only this viewer's filter is.
    /// </summary>
    private void OnSettingsChanged()
    {
        if (ClientOnline && _fromServer) Recompute();
        else EvaluateAuthority(force: true);
    }

    public override void _Process(double delta)
    {
        // The server went away: this client is its own authority again.
        if (_fromServer && !ClientOnline)
        {
            _fromServer = false;
            GD.Print("[occasions] disconnected; back to the local calendar");
            EvaluateAuthority(force: true);
            return;
        }

        _untilEvaluate -= delta;
        if (_untilEvaluate <= 0) EvaluateAuthority();
    }

    // ---- roles ---------------------------------------------------------------------------------

    private bool Online => NetLink.Online(this);

    private bool ClientOnline => Online && !Multiplayer.IsServer();
    private bool DedicatedServer => Online && Multiplayer.IsServer();

    // ---- queries -------------------------------------------------------------------------------

    /// <summary>The highest-priority running occasion with <paramref name="facet"/>, or null.</summary>
    public ActiveOccasion? Top(OccasionFacets facet)
    {
        foreach (var a in Active)
            if (a.Has(facet)) return a;
        return null;
    }

    public bool IsActive(string id, OccasionFacets facet = OccasionFacets.None)
    {
        foreach (var a in Active)
            if (string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase) && a.Has(facet)) return true;
        return false;
    }

    /// <summary>The sky and sun of the highest-priority running occasion that has one.</summary>
    public (Occasion Owner, OccasionAtmosphere Atmosphere)? Atmosphere
    {
        get
        {
            foreach (var a in Active)
                if (a.Has(OccasionFacets.Atmosphere) && a.Content.Atmosphere is { } atmo)
                    return (a.Content, atmo);
            return null;
        }
    }

    public OccasionPreference PreferenceFor(string id)
    {
        // a command-line set wins over the saved choice for this session, in both directions
        if (_cliSet != null) return _cliSet.Contains(id) ? OccasionPreference.Always : OccasionPreference.Off;
        return GameSettings.Current.OccasionPreferences.TryGetValue(id, out var p) ? p : OccasionPreference.Auto;
    }

    // ---- the authority -------------------------------------------------------------------------

    /// <summary>Re-reads the calendar. A client in a server's world leaves this to the server.</summary>
    private void EvaluateAuthority(bool force = false)
    {
        _untilEvaluate = EvaluateEvery;
        if (ClientOnline && _fromServer) return;

        var date = Today;
        var running = new List<(OccasionEntry Entry, string Instance)>();
        if (_cliSet != null)
        {
            foreach (var e in _config)
                if (_cliSet.Contains(e.Id)) running.Add((e, OccasionSchedule.ForcedInstance(e, date)));
        }
        else
        {
            running.AddRange(OccasionSchedule.Evaluate(_config, date));
            // offline, "Always" in the settings is a force; a server's own settings are not a policy
            if (!Online)
                foreach (var e in _config)
                    if (PreferenceFor(e.Id) == OccasionPreference.Always && running.All(r => r.Entry != e))
                        running.Add((e, OccasionSchedule.ForcedInstance(e, date)));
        }

        foreach (var e in _config)
            if (_started.Contains(e.Id) && running.All(r => r.Entry != e))
                running.Add((e, OccasionSchedule.ForcedInstance(e, date)));
        running.RemoveAll(r => _stopped.Contains(r.Entry.Id));

        var wire = running
            .Select(r => new Wire(r.Entry.Id, (int)r.Entry.Facets.ToFlags(), r.Entry.Priority,
                r.Entry.AllowClientOptOut, r.Instance))
            .Where(w => w.Facets != 0)
            .ToList();
        string json = JsonSerializer.Serialize(wire, WireJson);
        if (!force && json == _authorityJson && _signature.Length > 0) return;

        _authority = wire;
        _authorityJson = json;
        _fromServer = false;
        if (DedicatedServer)
        {
            GD.Print($"[occasions] running: {Describe(wire)}");
            Rpc(MethodName.SetActive, json);
        }
        Recompute();
    }

    /// <summary>Server: tells a newly connected peer what is running.</summary>
    public void SendTo(long peerId)
    {
        if (DedicatedServer) RpcId(peerId, MethodName.SetActive, _authorityJson);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetActive(string json)
    {
        try
        {
            _authority = JsonSerializer.Deserialize<List<Wire>>(json, WireJson) ?? new();
        }
        catch (JsonException e)
        {
            GD.PushWarning($"[occasions] unreadable list from the server: {e.Message}");
            return;
        }
        _authorityJson = json;
        _fromServer = true;
        GD.Print($"[occasions] server runs: {Describe(_authority)}");
        Recompute();
    }

    // ---- this viewer ---------------------------------------------------------------------------

    /// <summary>Applies the player's preferences to the authority list.</summary>
    private void Recompute()
    {
        var active = new List<ActiveOccasion>();
        foreach (var w in _authority)
        {
            var facets = (OccasionFacets)w.Facets;
            if (PreferenceFor(w.Id) == OccasionPreference.Off && w.AllowOptOut) facets &= ~OccasionFacets.Cosmetic;
            if (facets == OccasionFacets.None) continue;
            active.Add(new ActiveOccasion(OccasionRegistry.Get(w.Id), EntryFor(w), facets, w.Instance));
        }

        // Online, "Always" can only add what is this viewer's to add: the look, not the loot.
        if (ClientOnline)
            foreach (var e in _config)
                if (PreferenceFor(e.Id) == OccasionPreference.Always && active.All(a => a.Id != e.Id))
                {
                    var facets = e.Facets.ToFlags() & OccasionFacets.Cosmetic;
                    if (facets != OccasionFacets.None)
                        active.Add(new ActiveOccasion(OccasionRegistry.Get(e.Id), e, facets,
                            OccasionSchedule.ForcedInstance(e, Today)));
                }

        // stable: equal priorities keep the config's order
        active = active.Select((a, i) => (a, i)).OrderByDescending(x => x.a.Priority).ThenBy(x => x.i)
            .Select(x => x.a).ToList();

        string signature = active.Count == 0 ? "-"
            : string.Join(";", active.Select(a => $"{a.Id}:{(int)a.Facets}:{a.Instance}"));
        if (signature == _signature) return;
        _signature = signature;
        Active = active;

        if (!DedicatedServer)
            GD.Print($"[occasions] active: {(active.Count == 0 ? "none" : string.Join(", ", active.Select(a => $"{a.Id} ({a.Instance}, {a.Facets})")))}");
        Changed?.Invoke();
    }

    /// <summary>The local config's entry, or one rebuilt from the wire for an occasion only the server knows.</summary>
    private OccasionEntry EntryFor(Wire w) =>
        _config.FirstOrDefault(e => e.Id == w.Id) is { } local
            ? new OccasionEntry
            {
                Id = local.Id, Enabled = true, Priority = w.Priority, Schedule = local.Schedule,
                Facets = local.Facets, AllowClientOptOut = w.AllowOptOut,
            }
            : new OccasionEntry { Id = w.Id, Priority = w.Priority, AllowClientOptOut = w.AllowOptOut };

    private static string Describe(List<Wire> list) =>
        list.Count == 0 ? "none" : string.Join(", ", list.Select(w => $"{w.Id} ({w.Instance}, {(OccasionFacets)w.Facets})"));

    // ---- overrides -----------------------------------------------------------------------------

    /// <summary>
    /// <c>--occasion &lt;id|none&gt;</c> (repeatable) replaces the calendar with exactly that set for
    /// this session, and <c>--date YYYY-MM-DD</c> reads the calendar on another day. Neither is saved.
    /// </summary>
    private void ParseCommandLine(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            string v = args[i + 1].Trim().ToLowerInvariant();
            if (args[i] == "--occasion")
            {
                _cliSet ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (v is not ("none" or "off")) _cliSet.Add(v);
            }
            else if (args[i] == "--date"
                && DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                _dateOverride = d;
            }
        }
        if (_cliSet != null) GD.Print($"[occasions] --occasion: {(_cliSet.Count == 0 ? "none" : string.Join(", ", _cliSet))}");
        if (_dateOverride is { } date) GD.Print($"[occasions] --date: calendar read as {date:yyyy-MM-dd}");
    }

    /// <summary>
    /// <c>/occasion list | start &lt;id&gt; | stop &lt;id&gt; | auto</c>, from chat or the server console.
    /// Changes last for the session; the calendar itself lives in <c>occasions.json</c>.
    /// </summary>
    /// <returns>The lines to reply with.</returns>
    public List<string> RunCommand(string[] args, bool isAdmin)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        string? id = args.Length > 1 ? args[1].ToLowerInvariant() : null;

        if (sub == "list") return ListLines();
        if (!isAdmin) return new() { $"'/occasion {sub}' is an admin command." };

        switch (sub)
        {
            case "start" or "stop" when id == null:
                return new() { $"Usage: /occasion {sub} <id>" };
            case "start" or "stop" when _config.All(e => e.Id != id):
                return new() { $"No occasion '{id}'. Known: {string.Join(", ", _config.Select(e => e.Id))}" };
            case "start":
                _stopped.Remove(id!);
                _started.Add(id!);
                break;
            case "stop":
                _started.Remove(id!);
                _stopped.Add(id!);
                break;
            case "auto":
                _started.Clear();
                _stopped.Clear();
                break;
            default:
                return new() { "Usage: /occasion list | start <id> | stop <id> | auto" };
        }

        EvaluateAuthority(force: true);
        return ListLines();
    }

    private List<string> ListLines()
    {
        var lines = new List<string> { $"Calendar date {Today:yyyy-MM-dd}. Running: {Describe(_authority)}" };
        foreach (var e in _config)
        {
            string windows = string.Join(", ", e.Schedule.Select(w => $"{w.From}..{w.To}"));
            string state = _started.Contains(e.Id) ? " [started]" : _stopped.Contains(e.Id) ? " [stopped]" : "";
            lines.Add($"  {e.Id}: {(e.Enabled ? windows : "disabled")}{(e.AllowClientOptOut ? "" : " (locked)")}{state}");
        }
        return lines;
    }
}
