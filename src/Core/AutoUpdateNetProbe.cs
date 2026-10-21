using Godot;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Core;

/// <summary>
/// <c>--autoupdatenet A|B &lt;flag file&gt;</c> with <c>--connect</c> (driven by <c>tools/autoupdatenetcheck.sh</c>, #1088):
/// the server installs a newer release by itself, but never under a player.
/// <list type="bullet">
/// <item>A logs in as admin and waits for the server's word that a release is out and downloaded (the check's feed
/// appears only once A is in, so the server cannot restart before). It then stays until the flag file exists,
/// and leaves: the script expects the server to restart only after that.</item>
/// <item>B speaks a newer protocol (<c>--fakeprotocol</c>): refused, and the refusal says the server catches up by itself.</item>
/// </list>
/// </summary>
public partial class AutoUpdateNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--autoupdatenet");

    public AutoUpdateNetProbe(ItemController items) : base(items, "autoupdate") { }
    public AutoUpdateNetProbe() : this(null!) { }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (_role == "B") { await RunB(); return; }
        if (!await Joined(150) || !await LogIn("test")) { await Finish(0); return; }
        GD.Print($"{Log} logged in");
        string? told = null;
        await Until(() => (told = _heard.FirstOrDefault(l => l.Contains("is out and downloaded"))) != null, 120);
        Expect(told != null, $"the admin is told about the release: {told ?? "nothing heard"}");
        string flag = CmdArgs.Value("--autoupdatenet", at: 2) ?? "";
        Expect(await Until(() => File.Exists(flag), 120), $"the script's go to leave ({flag})");
        await Finish(0);
    }

    private async Task RunB()
    {
        // the world keeps the refusal's reason: no race with a handshake that answers within a frame
        string? reason = null;
        await Until(() => (reason = (GetParent() as ClientWorld)?.Failure) != null, 60);
        GD.Print($"{Log} refused: {reason}");
        Expect(reason?.Contains("older version") == true, "refused as a newer client");
        Expect(reason?.Contains("v9.9.9 as soon as nobody is playing") == true, "the refusal says the server installs v9.9.9 by itself");
        await Finish(0);
    }
}
