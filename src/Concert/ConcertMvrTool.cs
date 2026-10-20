using System.IO;
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
}
