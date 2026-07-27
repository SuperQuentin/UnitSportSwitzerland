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
		bool isServer = OS.HasFeature("dedicated_server")
			|| OS.GetCmdlineUserArgs().Contains("--server");

		if (isServer)
			AddChild(new ServerWorld { Name = "World" });
		else
			AddChild(new ClientWorld { Name = "World" });
	}
}
