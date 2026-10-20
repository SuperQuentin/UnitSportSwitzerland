using System.IO;
using System.Net;
using Godot;
using UnitSport.Concert.Mvr;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>
/// The MVR commands of the nearest concert (#941), on this machine:
/// <c>/concert export</c> writes its rig (and its stage as glTF) to <c>user://concert/mvr/</c>;
/// <c>/concert import &lt;file&gt; [stage|stadium]</c> reads an MVR (a path, or a name in that folder),
/// puts someone else's in the stadium frame from the stage's (its origin the deck's front centre, Y
/// upstage) and sends it to the server for the hosted set; <c>/concert import off</c> gives the set its
/// own rig back; <c>/concert rigs</c> lists the folder. The desk screen's MVR row runs the same.
/// <c>/concert xchange …</c> shares rigs live with lighting tools on the LAN (MVR-xchange, #960).
/// </summary>
public static class ConcertMvrTool
{
    public const string Folder = "user://concert/mvr";

    public static string FolderPath
    {
        get
        {
            string dir = ProjectSettings.GlobalizePath(Folder);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static ConcertRig? Nearest()
    {
        var eye = (Engine.GetMainLoop() as SceneTree)?.Root.GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
        ConcertRig? best = null;
        float bestD = float.MaxValue;
        foreach (var r in ConcertRig.Live.Values)
        {
            if (!GodotObject.IsInstanceValid(r) || !r.IsInsideTree()) continue;
            float d = r.GlobalPosition.DistanceSquaredTo(eye);
            if (d < bestD) { bestD = d; best = r; }
        }
        return best;
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : '_')).Trim();

    /// <summary>The MVR files in the folder, newest first.</summary>
    public static string[] Files() => Directory.GetFiles(FolderPath, "*.mvr").OrderByDescending(File.GetLastWriteTimeUtc).ToArray();

    public static string List()
    {
        var files = Files();
        return files.Length == 0 ? $"No MVR in {FolderPath}." : $"In {FolderPath}: " + string.Join(", ", files.Select(Path.GetFileName));
    }

    /// <summary>Writes the nearest rig as MVR (with its stage as glTF); returns the line for the chat.</summary>
    public static string Export()
    {
        if (Nearest() is not { Plan: { } plan } rig) return "No concert near to export.";
        var e = rig.Event;
        string title = $"{e.Stadium.Name} - {e.Artist}";
        var data = RigData.FromPatch(plan.Patch, title);
        byte[]? glb = null;
        try { glb = StageGlb(rig); }
        catch (Exception ex) { GD.PushWarning($"[concert] the stage did not export as glTF: {ex.Message}"); }
        var bytes = MvrFile.Write(data, title, glb);
        string path = Path.Combine(FolderPath, Safe(title) + ".mvr");
        File.WriteAllBytes(path, bytes);
        GD.Print($"[concert] exported {data.Fixtures.Count} fixtures of {data.Types.Count} types to {path} ({bytes.Length / 1024} kB)");
        return $"Exported {data.Fixtures.Count} fixtures ({data.Types.Count} types, {plan.Patch.Universes.Count} universes) to {path}";
    }

    /// <summary>The stage, its PA and its trusses as one glTF (in the rig's frame: Y up, metres, as glTF is).</summary>
    private static byte[]? StageGlb(ConcertRig rig)
    {
        var root = new Node3D { Name = "Stage" };
        try
        {
            foreach (var name in new[] { "Stage", "PA/Cabinets" })
                if (rig.GetNodeOrNull<MeshInstance3D>(name) is { Mesh: { } mesh } mi)
                    root.AddChild(new MeshInstance3D
                    {
                        Name = name.Replace('/', '_'), Mesh = mesh, Transform = rig.GlobalTransform.AffineInverse() * mi.GlobalTransform,
                        MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true },
                    });
            if (root.GetChildCount() == 0) return null;
            var doc = new GltfDocument();
            var state = new GltfState();
            if (doc.AppendFromScene(root, state) != Error.Ok) return null;
            return doc.GenerateBuffer(state);
        }
        finally
        {
            root.Free();
        }
    }

