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
			if (fi >= 0 && fi + 1 < a.Length) int.TryParse(a[fi + 1], out focus);
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
			AddChild(UnitSport.Avatar.AvatarPreview.Create(
				seconds, output, view, focus, crank, stride));
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
