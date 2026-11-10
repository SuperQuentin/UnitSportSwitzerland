using Godot;

namespace UnitSport.Core;

/// <summary>
/// Entry point: boots into the dedicated server world (exported with the
/// dedicated_server feature or run with "-- --server") or the client world.
/// </summary>
public partial class Main : Node
{
	/// <summary>
	/// Names the window after what this run is (<c>--title "..."</c>, else its user args minus
	/// the --chunks/--cache paths), so parallel test windows can be told apart.
	/// </summary>
	private static void SetWindowTitle(Window window)
	{
		var args = CmdArgs.All;
		string what;
		if (CmdArgs.Value("--title") is { } title) what = title;
		else
		{
			var shown = new System.Collections.Generic.List<string>();
			for (int i = 0; i < args.Length; i++)
				if (args[i] is "--chunks" or "--cache") i++;
				else shown.Add(args[i]);
			what = string.Join(' ', shown);
		}
		if (what.Length > 100) what = what[..100] + "…";
		// the root Window re-applies its own Title over DisplayServer.WindowSetTitle, so set it there
		if (what.Length > 0) window.Title = $"UnitSportSwitzerland — {what}";
	}

	private static double _lv95WarnedAt = double.NegativeInfinity;

	/// <summary>
	/// <see cref="Terrain.Format.SwissGeodesy.OutsideDomain"/>: a point within ~0.13° of Bern's antimeridian has no
	/// right LV95 place (#1480). Logged with its stack once every 10 s (an orbit crosses the sliver in about 2 s);
	/// any thread may call it.
	/// </summary>
	private static void WarnOutsideLv95(Terrain.Format.EcefPos p)
	{
		double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
		if (now - _lv95WarnedAt < 10) return;
		_lv95WarnedAt = now;
		var g = Terrain.Format.Ellipsoid.Wgs84.ToGeodetic(p);
		GD.PushWarning(System.FormattableString.Invariant(
			$"[geodesy] LV95 asked for {g.LatDeg:F3}° {g.LonDeg:F3}°, on Bern's antimeridian where it does not round-trip (up to 31 km off): use ECEF there (#1480): {System.Environment.StackTrace}"));
	}

