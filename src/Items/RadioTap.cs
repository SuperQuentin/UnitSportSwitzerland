using System;
using Godot;
using UnitSport.Audio;
using UnitSport.Audio.Cd;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Items;

/// <summary>
/// Tap a radio to switch it on or off, hold the same key to open its panel (#725): E on the
/// pointed radio, Use on the one in the hand, the same buttons on a pad. One press at a time:
/// <see cref="Begin"/> on the press, <see cref="Tick"/> every frame (from <c>ItemController</c>)
/// until the key comes up (a tap) or has been held <see cref="HoldTime"/> (a hold).
/// </summary>
public static class RadioTap
{
    /// <summary>Seconds a key must stay down to count as a hold.</summary>
    public const float HoldTime = 0.35f;

    private static StringName? _action;
    private static Action? _tap, _hold;
    private static Func<bool>? _valid;
    private static float _held = -1f;

    /// <summary>0..1 while a press is being timed (for a fill on the prompt), else -1.</summary>
    public static float Progress => _held < 0f ? -1f : Mathf.Clamp(_held / HoldTime, 0f, 1f);

    /// <summary>The prompt beside the key: what a tap and a hold do, and "keep holding" while it is held.</summary>
    public static string Prompt => _held < 0f ? "Radio on / off · hold: open it" : "Keep holding: the radio's panel";

    /// <summary>
    /// Starts timing a press of <paramref name="action"/>: <paramref name="tap"/> when it comes up
    /// early, <paramref name="hold"/> once held long enough; neither when <paramref name="valid"/>
    /// turns false first (the radio is no longer pointed at or held).
    /// </summary>
    public static void Begin(StringName action, Action tap, Action hold, Func<bool> valid)
    {
        _action = action;
        _tap = tap;
        _hold = hold;
        _valid = valid;
        _held = 0f;
    }

    public static void Cancel()
    {
        _held = -1f;
        _action = null;
        _tap = _hold = null;
        _valid = null;
    }

    public static void Tick(float dt)
    {
        if (_held < 0f || _action == null) return;
        if (_valid?.Invoke() != true) { Cancel(); return; }
        if (!PlayerInput.Held(_action))
        {
            var tap = _tap;
            Cancel();
            tap?.Invoke();
            return;
        }
        if ((_held += dt) < HoldTime) return;
        var hold = _hold;
        Cancel();
        hold?.Invoke();
    }

    // ---- switching --------------------------------------------------------------------------

    /// <summary>On or off, a radio lying in the world: off stops it, on plays its last CD (else the first one).</summary>
    public static void Toggle(RadioBody radio)
    {
        if (!GodotObject.IsInstanceValid(radio) || RadioManager.Instance is not { } manager) return;
        Clack();
        radio.Poke();
        if (radio.Playing && radio.WantedPosition < radio.Length) { manager.Stop(radio); return; }
        if (Pick(radio.CdId) is not { } cd) { Empty(); return; }
        manager.Play(radio, cd.Id, cd.Duration);
        NowPlaying(cd);
    }

    /// <summary>On or off, the radio in hotbar <paramref name="slot"/>: off keeps the CD in it (<see cref="RadioPlay.Off"/>).</summary>
    public static void ToggleHeld(Inventory inventory, int slot)
    {
        var stack = inventory[slot];
        if (stack.IsEmpty || stack.Id != ItemId.Radio) return;
        Clack();
        var now = ClockSync.ServerNow;
        if (RadioPlay.Decode(stack.Data) is { } playing && playing.Sounding(now))
        {
            inventory.SetData(slot, RadioPlay.Off(playing));
            return;
        }
        var last = RadioPlay.DecodeAny(stack.Data);
        if (Pick(last?.CdId ?? 0) is not { } cd) { Empty(); return; }
        inventory.SetData(slot, new RadioPlay(cd.Id, now, cd.Duration, last?.Mode ?? RadioMode.Once).Encode());
        NowPlaying(cd);
    }

    /// <summary>The CD to put on: <paramref name="last"/> when the library still has it, else the first in play order.</summary>
    private static CdInfo? Pick(int last)
    {
        if (CdLibrary.Instance is not { } library) return null;
        if (last != 0 && library.Find(last) is { } cd) return cd;
        foreach (int id in RadioQueue.Order(library, withPersonal: true))
            if (library.Find(id) is { } first) return first;
        return null;
    }

    private static void Clack() => ItemController.Instance?.PlaySound(SfxSynth.Clack, 0.95f + 0.1f * GD.Randf());

    private static void NowPlaying(CdInfo cd) => ItemController.Instance?.Toast($"♪ {cd.Title}");

    private static void Empty() =>
        ItemController.Instance?.Toast(InputHints.Format("No CDs yet: hold {use_item} or {interact_mount} on a radio to burn one."));
}
