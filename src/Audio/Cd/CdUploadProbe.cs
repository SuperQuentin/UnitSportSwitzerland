using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;

namespace UnitSport.Audio.Cd;

/// <summary>
/// <c>--uploadcheck &lt;good audio&gt; &lt;second file&gt;</c> (#736, <c>tools/uploadcheck.sh</c>): a client
/// of a dedicated server uploads a song of its own as a shared CD. The server must scan it for
/// viruses (the status says so) and burn it: it arrives in the shared list under the file's name.
/// A second upload right after is refused (one a minute). Read the <c>[uploadcheck]</c> lines.
/// </summary>
public partial class CdUploadProbe : Node
{
    public static bool Requested => CmdArgs.Has("--uploadcheck");

    private readonly List<string> _status = new();
    private int _failed;

    public CdUploadProbe() => Name = "CdUploadProbe";

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[uploadcheck] {what}");

    private void Check(bool ok, string what)
    {
        Log($"{(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) _failed++;
    }

    private async Task Run()
    {
        string good = CmdArgs.Value("--uploadcheck") ?? "";
        string second = CmdArgs.Value("--uploadcheck", 2) ?? good;
        for (int i = 0; i < 600 && !(Multiplayer.MultiplayerPeer is { } p && p.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected
                                      && CdLibrary.Instance != null && CdUpload.Instance != null); i++)
            await Wait(0.1);
        if (CdLibrary.Instance is not { } library) { Finish("no library"); return; }
        await Wait(2.0);   // the server's list arrives
        library.BurnStatus += line => { _status.Add(line); Log($"status: {line}"); };

        string title = System.IO.Path.GetFileNameWithoutExtension(good);
        int before = library.All.Count;
        library.BurnFile(good);
        CdInfo? got = null;
        for (int i = 0; i < 1800 && got == null; i++)
        {
            await Wait(0.1);
            got = library.All.Values.FirstOrDefault(c => c.Title == title);
            if (_status.Any(s => s.Contains("cannot check files") || s.Contains("flagged"))) break;
        }
        Check(_status.Any(s => s.StartsWith("Uploading")), "the file went up in chunks (Uploading… %)");
        Check(_status.Any(s => s.StartsWith("Scanning")), "the server scanned it for viruses");
        Check(got != null && library.All.Count == before + 1, $"it became a shared CD ({got?.Describe() ?? "none"})");

        _status.Clear();
        library.BurnFile(second);
        for (int i = 0; i < 100 && _status.Count == 0 || i < 10; i++) await Wait(0.1);
        Check(_status.Any(s => s.Contains("One upload a minute")), "a second upload straight after is refused");
        Finish(null);
    }

    private void Finish(string? fatal)
    {
        if (fatal != null) { Log($"RESULT: FAIL {fatal}"); GetTree().Quit(1); return; }
        Log(_failed == 0 ? "RESULT: ok" : $"RESULT: FAILED {_failed} check(s)");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
