using Godot;
using UnitSport.Net;

namespace UnitSport.Player;

/// <summary>
/// The name over another player's head on every client (#1136), with "VR" or "MOBILE" under it
/// when they play on a headset or a phone (<see cref="FootPlayer.DeviceKind"/>). Drawn to 60 m like
/// a bot's tag (<c>Bots/BotTag</c>) and hidden by walls (depth-tested); a player out of sight is
/// not on this client at all (vision interest), so neither is their tag. The name is the server's
/// (<see cref="ChatManager.TagNames"/>), never one the player's own client claims.
/// </summary>
public partial class NameTag : Label3D
{
    /// <summary>Over the top of the head (<see cref="FootPlayer.HeadTop"/> is already just over it).</summary>
    private const float Lift = 0.2f;

    private readonly FootPlayer _body;
    private ChatManager? _chat;
    private string _name = "";
    private int _device = -1;

    public NameTag(FootPlayer body)
    {
        _body = body;
        Name = "NameTag";
        FontSize = 32;
        PixelSize = 0.006f;
        OutlineSize = 8;
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        Shaded = false;
        VisibilityRangeEnd = 60f;
        CastShadow = ShadowCastingSetting.Off;
        TopLevel = true;   // placed at the head each frame: a rider's or a passenger's is not over the body's origin
    }

    public override void _Ready()
    {
        _chat = _body.GetNodeOrNull<ChatManager>("../../" + ChatManager.NodeName);
        if (_chat != null) _chat.TagNamesReceived += OnNames;
        OnNames();
    }

    public override void _ExitTree()
    {
        if (_chat != null) _chat.TagNamesReceived -= OnNames;
    }

    private void OnNames()
    {
        string name = _chat != null && _chat.TagNames.TryGetValue(_body.GetMultiplayerAuthority(), out var n) ? n : "";
        if (name == _name) return;
        _name = name;
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (_body.DeviceKind != _device)
        {
            _device = _body.DeviceKind;
            Refresh();
        }
        if (Visible) GlobalPosition = _body.HeadTop + Vector3.Up * Lift;
    }

    private void Refresh()
    {
        Text = Label(_name, _device);
        Visible = Text.Length > 0;
    }

    /// <summary>The tag's text: the name, and the device under it when it is not a computer.</summary>
    public static string Label(string name, int device) =>
        name.Length == 0 ? "" : device switch
        {
            FootPlayer.DeviceVr => $"{name}\nVR",
            FootPlayer.DeviceMobile => $"{name}\nMOBILE",
            _ => name,
        };
}