	public override void _Ready()
	{
		// MVR textures Godot decodes: here, not in a module initializer (#1010, Android)
		Concert.ConcertRig.UseGodotImages();
		// an LV95 place from the sliver on Bern's antimeridian is up to 31 km off: never quietly (#1480)
		Terrain.Format.SwissGeodesy.OutsideDomain = WarnOutsideLv95;
		SetWindowTitle(GetWindow());
		MouseCapture.PassTestWindowThrough();
		GameSettings.LowerTestPriority();
		// --clip name,delay,seconds: films whatever follows, a probe or a shot queue (#487)
		ClipRecorder.StartFromArgs(GetTree());

		// the quick self-checks (--interestcheck, --beatcheck…) run in ClientWorld.QuickChecks

		// LAN discovery probe: browse mDNS for dedicated servers, list them, quit (docs/notes/net/lan-discovery.md)
		int dc = Array.IndexOf(CmdArgs.All, "--discovercheck");
		if (dc >= 0)
		{
			var a = CmdArgs.All;
			double wait = 8;
			if (dc + 1 < a.Length) double.TryParse(a[dc + 1], System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out wait);
			var lan = new UnitSport.Net.LanDiscovery();
			lan.Start();
			// and the status query the Multiplayer screen uses: broadcast once a second
			var query = new UnitSport.Net.ServerQuery();
			query.Start();
			var tick = new Godot.Timer { WaitTime = 1, Autostart = true };
			tick.Timeout += () => query.Broadcast();
			AddChild(tick);
			GetTree().CreateTimer(wait).Timeout += () =>
			{
				lan.Poll();
				query.Poll();
				foreach (var s in lan.Servers) GD.Print($"[discover] mdns {s.Name} {s.Endpoint} version={s.Version}");
				foreach (var r in query.Lan) GD.Print($"[discover] udp {r.Status.Name} {r.Endpoint} players={r.Status.Players}/{r.Status.Max} ping={r.PingMs}ms world={r.Status.World}");
				bool ok = lan.Servers.Count > 0 || query.Lan.Count > 0;
				lan.Dispose();
				query.Dispose();
				GD.Print(ok ? "[discover] RESULT: ok" : "[discover] RESULT: FAILED (no server answered)");
				GetTree().Quit(ok ? 0 : 1);
			};
			return;
		}

#if TRAILER
		// the films' subtitle files (#862): no world, a file job for tools/trailer.sh
		if (UnitSport.Trailer.SubtitleCut.Requested)
		{
			AddChild(new UnitSport.Trailer.SubtitleCut { Name = "SubtitleCut" });
			return;
		}
#endif

		// A model turntable, before any world is built: the avatars are the subject, so
		// there is no point streaming terrain to look at them.
		if (UnitSport.Avatar.AvatarPreview.Requested(out double seconds, out string output))
		{
			// the figures are built and shaded for the visual style: the saved one, or --style
			GameSettings.Load();
			float view = 90;
			var a = CmdArgs.All;
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

		// a test that names its systems (#221, Core/Systems): physics off is a stub, the space never steps;
		// --world flat is the flat TestWorld, no map at all
		if (!Systems.On(Systems.Physics)) PhysicsServer3D.SetActive(false);
		if (Systems.World == Systems.WorldKind.Flat)
		{
			AddChild(new TestWorld { Name = "World" });
			return;
		}

		if (UnitSport.Audio.SoundPlayer.Requested())
		{
			AddChild(new UnitSport.Audio.SoundPlayer { Name = "SoundPlayer" });
			return;
		}

		// G1's cube-tile streaming and collision harness (#1443): a region on its own, no game
		if (UnitSport.Terrain.CubeHarness.Requested())
		{
			AddChild(new UnitSport.Terrain.CubeHarness { Name = "CubeHarness" });
			return;
		}

		// the leaves' aerial photos into a cube region (#1507, G2 B3 row 7): a tool, no game
		if (UnitSport.World.CubePhotoBake.Requested())
		{
			AddChild(new UnitSport.World.CubePhotoBake { Name = "CubePhotoBake" });
			return;
		}

		// the globe's colour bake into a --cube-global region (#1430 H4): a tool, no game
		if (UnitSport.World.GlobeAlbedoBake.Requested())
		{
			AddChild(new UnitSport.World.GlobeAlbedoBake { Name = "GlobeAlbedoBake" });
			return;
		}

		if (UnitSport.Avatar.ModelViewer.Requested())
		{
			// shaded for the visual style, as the turntable above: the saved one, or --style (#760:
			// without this every model showed in PS1 whatever the flag said)
			GameSettings.Load();
			AddChild(new UnitSport.Avatar.ModelViewer { Name = "ModelViewer" });
			return;
		}

		bool isServer = OS.HasFeature("dedicated_server")
			|| CmdArgs.Has("--server");

		if (isServer)
		{
			AddChild(new ServerWorld { Name = "World" });
			return;
		}

		// The client: the shell (menus, loading screen, window settings) and, once a mode is
		// picked, the world beside it at /root/Main/World. A run that names a session or a tool on
		// its command line skips the title and builds the world straight away (GameShell.UseTitle).
		GameSettings.Load();
		if (GameShell.UseTitle(CmdArgs.All) || GameShell.PlayAt(CmdArgs.All))
		{
			AddChild(new GameShell { Name = "Shell" });
			return;
		}
		var shell = new GameShell { Name = "Shell", Direct = true };
		AddChild(shell);
		var world = new ClientWorld { Name = "World", Launch = WorldLaunch.FromArgs() };
		shell.Attach(world);
		AddChild(world);
	}

	// after every child's _ExitTree (Godot exits children first): saves still queued reach the disk
	public override void _ExitTree()
	{
		SaveQueue.Flush();
		FrameReport.AtExit();
	}
}
