using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.World;

/// <summary>
/// <c>--pednetcheck A|B</c> with <c>--connect</c> (driven by <c>tools/pednetcheck.sh</c>, real terrain with
/// buildings: a town): the server's pedestrians (#217) between two clients.
/// <list type="bullet">
/// <item>pedestrians come into A's view, and none appears in plain sight near the middle of it;</item>
/// <item>B, standing by A and looking the same way, draws the same people;</item>
/// <item>A turns away (they are no longer drawn) and back: the same ids are there again, moved on along their walk;</item>
/// <item>one behind A, not drawn, is still solid (its capsule is there), and A running backwards into it
/// is stopped by it and knocks it over on the server; B sees it lying where A's capsule was.</item>
/// </list>
/// </summary>
public partial class PedNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--pednetcheck");

    public PedNetProbe(ItemController items) : base(items, "pednet", "PD") { }
    public PedNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);
    private static Pedestrians? Peds => Pedestrians.Instance;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ProcessPriority = 1000;
        if (!await Joined(150, () => Peds != null))
        {
            await Finish(0);
            return;
        }
        var me = Me!;
        var peds = Peds!;
        if (_role == "A") await RunA(me, peds); else await RunB(me, peds);
        await Finish(2);
    }

    private Dictionary<int, Vector3> Drawn(Pedestrians peds)
    {
        var d = new Dictionary<int, Vector3>();
        foreach (var s in peds.Seen()) if (s.Visible) d[s.Id] = s.Pos;
        return d;
    }

    private string Ids(Pedestrians peds) => string.Join(",", Drawn(peds).Keys.OrderBy(i => i));

    private async Task RunA(FootPlayer me, Pedestrians peds)
    {
        Expect(await Until(() => peds.Drawn >= 5, 90), $"pedestrians in A's view ({peds.Drawn} drawn, {peds.Bodies} solid)");
        // watch 8 s: any new one drawn near the middle of the view and close is pop-in
        var known = new HashSet<int>(Drawn(peds).Keys);
        int popIn = 0, fresh = 0;
        double end = Time.GetTicksMsec() / 1000.0 + 8;
        while (Time.GetTicksMsec() / 1000.0 < end)
        {
            var cam = me.Camera.GlobalTransform;
            foreach (var (id, pos) in Drawn(peds))
            {
                if (!known.Add(id)) continue;
                fresh++;
                var to = pos - cam.Origin;
                float angle = Mathf.RadToDeg((-cam.Basis.Z with { Y = 0 }).Normalized().AngleTo((to with { Y = 0 }).Normalized()));
                if (to.Length() < 60f && angle < 25f) { popIn++; GD.Print($"[pednet A] pop-in: #{id} at {to.Length():F0} m, {angle:F0} deg"); }
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        Expect(popIn == 0, $"no pop-in in plain sight ({fresh} new in 8 s, {popIn} close in the middle)");

        Say($"look {me.LookYaw.ToString(System.Globalization.CultureInfo.InvariantCulture)} {Ids(peds)}");
        Expect(await Heard("B", "same", 30), "B draws the same people");

        // turn away and back
        var before = Drawn(peds);
        me.LookYaw += Mathf.Pi;
        await Seconds(6);
        var behind = Drawn(peds);
        int stillDrawn = before.Keys.Count(behind.ContainsKey);
        me.LookYaw -= Mathf.Pi;
        await Seconds(3);
        var after = Drawn(peds);
        int back = before.Keys.Count(after.ContainsKey);
        float worst = before.Where(kv => after.ContainsKey(kv.Key)).Select(kv => (after[kv.Key] - kv.Value).Length()).DefaultIfEmpty(0).Max();
        Expect(back >= before.Count * 0.6f, $"turned back: {back} of {before.Count} the same ids again ({stillDrawn} still drawn while turned away)");
        Expect(worst < 9 * 1.6f + 3f, $"moved on along their walk, not jumped (largest move {worst:F1} m in 9 s)");

        // one behind A, out of view: solid
        (int Id, Vector3 Pos)? target = null;
        await Until(() =>
        {
            var fwd = (-me.Camera.GlobalBasis.Z with { Y = 0 }).Normalized();
            foreach (var s in peds.Seen())
            {
                var to = (s.Pos - me.GlobalPosition) with { Y = 0 };
                if (s.Body && !s.Visible && (s.Flags & Pedestrians.FlagKnocked) == 0 && to.Length() is > 3f and < 25f && fwd.Dot(to.Normalized()) < -0.8f)
                { target = (s.Id, s.Pos); return true; }
            }
            return false;
        }, 60);
        Expect(target != null, "a pedestrian behind A, not drawn, with a body");
        if (target is not { } t) return;
        var space = me.GetWorld3D().DirectSpaceState;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(t.Pos + Vector3.Up * 3f, t.Pos + Vector3.Down, TreeColliders.Layer));
        Expect(hit.Count > 0 && hit["collider"].AsGodotObject() is AnimatableBody3D, "its capsule is where it is, unseen");
        Say($"bump {t.Id}");
        // straight back at it, running
        float nearest = float.MaxValue;
        bool knocked = false;
        Input.ActionPress(PlayerInput.MoveBack);
        Input.ActionPress(PlayerInput.Sprint);
        end = Time.GetTicksMsec() / 1000.0 + 12;
        while (Time.GetTicksMsec() / 1000.0 < end && !knocked)
        {
            foreach (var s in peds.Seen())
                if (s.Id == t.Id)
                {
                    nearest = Mathf.Min(nearest, ((s.Pos - me.GlobalPosition) with { Y = 0 }).Length());
                    knocked = (s.Flags & Pedestrians.FlagKnocked) != 0;
                    // steer the back at it as it walks
                    var away = (me.GlobalPosition - s.Pos) with { Y = 0 };
                    if (away.LengthSquared() > 0.01f) me.LookYaw = Mathf.Atan2(-away.X, -away.Z);
                }
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        Input.ActionRelease(PlayerInput.MoveBack);
        Input.ActionRelease(PlayerInput.Sprint);
        Expect(knocked, $"A ran into #{t.Id} unseen and the server knocked it over");
        Expect(nearest > 0.35f, $"and did not pass through it (closest {nearest:F2} m)");
        Say($"knocked {t.Id}");
        await Heard("B", "seenknocked", 20);
    }

    private async Task RunB(FootPlayer me, Pedestrians peds)
    {
        if (!await Heard("A", "look", 150)) { Fail("A never looked"); return; }
        var line = _heard.Last(l => l.Contains("PD A look"));
        var words = line[(line.IndexOf("look ") + 5)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var theirs = (words.Length > 1 ? words[1] : "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        // the same street: B looks where A looks
        me.LookYaw = Float(words[0]);
        await Seconds(3);
        var mine = Drawn(peds).Keys.ToHashSet();
        int both = theirs.Count(mine.Contains);
        Expect(both >= Mathf.Min(theirs.Count, mine.Count) * 0.5f, $"B draws {both} of A's {theirs.Count} ({mine.Count} drawn here)");
        Say("same");

        if (!await Heard("A", "knocked", 120)) { Fail("A never knocked one"); return; }
        line = _heard.Last(l => l.Contains("PD A knocked"));
        int id = int.Parse(line[(line.IndexOf("knocked ") + 8)..].Trim());
        Expect(await Until(() => peds.Seen().Any(s => s.Id == id && (s.Flags & Pedestrians.FlagKnocked) != 0), 6) || !peds.Seen().Any(s => s.Id == id),
            $"B sees #{id} knocked over (or it is out of B's range)");
        Say("seenknocked");
    }
}
