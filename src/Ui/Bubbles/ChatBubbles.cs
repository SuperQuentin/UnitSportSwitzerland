using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Ui.Bubbles;

/// <summary>
/// Comic speech bubbles over the players who talk in the chat near you (#862,
/// docs/notes/ui/chat-bubbles.md): the films' bubble (<see cref="SpeechBubble"/>) over the
/// speaker's head for a few seconds, typed in, within <see cref="Range"/> m of the camera and in
/// view; never for a command or a system line. A line in capitals (or ending "!!") is shouted, a
/// line in parentheses is thought. One bubble per speaker: a new line replaces it.
/// Online the server says who spoke (<see cref="ChatManager.Spoken"/>); offline it is you.
/// On a flat screen a 2D layer under the HUD places them like the films' (none over another);
/// in VR (<see cref="XR.XrSession.Active"/>) each is a billboard in the world over the head,
/// its bubble drawn in a small 2D viewport, since the 2D layers are only a panel in the headset.
/// Setting: <see cref="GameSettings.ChatBubbles"/> (Settings, Gameplay). Nothing runs per frame
/// while nobody is talking.
/// </summary>
public partial class ChatBubbles : CanvasLayer
{
    /// <summary>Metres from the camera within which a speaker's bubble shows.</summary>
    public const float Range = 36f;
    private const int Max = 8;

    private readonly ChatManager _chat;
    private readonly Func<long, FootPlayer?> _speaker;
    private readonly List<Entry> _entries = new();
    private readonly BubbleWant[] _wants = new BubbleWant[Max];
    private readonly BubblePlaced[] _placed = new BubblePlaced[Max];
    private readonly Entry?[] _on = new Entry?[Max];
    private int _seed;

    private sealed class Entry
    {
        public long Peer;
        public FootPlayer? Body;
        public BubbleShape Shape;
        public double Born, Seconds;
        public SpeechBubble View = null!;
        // VR: the bubble's own 2D viewport on a billboard
        public SubViewport? Port;
        public Sprite3D? Sprite;
    }

    public ChatBubbles(ChatManager chat, Func<long, FootPlayer?> speaker)
    {
        Name = "ChatBubbles";
        Layer = 4;   // over the world, under the HUD (prompts 11, chat 15) and the menus
        _chat = chat;
        _speaker = speaker;
    }

    public override void _Ready()
    {
        _chat.Spoken += OnSpoken;
        if (CheckRequested)
        {
            // one's own bubble needs the camera off one's head
            GameSettings.Current.ThirdPerson = true;
            GameSettings.Current.ChatBubbles = true;
            RunCheck();
        }
    }

    /// <summary>
    /// <c>--bubblecheck</c> (with <c>--autostart</c>, on foot): once one's body has stood a few
    /// seconds, a command (no bubble) then a line said (a bubble over one's head) then one shouted;
    /// screenshots in <c>test_output/bubbles/</c>, <c>[bubbles] RESULT</c>, quits.
    /// </summary>
    public static bool CheckRequested => CmdArgs.Has("--bubblecheck");

