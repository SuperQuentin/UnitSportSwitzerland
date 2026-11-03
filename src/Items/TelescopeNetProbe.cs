using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--telescopenet A|B</c> with <c>--connect</c> (driven by <c>tools/telescopecheck.sh</c>, #1440): one
/// player at a time at a set-up telescope, between real clients. A sets a telescope up through the
/// item's real path and looks through it; B, beside it, is told someone is looking (the server's
/// broadcast) and is refused packing up A's telescope; A steps back; B then looks through it itself
/// and steps back; A packs it up.
/// </summary>
public partial class TelescopeNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--telescopenet");

    public TelescopeNetProbe(ItemController items) : base(items, "telescopenet", "TS") { }
    public TelescopeNetProbe() : this(null!) { }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => PlacedObjects.Instance != null && Telescopes.Instance != null)) return;
        await Seconds(2.0);   // the join snapshot, and the server's copy of our position
        try
        {
            if (_role == "A") await RunA(Me!, PlacedObjects.Instance!);
            else await RunB(Me!, PlacedObjects.Instance!);
        }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        TelescopeView.Current?.Close();
        await Finish(1.5);
    }

    private async Task RunA(FootPlayer me, PlacedObjects placed)
    {
        // a run that died half way left its telescope on the server: pack it up first
        foreach (var old in placed.All.Values.Where(o => o.Owner == "ScopeA").ToList()) placed.RequestRemove(old.Id);
        await Until(() => placed.All.Values.All(o => o.Owner != "ScopeA"), 5);

        var before = placed.All.Keys.ToHashSet();
        _items.Inventory.Add(ItemId.Telescope, 1);
        int slot = SlotOf(ItemId.Telescope);
        me.LookYaw = 0f;
        me.LookPitch = -0.6f;
        await Until(() => FlagGhost.Aim(me, Placeables.ForItem(ItemId.Telescope)) is { Kind: FlagAimKind.Plant, Valid: true }, 2);
        _items.UseSlot(me, slot);
        Expect(await Until(() => placed.All.Values.Any(o => o.Kind == PlacedKind.Telescope && !before.Contains(o.Id)), 6), "the server set my telescope up");
        var scope = placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.Telescope && !before.Contains(o.Id));
        if (scope == null) { Fail("no telescope to go on with"); return; }

        await StepUpTo(me, placed, scope, 1.2f);
        Expect(Telescopes.TryInteract(me), "E takes it");
        Expect(await Until(() => TelescopeView.Current != null, 6), "the server let me look through it");

        // chat is not replayed to a late joiner: say it until B answers
        for (int i = 0; i < 60 && !_heard.Any(l => l.Contains("TS B busy")); i++)
        {
            Say($"looking {scope.Id}");
            await Heard("B", "busy", 2.5);
        }
        if (!_heard.Any(l => l.Contains("TS B busy"))) { Fail("B never reported"); return; }
        Expect(await Heard("B", "refused", 30), "B could not pack my telescope up");

        TelescopeView.Current?.Close();
        Expect(await Until(() => TelescopeView.Current == null, 2), "I stepped back");
        Say("left");
        Expect(await Heard("B", "looked", 30), "B looked through it once I stepped back");
        Expect(await Heard("B", "done", 30), "B stepped back");

        PlacedResult? packed = null;
        placed.RequestRemove(scope.Id, r => packed = r);
        Expect(await Until(() => packed != null, 5) && packed!.Value.Ok, $"I packed it up ({packed?.Refused})");
        Say("packed");
    }

    private async Task RunB(FootPlayer me, PlacedObjects placed)
    {
        if (!await Heard("A", "looking", 150)) { Fail("A never looked through one"); return; }
        long id = long.Parse(_heard.Last(l => l.Contains("TS A looking")).Split(' ').Last());
        Expect(await Until(() => placed.All.ContainsKey(id) && Telescopes.Instance!.NodeOf(id) != null, 10), "A's telescope is here");
        if (!placed.All.TryGetValue(id, out var scope)) return;

        await StepUpTo(me, placed, scope, -1.2f);
        Expect(await Until(() => Telescopes.InteractionFor(me) is { Text: var t } && t.Contains("Someone is looking"), 10),
            $"told someone is looking ({Telescopes.InteractionFor(me)?.Text})");
        Expect(Telescopes.TryInteract(me) && TelescopeView.Current == null, "E does not open it while A looks");
        Say("busy");

        PlacedResult? tried = null;
        placed.RequestRemove(id, r => tried = r);
        Expect(await Until(() => tried != null, 5) && tried!.Value.Refused != null && placed.All.ContainsKey(id),
            $"packing up A's telescope is refused ({tried?.Refused})");
        Say("refused");

        if (!await Heard("A", "left", 30)) { Fail("A never stepped back"); return; }
        Expect(await Until(() => Telescopes.InteractionFor(me) is { Text: var free } && free.Contains("Look through"), 10), "free again");
        Expect(Telescopes.TryInteract(me), "E takes it");
        Expect(await Until(() => TelescopeView.Current != null, 6), "the server let me look through it now");
        Expect(GetViewport().GetCamera3D()?.Name == "TelescopeCamera", "through its tube");
        Say("looked");
        await Seconds(0.5);
        TelescopeView.Current?.Close();
        Expect(await Until(() => TelescopeView.Current == null, 2), "I stepped back");
        await Seconds(0.5);   // the server freed it before A packs it up
        Say("done");
        Expect(await Heard("A", "packed", 30) && await Until(() => !placed.All.ContainsKey(id), 10), "A packed it up: gone here too");
    }

    /// <summary>Stands <paramref name="side"/> metres from the tripod (negative: the other side), and waits for the server to know.</summary>
    private async Task StepUpTo(FootPlayer me, PlacedObjects placed, PlacedObject scope, float side)
    {
        var at = scope.WorldTransform(placed.Origin).Origin;
        me.GlobalPosition = new Vector3(at.X + side, me.GlobalPosition.Y, at.Z);
        me.Velocity = Vector3.Zero;
        await Seconds(1.0);
    }
}
