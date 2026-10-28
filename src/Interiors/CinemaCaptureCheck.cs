using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --capturecheck &lt;out dir&gt;</c> (#1298 phase 3): what this machine can share, tried for real.
/// Lists the monitors, windows, cameras and microphones (<see cref="CinemaDevices"/>); shares the first monitor at High
/// quality, then the first window, for 3 s each (<see cref="CinemaCapture"/>), counting the frames and writing the last
/// one to <c>&lt;dir&gt;/monitor.jpg</c> and <c>window.jpg</c>; records the PC's sound for 4 s and prints its loudest
/// sample (play something meanwhile: the game itself is never recorded). A desktop check: a person or a script with a
/// screen, not CI.
/// </summary>
public partial class CinemaCaptureCheck : Node
{
    public static string? ParseArgs() => CmdArgs.Value("--capturecheck");

    private readonly string _dir;
    public CinemaCaptureCheck(string dir) => _dir = dir;
    public CinemaCaptureCheck() : this("") { }

    public override async void _Ready()
    {
        DirAccess.MakeDirRecursiveAbsolute(_dir);
        var found = await Task.Run(CinemaDevices.List);
        GD.Print($"[capturecheck] monitors {found.Monitors.Count}: {string.Join(", ", found.Monitors.Select(m => $"{m.Label} at {m.Area.Position}"))}");
        GD.Print($"[capturecheck] windows {found.Windows.Count}: {string.Join(" | ", found.Windows.Take(8).Select(w => w.Label))}");
        GD.Print($"[capturecheck] cameras {found.Cameras.Count}: {string.Join(", ", found.Cameras.Select(c => c.Label))}");
        GD.Print($"[capturecheck] mics {found.Mics.Count}: {string.Join(", ", found.Mics)}; PC sound {(found.PcSound ? "yes" : "no")}");
        bool ok = found.Monitors.Count > 0;
        if (found.Monitors.Count > 0) ok &= await Share(found.Monitors[0], "monitor");
        if (found.Windows.Count > 0) ok &= await Share(found.Windows[0], "window");
        // an NDI source on the network (phase 5), its sound with it: OBS's program, a concert's PGM
        if (found.Cameras.FirstOrDefault(c => c.Kind == CaptureKind.Ndi) is { } ndi) ok &= await Share(ndi, "ndi", sound: true);
        if (PcSoundLoopback.Supported)
        {
            int peak = 0;
            long samples = 0;
            using (var lb = new PcSoundLoopback(block =>
                   {
                       foreach (short s in block) peak = Math.Max(peak, Math.Abs((int)s));
                       Interlocked.Add(ref samples, block.Length);
                   }))
            {
                await ToSignal(GetTree().CreateTimer(4.0), SceneTreeTimer.SignalName.Timeout);
                if (lb.Problem.Length > 0) { GD.Print($"[capturecheck] PC sound: {lb.Problem}"); ok = false; }
            }
            GD.Print($"[capturecheck] PC sound: {samples} samples in 4 s (24 kHz: {samples / 4.0 / CinemaVideo.RelaySoundRate:P0} of real time while anything plays), loudest {peak}");
        }
        GD.Print($"[capturecheck] RESULT: {(ok ? "ok" : "FAILED")}");
        GetTree().Quit(ok ? 0 : 1);
    }

    private async Task<bool> Share(CaptureSource source, string name, bool sound = false)
    {
        using var capture = new CinemaCapture(source, CinemaVideo.LiveQuality.High, false, null);
        var frames = new List<(double, byte[])>();
        var sounds = new List<(long, byte[])>();
        int loud = 0;
        int count = 0;
        byte[]? last = null;
        double until = RealClock.Now + 3;
        while (RealClock.Now < until && !capture.Failed)
        {
            frames.Clear();
            capture.Pump(RealClock.Now, frames, sounds);
            count += frames.Count;
            // a chunk with anything but silence (µ-law 0xFF / 0x7F are zero)
            foreach (var (_, chunk) in sounds) if (chunk.Any(b => b is not (0xFF or 0x7F))) loud++;
            sounds.Clear();
            if (frames.Count > 0) last = frames[^1].Item2;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        if (last != null) File.WriteAllBytes(Path.Combine(_dir, name + ".jpg"), last);
        GD.Print($"[capturecheck] {name} '{source.Label}': {count} frames in 3 s{(capture.Failed ? $", {capture.Status}" : "")}, last {last?.Length ?? 0} bytes"
            + (sound ? $", {loud} sound chunks not silent" : ""));
        return count >= 10 && (!sound || loud > 0);
    }
}
