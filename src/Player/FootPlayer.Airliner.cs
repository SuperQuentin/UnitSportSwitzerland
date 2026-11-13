using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

// An airliner's levers and switches (#414): pressed keys handed to the aircraft for its next step.
public partial class FootPlayer
{
    /// <summary>Keys that mean something only at an airliner's controls. True when this event was one of them.</summary>
    private bool HandleAirlinerInput(InputEvent e, Airliner airliner)
    {
        if (AirlinerPadSwitch(e, airliner)) return true;
        if (!e.IsPressed() || e.IsEcho() || SeatIndex != 0) return false;
        if (e.IsActionPressed(PlayerInput.FlapsDown)) { airliner.Command(AirlinerCommand.FlapsDown); return true; }
        if (e.IsActionPressed(PlayerInput.FlapsUp)) { airliner.Command(AirlinerCommand.FlapsUp); return true; }
        if (e.IsActionPressed(PlayerInput.Speedbrake)) { airliner.Command(AirlinerCommand.Speedbrake); return true; }
        if (e.IsActionPressed(PlayerInput.ParkingBrake)) { airliner.Command(AirlinerCommand.ParkingBrake); return true; }
        if (e.IsActionPressed(PlayerInput.LightsToggle)) { airliner.Command(AirlinerCommand.Lights); return true; }
        if (e.IsActionPressed(PlayerInput.Autopilot))
        {
            if (Airliner.Handling != AirlinerHandling.Sim) Announced?.Invoke("Autopilot: Light sim only (Settings)", false);
            else airliner.Command(AirlinerCommand.Autopilot);
            return true;
        }
        // G: in the air the gear lever; on the ground (where the gear is locked down) the doors
        // passengers use, stopped: the A320's left-hand doors (#416), the freighter's ramp and crew door (#420)
        if (e.IsActionPressed(PlayerInput.CarDoor))
        {
            if (!(airliner.State.OnGround && airliner.State.GearDown)) airliner.Command(AirlinerCommand.Gear);
            else if (!airliner.MayOpenDoors && airliner.DoorsOpen == 0) Announced?.Invoke("Stop to open the " + airliner.GDoorsName, false);
            else
            {
                int doors = airliner.GDoors;
                bool open = (airliner.DoorsOpen & doors) == 0;
                for (int d = 0; d < airliner.DoorCount; d++)
                    if ((doors >> d & 1) != 0 && ((airliner.DoorsOpen >> d & 1) != 0) != open) airliner.ToggleDoor(d);
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Z at an airliner's controls (#415): in Light sim it runs the start (battery, APU, one engine after
    /// the other) or shuts them down, the switches the aircraft works through; in Arcade it is the
    /// engine switch every vehicle has. False when this is not one.
    /// </summary>
    private bool AirlinerEngines()
    {
        if (_ride is Airliner { DriveAvailable: true } shuttle) return ShuttleDrive(shuttle);
        if (_ride is not Airliner airliner || Airliner.Handling != AirlinerHandling.Sim) return false;
        airliner.Command(AirlinerCommand.Engines);
        bool starting = !airliner.State.Starting;
        Announced?.Invoke(starting ? "Engine start: battery, APU, then each engine" : "Engines shutting down", true);
        // the vehicle's switch stays on: the aircraft's own state says what runs (its spool, for every peer)
        EngineOn = true;
        return true;
    }

    /// <summary>
    /// Z / D-pad ↑ at the shuttle's controls above 60 km (#1492): the cruise drive on or off (off leaves it on a
    /// circular orbit). Below it the switch is the engines', as every airliner's. In VR the guarded switch on the
    /// panel, gripped (<c>XrCabControls</c>), ends here too.
    /// </summary>
    public bool ShuttleDrive(Airliner shuttle)
    {
        if (!shuttle.DriveAvailable) return false;
        shuttle.Command(AirlinerCommand.Drive);
        Announced?.Invoke(shuttle.DriveOn ? "Cruise drive off: a circular orbit" : "Cruise drive on: the levers set its speed", true);
        EngineOn = true;
        return true;
    }

    // ---- another player's shuttle, heard (#1492) ----

    private AudioStreamPlayer3D? _remoteRockets, _remoteDrive, _remotePlasma;

    /// <summary>
    /// Another player's shuttle heard where it is: its rockets by the spool, its drive's hum, re-entry's roar
    /// by the heat, from the look its pose carries. Null: none of that here (the players are freed).
    /// </summary>
    private void RemoteShuttleAudio(Avatar.AirlinerLook? look)
    {
        if (look is not { } l)
        {
            _remoteRockets?.QueueFree(); _remoteDrive?.QueueFree(); _remotePlasma?.QueueFree();
            _remoteRockets = _remoteDrive = _remotePlasma = null;
            return;
        }
        _remoteRockets ??= SpatialLoop(Audio.ShuttleSounds.Rockets, "RemoteRockets", 30f, 8000f);
        _remoteDrive ??= SpatialLoop(Audio.ShuttleSounds.Drive, "RemoteDrive", 10f, 1500f);
        _remotePlasma ??= SpatialLoop(Audio.ShuttleSounds.Plasma, "RemotePlasma", 20f, 4000f);
        SpatialLevel(_remoteRockets, l.Drive ? 0f : l.Spool, 0.8f + 0.25f * l.Spool);
        SpatialLevel(_remoteDrive, l.Drive ? 0.6f : 0f, 0.8f + 0.6f * l.Lever);
        SpatialLevel(_remotePlasma, l.Heat, 0.7f + 0.5f * l.Heat);
    }

    private AudioStreamPlayer3D SpatialLoop(AudioStream stream, string name, float unit, float range)
    {
        var p = new AudioStreamPlayer3D { Name = name, Stream = stream, UnitSize = unit, MaxDistance = range, VolumeDb = -80f, Bus = Audio.SfxBus.Name };
        AddChild(p);
        return p;
    }

    /// <summary>Eases a loop toward a linear level and a pitch; silent, it stops.</summary>
    private static void SpatialLevel(AudioStreamPlayer3D p, float level, float pitch)
    {
        float eased = Mathf.Lerp(Mathf.DbToLinear(p.VolumeDb), level, 0.12f);
        p.VolumeDb = eased < 0.001f ? -80f : Mathf.LinearToDb(eased);
        p.PitchScale = Mathf.Clamp(pitch, 0.3f, 3f);
        if (eased >= 0.001f && !p.Playing) p.Play();
        else if (eased < 0.001f && p.Playing) p.Stop();
    }
}