    /// <summary>
    /// Reads an MVR and sends it for the hosted set: <paramref name="args"/> is a path or a name in the
    /// folder, then <c>stage</c> or <c>stadium</c> to force the frame; <c>off</c> gives the set its own rig back.
    /// </summary>
    public static string Import(string args)
    {
        if (ConcertService.Instance is not { } svc) return "Concerts are not running.";
        if (Nearest() is not { Plan: { } plan } rig) return "No concert near: stand in the stadium.";
        int stadium = Array.IndexOf(StadiumCatalog.All, rig.Event.Stadium);
        if (stadium < 0) return "This stage's stadium is not in the catalog.";
        string a = args.Trim();
        if (a is "off" or "none" or "clear") return svc.SendRig(stadium, Array.Empty<byte>());
        if (a.Length == 0) return "/concert import <file.mvr> [stage|stadium] | off; " + List();

        string? frame = null;
        foreach (var f in new[] { " stage", " stadium" })
            if (a.EndsWith(f, StringComparison.OrdinalIgnoreCase)) { frame = f.Trim(); a = a[..^f.Length].Trim(); }
        a = a.Trim('"');
        string path = File.Exists(a) ? a : Path.Combine(FolderPath, a.EndsWith(".mvr", StringComparison.OrdinalIgnoreCase) ? a : a + ".mvr");
        if (!File.Exists(path)) return $"No file {path}. " + List();
        if (new FileInfo(path).Length > 256L << 20) return "That MVR is over 256 MB.";

        var import = MvrFile.Read(File.ReadAllBytes(path), Path.GetFileName(path), out string? error);
        if (import == null) return $"Could not read {Path.GetFileName(path)}: {error}";
        var data = import.Rig;
        bool stadiumFrame = frame == "stadium" || frame == null && import.OursStadiumFrame;
        if (!stadiumFrame) plan.FromStage(data);
        if (data.Check() is { } bad) return $"That rig does not fit: {bad}.";
        foreach (var n in import.Notes) GD.Print($"[concert] import {Path.GetFileName(path)}: {n}");
        string reply = svc.SendRig(stadium, data.Pack());
        string skipped = import.Notes.Count > 0 ? $" ({import.Notes.Count} note(s): {string.Join("; ", import.Notes.Take(2))})" : "";
        return $"{data.Fixtures.Count} fixtures read from {Path.GetFileName(path)} in the {(stadiumFrame ? "stadium" : "stage")} frame{skipped}. {reply}";
    }

    // ---- MVR-xchange (#960) ------------------------------------------------------------------------

    /// <summary>This machine's MVR-xchange station while it is on (off by default: nothing listens before).</summary>
    public static MvrXchangeStation? Station { get; private set; }

    /// <summary>Where the station's news goes: the chat that last ran an xchange command.</summary>
    private static Action<string>? _say;

    public const string XchangeHelp = "/concert xchange on [group] [local] | off | list | pull <n> | commit [comment] | join <ip:port> | ws <ws://url>";

    /// <summary><c>/concert xchange …</c>: the line for the chat; later news goes to <paramref name="say"/>.</summary>
    public static string Xchange(string[] args, Action<string>? say)
    {
        if (say != null) _say = say;
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        string rest = string.Join(' ', args.Skip(1)).Trim();
        if (verb == "on")
        {
            // local: loopback only, no mDNS (a tool on this machine, or a check)
            bool local = rest.Equals("local", StringComparison.OrdinalIgnoreCase) || rest.EndsWith(" local", StringComparison.OrdinalIgnoreCase);
            if (local) rest = rest[..^5].Trim();
            string group = MvrXchangeStation.CleanGroup(rest.Length > 0 ? rest : "Default");
            if (Station is { } on && on.Group.Equals(group, StringComparison.OrdinalIgnoreCase)) return XchangeStatus();
            Station?.Stop();
            Station = new MvrXchangeStation(group, StationName(), StationUuid()) { SaveFolder = FolderPath };
            return Station.Start(local ? IPAddress.Loopback : IPAddress.Any, 0, discovery: !local);
        }
        if (verb == "off")
        {
            if (Station is not { } s) return "MVR-xchange is off.";
            s.Stop();
            Station = null;
            return $"MVR-xchange off: left group {s.Group}.";
        }
        if (Station is not { } x) return $"MVR-xchange is off. {XchangeHelp}";
        switch (verb)
        {
            case "list" or "status":
                return XchangeStatus();
            case "join":
                if (!IPEndPoint.TryParse(rest, out var ep) || ep.Port == 0) return "/concert xchange join <ip:port> (the station's MVR-xchange port).";
                x.Join(ep);
                return $"MVR-xchange: joining {ep}...";
            case "ws":
                return Uri.TryCreate(rest, UriKind.Absolute, out var url) ? x.ConnectWebSocket(url) : "/concert xchange ws <ws://host:port/path>";
            case "pull":
                var offers = x.Offers();
                var pick = int.TryParse(rest, out int n) && n >= 1 && n <= offers.Length ? offers[n - 1]
                    : rest.Length >= 4 ? offers.FirstOrDefault(o => o.Commit.FileUuid.StartsWith(rest, StringComparison.OrdinalIgnoreCase)) : null;
                if (pick == null) return offers.Length == 0 ? "MVR-xchange: nothing offered yet." : $"/concert xchange pull <1..{offers.Length}>";
                x.Pull(pick);
                return $"MVR-xchange: pulling {OfferLabel(pick)}...";
            case "commit":
                string exported = Export();
                if (!exported.StartsWith("Exported", StringComparison.Ordinal) || Files() is not { Length: > 0 } files) return exported;
                // Export just wrote the newest file in the folder
                string path = files[0];
                return x.CommitFile(File.ReadAllBytes(path), Path.GetFileName(path), rest.Length > 0 ? rest : $"{Path.GetFileNameWithoutExtension(path)} from UnitSport");
            default:
                return XchangeHelp;
        }
    }

