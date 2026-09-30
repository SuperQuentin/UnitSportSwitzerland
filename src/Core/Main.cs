using Godot;

namespace UnitSport.Core;

/// <summary>
/// Entry point: boots into the dedicated server world (exported with the
/// dedicated_server feature or run with "-- --server") or the client world.
/// </summary>
public partial class Main : Node
{
	public override void _Ready()
	{
		// the network rules' own self-checks: vision interest and remote interpolation
		if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--interestcheck") >= 0)
		{
			bool ok = UnitSport.Net.Interest.SelfCheck() & UnitSport.Net.RemoteInterpolator.SelfCheck();
			GD.Print(ok ? "[interestcheck] RESULT: ok" : "[interestcheck] RESULT: FAILED");
			GetTree().Quit(ok ? 0 : 1);
			return;
		}

		// the CD beat analyser's self-test: synthetic clicks at known tempos
		if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--beatcheck") >= 0)
		{
			bool ok = UnitSport.Audio.Cd.BeatAnalyzer.SelfCheck();
			GD.Print(ok ? "[beatcheck] RESULT: ok" : "[beatcheck] RESULT: FAILED");
			GetTree().Quit(ok ? 0 : 1);
			return;
		}

		// LAN discovery probe: browse mDNS for dedicated servers, list them, quit (docs/notes/net/lan-discovery.md)
		int dc = Array.IndexOf(OS.GetCmdlineUserArgs(), "--discovercheck");
		if (dc >= 0)
		{
			var a = OS.GetCmdlineUserArgs();
			double wait = 8;
			if (dc + 1 < a.Length) double.TryParse(a[dc + 1], System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out wait);
			var lan = new UnitSport.Net.LanDiscovery();
			lan.Start();
			GetTree().CreateTimer(wait).Timeout += () =>
			{
				lan.Poll();
				foreach (var s in lan.Servers) GD.Print($"[discover] {s.Name} {s.Endpoint} version={s.Version}");
				bool ok = lan.Servers.Count > 0;
				lan.Dispose();
				GD.Print(ok ? "[discover] RESULT: ok" : "[discover] RESULT: FAILED (no server answered)");
				GetTree().Quit(ok ? 0 : 1);
			};
			return;
		}

		// A model turntable, before any world is built: the avatars are the subject, so
		// there is no point streaming terrain to look at them.
		if (UnitSport.Avatar.AvatarPreview.Requested(out double seconds, out string output))
		{
			float view = 90;
			var a = OS.GetCmdlineUserArgs();
			int vi = Array.IndexOf(a, "--view");
			if (vi >= 0 && vi + 1 < a.Length) float.TryParse(a[vi + 1],
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out view);
			int focus = -1;
			int fi = Array.IndexOf(a, "--focus");
			if (fi >= 0 && fi + 1 < a.Length && !int.TryParse(a[fi + 1], out focus))
				focus = a[fi + 1] switch { "r1" => 5, "monster" => 6, _ => -1 };
			float crank = float.NaN;
			int ci = Array.IndexOf(a, "--crank");
			if (ci >= 0 && ci + 1 < a.Length) float.TryParse(a[ci + 1],
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out crank);
			float stride = float.NaN;
			int si = Array.IndexOf(a, "--stride");
			if (si >= 0 && si + 1 < a.Length) float.TryParse(a[si + 1],
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out stride);
			// --dance <style>,<move>: one standing and one walking figure dancing that move
			(UnitSport.Audio.Cd.MusicStyle Style, int Move)? dance = null;
			int di = Array.IndexOf(a, "--dance");
			if (di >= 0 && di + 1 < a.Length)
			{
				var parts = a[di + 1].Split(',');
				if (parts.Length == 2
					&& Enum.TryParse<UnitSport.Audio.Cd.MusicStyle>(parts[0], true, out var danceStyle)
					&& int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
						System.Globalization.CultureInfo.InvariantCulture, out int danceMove))
					dance = (danceStyle, danceMove);
			}
			AddChild(UnitSport.Avatar.AvatarPreview.Create(
				seconds, output, view, focus, crank, stride, dance));
			return;
		}

		// A load-test process: N headless bots on one connection each, no world of its own.
		if (UnitSport.Net.Swarm.ParseArgs() is { } swarm)
		{
			AddChild(swarm);
			return;
		}

		bool isServer = OS.HasFeature("dedicated_server")
			|| OS.GetCmdlineUserArgs().Contains("--server");

		if (isServer)
			AddChild(new ServerWorld { Name = "World" });
		else
			AddChild(new ClientWorld { Name = "World" });
	}
}
