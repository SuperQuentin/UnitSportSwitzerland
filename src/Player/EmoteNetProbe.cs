using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>
/// <c>--emotenet A|B</c> with <c>--connect</c> (driven by <c>tools/emotenetcheck.sh</c>, #404): what
/// another peer sees of an emote, with no music anywhere.
/// <list type="bullet">
/// <item>A picks emotes off the wheel's catalog (YMCA, then Wave), then stops.</item>
/// <item>B checks A's copy for each: the same <c>DanceId</c>, the emote drawn at full weight, on the
/// same beat as the shared clock gives here (so both peers dance the same step), and the copy back
/// at rest once A stops.</item>
/// </list>
/// Windowed (<c>SHOTS=1</c>), B saves its view of A to <c>test_output/emotenet_B_*.png</c>.
/// </summary>
public partial class EmoteNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--emotenet");

    public EmoteNetProbe(ItemController items) : base(items, "emotenet", "EM", "emotenet_") { }
    public EmoteNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    /// <summary>YMCA (a dance) and Wave (a gesture): catalog indices.</summary>
    private static readonly int[] Picks = { 12, 0 };

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    private async Task RunA(FootPlayer me)
    {
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }
        foreach (int emote in Picks)
        {
            me.DanceId = FootPlayer.EmoteDanceBase + emote;
            await Seconds(0.5);
            Expect(me.DrawnDance is { } d && d.Move == HumanMeshBuilder.EmoteMoves + emote, $"A draws {HumanMeshBuilder.EmoteName(emote)}");
            Say($"emote {emote}");
            Expect(await Heard("B", $"seen {emote}", 12), $"B saw {HumanMeshBuilder.EmoteName(emote)}");
        }
        me.DanceId = 0;
        Say("stopped");
        Expect(await Heard("B", "seen stopped", 12), "B saw A stop");
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        foreach (int emote in Picks)
        {
            if (!await Heard("A", $"emote {emote}", 20)) { Fail($"A never played emote {emote}"); return; }
            string name = HumanMeshBuilder.EmoteName(emote);
            float lag = float.NaN;
            bool ok = await Until(() =>
            {
                if (Copy() is not { } a || a.DanceId != FootPlayer.EmoteDanceBase + emote
                    || a.DrawnDance is not { } d || d.Move != HumanMeshBuilder.EmoteMoves + emote || d.Weight < 0.99f) return false;
                // the bar phase the shared clock gives here, at FootPlayer.FreeBpm
                double bars = Net.ClockSync.ServerNow * FootPlayer.FreeBpm / 60.0 / 4.0;
                lag = Mathf.Abs((float)(bars - System.Math.Floor(bars)) - d.BarPhase);
                lag = Mathf.Min(lag, 1f - lag);
                return lag < 0.05f;
            }, 6);
            Expect(ok, $"A's copy dances {name} at full weight, on this peer's beat (off by {lag:F3} bar)");
            await Seconds(0.8);
            // and its face (#657): the emote's expression, from the replicated DanceId alone
            if (Copy() is { } seen)
            {
                var mood = Avatar.Face.FaceExpressions.ForEmote(emote);
                Expect(seen.FaceMood == mood, $"A's copy's face shows {mood} for {name} (shows {seen.FaceMood})");
                var want = Avatar.Face.FaceExpressions.Of(mood);
                var drawn = seen.DrawnFace;
                if (GetViewport().GetCamera3D() is not { } cam || cam.GlobalPosition.DistanceTo(seen.GlobalPosition) < FootPlayer.FaceDrawDistance)
                    Expect(drawn.Special == want.Special && Mathf.Abs(drawn.Smile - want.Smile) < 0.1f && Mathf.Abs(drawn.Jaw - want.Jaw) < 0.1f,
                        $"A's copy's face drawn as {mood} (smile {drawn.Smile:F2}, jaw {drawn.Jaw:F2}, eyes {drawn.Special})");
            }
            ShotOfA($"B_{name.ToLowerInvariant()}");
            Say($"seen {emote}");
        }
        if (!await Heard("A", "stopped", 20)) { Fail("A never stopped"); return; }
        Expect(await Until(() => Copy() is { DanceId: 0, DrawnDance: null }, 3), "A's copy eases back to rest");
        Expect(Copy() is { FaceMood: Avatar.Face.FaceExpression.Neutral }, "A's copy's face back to neutral");
        Say("seen stopped");
    }

    /// <summary>Windowed: turn to A's copy and save the view.</summary>
    private void ShotOfA(string name)
    {
        if (DisplayServer.GetName() == "headless" || Copy() is not { } a || Me is not { } me) return;
        var to = a.GlobalPosition + Vector3.Up - (me.GlobalPosition + Vector3.Up * 1.68f);
        me.LookYaw = Mathf.Atan2(-to.X, -to.Z);
        me.LookPitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
        GD.Print($"{Log} shot {Shot(name)}");
    }

    /// <summary>A's copy here: the one player node that is not ours.</summary>
    private FootPlayer? Copy()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != Me && !p.IsMultiplayerAuthority()) return p;
        return null;
    }
}