    private async void RunCheck()
    {
        var tree = GetTree();
        double start = RealClock.Now, found = double.NaN;
        while (RealClock.Now - start < 240)
        {
            if (_speaker(ChatManager.OfflineSelf) != null && double.IsNaN(found)) found = RealClock.Now;
            if (!double.IsNaN(found) && RealClock.Now - found > 8) break;
            await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        var problems = new List<string>();
        if (double.IsNaN(found)) problems.Add("no body of one's own to speak from");
        string dir = ProjectSettings.GlobalizePath("res://test_output/bubbles");
        System.IO.Directory.CreateDirectory(dir);
        async System.Threading.Tasks.Task Say(string line, string png, BubbleShape? want)
        {
            _chat.Send(line);
            await ToSignal(tree.CreateTimer(1.6, true, false, true), SceneTreeTimer.SignalName.Timeout);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var shown = _entries.Find(e => e.View.Visible || e.Sprite?.Visible == true);
            if (want == null && _entries.Count > 0) problems.Add($"\"{line}\" made a bubble");
            if (want != null && (shown == null || shown.Shape != want)) problems.Add($"\"{line}\": {(shown == null ? "no bubble on screen" : $"a {shown.Shape} bubble, not {want}")}");
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, png));
            GD.Print($"[bubbles] {line} -> {png}: {_entries.Count} bubble(s)");
        }
        await Say("/time 13", "command.png", null);
        await Say("Anyone up for a fondue on the Gornergrat?", "say.png", BubbleShape.Say);
        await Say("NOBODY TOUCHES MY CHEESE", "shout.png", BubbleShape.Shout);
        foreach (var p in problems) GD.PrintErr($"[bubbles] {p}");
        GD.Print(problems.Count == 0 ? "[bubbles] RESULT: ok" : "[bubbles] RESULT: FAILED");
        tree.Quit(problems.Count == 0 ? 0 : 1);
    }

    public override void _ExitTree() => _chat.Spoken -= OnSpoken;

    private void OnSpoken(long peer, string text)
    {
        if (!GameSettings.Current.ChatBubbles || text.Length == 0) return;
        if (_speaker(peer) is not { } body) return;
        var e = _entries.Find(x => x.Peer == peer);
        if (e == null)
        {
            if (_entries.Count >= Max) Drop(_entries[0]);
            e = new Entry { Peer = peer, View = new SpeechBubble { Visible = false } };
            _entries.Add(e);
        }
        else
        {
            // the newest line last: it is placed after the others
            _entries.Remove(e);
            _entries.Add(e);
        }
        e.Body = body;
        e.Shape = ShapeOf(text);
        e.Born = RealClock.Now;
        e.Seconds = BubbleGeometry.ReadingTime(text.Length, e.Shape) + 1.0;
        bool vr = XR.XrSession.Active;
        float h = vr ? 720f : GetViewport().GetVisibleRect().Size.Y;
        e.View.Set(text.Trim('(', ')'), e.Shape, h * 0.026f, null, ++_seed);
        if (vr) Billboard(e);
        else if (e.View.GetParent() == null) AddChild(e.View);
    }

    /// <summary>Capitals (four letters or more) or "!!" shout; a line in parentheses is a thought.</summary>
    public static BubbleShape ShapeOf(string text)
    {
        var t = text.Trim();
        if (t.Length > 2 && t[0] == '(' && t[^1] == ')') return BubbleShape.Think;
        if (t.EndsWith("!!", StringComparison.Ordinal)) return BubbleShape.Shout;
        int upper = 0, lower = 0;
        foreach (char c in t)
            if (char.IsUpper(c)) upper++;
            else if (char.IsLower(c)) lower++;
        return upper >= 4 && lower == 0 ? BubbleShape.Shout : BubbleShape.Say;
    }

    /// <summary>VR: the bubble in a viewport of its own, shown on a sprite that faces the head.</summary>
    private void Billboard(Entry e)
    {
        if (e.Port == null)
        {
            e.Port = new SubViewport { TransparentBg = true, Disable3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible };
            e.Sprite = new Sprite3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Shaded = false,
                DoubleSided = false,
                AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
                Texture = e.Port.GetTexture(),
            };
            AddChild(e.Port);
            e.Port.AddChild(e.View);
            if (GetViewport().GetCamera3D()?.GetParent() is { } world) world.AddChild(e.Sprite);
            else AddChild(e.Sprite);
        }
        // room round the body for the pop, and under it for the tail to the head
        var size = e.View.Size;
        float tail = size.Y * 0.45f;
        e.Port.Size = new Vector2I(Mathf.CeilToInt(size.X * 1.2f), Mathf.CeilToInt(size.Y * 1.1f + tail));
        e.View.Position = new Vector2(size.X * 0.1f, size.Y * 0.05f);
        e.View.Point(new Vector2(size.X * 0.5f, size.Y + tail));
        // about 1.4 m wide at hand, so it reads at the length of a room
        e.Sprite!.PixelSize = 1.4f / e.Port.Size.X;
    }

    private void Drop(Entry e)
    {
        _entries.Remove(e);
        e.View.QueueFree();
        e.Port?.QueueFree();
        e.Sprite?.QueueFree();
    }

    public override void _Process(double delta)
    {
        if (_entries.Count == 0) return;
        double now = RealClock.Now;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (now - e.Born > e.Seconds || e.Body == null || !IsInstanceValid(e.Body) || !GameSettings.Current.ChatBubbles) Drop(e);
        }
        if (_entries.Count == 0 || GetViewport().GetCamera3D() is not { } camera) return;
        var eye = camera.GlobalPosition;
        var frame = GetViewport().GetVisibleRect();
        int n = 0;
        foreach (var e in _entries)
        {
            var head = e.Body!.HeadTop;
            float distance = eye.DistanceTo(head);
            // in range, in front, and not one's own head seen from inside it (first person)
            bool near = distance <= Range && distance > 1.2f && !camera.IsPositionBehind(head);
            double age = now - e.Born;
            float pop = BubbleGeometry.Pop(age), alpha = BubbleGeometry.Fade(age, e.Seconds);
            e.View.Reveal(BubbleGeometry.Typed(e.View.Letters, age - 0.12, e.Shape));
            if (e.Sprite != null)
            {
                e.Sprite.Visible = near;
                if (!near) continue;
                // the tail's end at the head; farther, larger, so it still reads
                float k = Mathf.Clamp(distance / 9f, 1f, 3.2f) * pop;
                e.Sprite.Scale = new Vector3(k, k, k);
                float tall = e.Port!.Size.Y * e.Sprite.PixelSize * k;
                e.Sprite.GlobalPosition = head + Vector3.Up * (tall * 0.5f);
                e.Sprite.Modulate = new Color(1, 1, 1, alpha);
                continue;
            }
            var at = camera.UnprojectPosition(head);
            if (!near || !frame.HasPoint(at) || n >= Max) { e.View.Visible = false; continue; }
            e.View.Visible = true;
            _on[n] = e;
            _wants[n] = new BubbleWant(at, e.View.Size);
            n++;
        }
        if (n == 0) return;
        float h = frame.Size.Y;
        BubbleLayout.Place(_wants.AsSpan(0, n), frame, h * 0.02f, h * 0.01f, h * 0.05f, _placed);
        for (int k = 0; k < n; k++)
        {
            var e = _on[k]!;
            var place = _placed[k];
            e.View.Position = place.Body.Position;
            e.View.Point(place.Tip - place.Body.Position);
            int z = -Mathf.Min(1000, (int)(place.Reach / 4f));
            if (e.View.ZIndex != z) e.View.ZIndex = z;
            double age = now - e.Born;
            e.View.Pop(BubbleGeometry.Pop(age), BubbleGeometry.Fade(age, e.Seconds));
        }
    }
}