    /// <summary>The station, the stations heard and the files offered: for the chat and the desk.</summary>
    public static string XchangeStatus(int maxOffers = 20)
    {
        if (Station is not { } x) return "MVR-xchange off.";
        var joined = x.Peers().Where(p => p.Joined).Select(p => p.Name).ToArray();
        var groups = x.Lan().Where(s => !s.Group.Equals(x.Group, StringComparison.OrdinalIgnoreCase)).Select(s => s.Group).Distinct().ToArray();
        var offers = x.Offers();
        var sb = new System.Text.StringBuilder($"MVR-xchange group {x.Group} on TCP {x.Port}");
        if (x.WebSocketUrl != null) sb.Append($", WebSocket {x.WebSocketUrl} {(x.WebSocketOpen ? "open" : "closed")}");
        if (x.MdnsError != null) sb.Append(" (no mDNS)");
        sb.Append($": {(joined.Length == 0 ? "no station joined" : string.Join(", ", joined))}");
        if (groups.Length > 0) sb.Append($"; other groups on the LAN: {string.Join(", ", groups)}");
        sb.Append($"; we offer {x.Ours().Length} file(s), {offers.Length} offered to us{(offers.Length > 0 && maxOffers > 0 ? ":" : ".")}");
        for (int i = 0; i < offers.Length && i < maxOffers; i++) sb.Append($"\n {i + 1}. {OfferLabel(offers[i])}");
        return sb.ToString();
    }

    public static string OfferLabel(MvrXchangeStation.Offer o)
    {
        var c = o.Commit;
        string name = c.FileName.Length > 0 ? c.FileName : c.FileUuid[..Math.Min(8, c.FileUuid.Length)];
        return $"{name} from {o.StationName} ({c.FileSize / 1024} kB){(c.Comment.Length > 0 ? $" \"{c.Comment}\"" : "")}";
    }

    /// <summary>Hands the station's news to the main thread: a pulled file is imported like <c>/concert import</c>. Called every frame; nothing allocated while quiet.</summary>
    public static void Pump()
    {
        if (Station is not { } x) return;
        while (x.Events.TryDequeue(out var e))
        {
            string line = e.Line;
            if (e.Saved != null) line += " " + Import(e.Saved);
            GD.Print($"[concert] {line}");
            _say?.Invoke(line);
        }
    }

    /// <summary>The player's name, as other stations list us.</summary>
    private static string StationName()
    {
        string name = Net.PlayerRegistry.ParseRequestedName();
        if (name.Length == 0) name = Core.GameSettings.Current.PlayerName;
        return name.Length > 0 ? $"{name} (UnitSport)" : $"UnitSport on {System.Environment.MachineName}";
    }

    /// <summary>This machine's station UUID, kept across runs as the spec asks.</summary>
    private static string StationUuid()
    {
        string path = Path.Combine(Path.GetDirectoryName(FolderPath)!, "xchange_station_uuid");
        try
        {
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var kept)) return kept.ToString("D");
            string made = MvrXchange.NewUuid();
            File.WriteAllText(path, made);
            return made;
        }
        catch (IOException) { return MvrXchange.NewUuid(); }
    }
}
