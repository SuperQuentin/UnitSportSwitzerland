using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Movie;

/// <summary>
/// Where a movie plays (#638): one puppet <see cref="FootPlayer"/> per lane, posed every frame from
/// the clip that lane plays at the clock's time. The puppets take the remote path every peer
/// already draws other players with, so a plane's rotors, a car's doors or a rider's crank move as
/// they did, forwards, backwards or held still.
/// </summary>
public partial class MovieStage : Node3D
{
    /// <summary>Puppet names and authorities: far above any peer id, so none is ever mistaken for a player.</summary>
    public const int PuppetBase = 2_000_000_000;

    public MovieProject Project { get; private set; }
    public double Time { get; private set; }
    /// <summary>Seconds of movie per second; negative plays backwards.</summary>
    public double Speed { get; set; } = 1;
    public bool Playing { get; set; }
    public double Duration => Project.Duration;

    private readonly WorldOrigin _origin;
    private readonly List<FootPlayer> _puppets = new();
    private readonly List<Clip?> _active = new();
    private readonly ActorState _state = new(ActorIo.Names.Length);
    private double _stamp;

    public MovieStage(MovieProject project, WorldOrigin origin)
    {
        Name = "MovieStage";
        Project = project;
        _origin = origin;
        // before the puppets (children, priority 0): they draw this frame what was written this frame
        ProcessPriority = -1;
    }

    /// <summary>Another project (loaded, or a new one): every puppet goes, the next frame makes the new ones.</summary>
    public void Use(MovieProject project)
    {
        Project = project;
        foreach (var p in _puppets)
        {
            // out of the tree now: the new puppets take the same names, which a passenger finds its host by
            RemoveChild(p);
            p.QueueFree();
        }
        _puppets.Clear();
        Seek(0);
    }

    public void Seek(double t)
    {
        Time = Math.Clamp(t, 0, Duration);
        Apply();
    }

    public void TogglePlay(double speed)
    {
        if (Playing && Math.Sign(Speed) == Math.Sign(speed)) { Playing = false; return; }
        Speed = speed;
        // played to an end: from the other end again
        if (speed > 0 && Time >= Duration - 1e-3) Time = 0;
        if (speed < 0 && Time <= 1e-3) Time = Duration;
        Playing = Duration > 0;
    }

    /// <summary>The puppet of lane <paramref name="lane"/>, when it is on screen now.</summary>
    public FootPlayer? Puppet(int lane) =>
        lane >= 0 && lane < _puppets.Count && lane < _active.Count && _active[lane] != null ? _puppets[lane] : null;

    public override void _Process(double delta)
    {
        if (Playing)
        {
            Time += delta * Speed;
            if (Time >= Duration) { Time = Duration; Playing = false; }
            else if (Time <= 0) { Time = 0; Playing = false; }
        }
        Apply();
    }

    /// <summary>Every lane's puppet as it was at <see cref="Time"/>.</summary>
    public void Apply()
    {
        while (_puppets.Count < Project.Lanes.Count) AddPuppet(_puppets.Count);
        _active.Clear();
        for (int lane = 0; lane < Project.Lanes.Count; lane++) _active.Add(Project.ActiveClip(lane, Time));

        for (int lane = 0; lane < _puppets.Count; lane++)
        {
            var puppet = _puppets[lane];
            var clip = lane < _active.Count ? _active[lane] : null;
            bool shown = clip != null;
            if (puppet.Visible != shown || (puppet.ProcessMode == ProcessModeEnum.Disabled) == shown)
            {
                // off the timeline here: not drawn, not animated (a remote copy sets its own Visible each frame)
                puppet.ProcessMode = shown ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
                puppet.Visible = shown;
            }
            if (clip == null) continue;
            Project.Tracks[clip.Track].Sample(clip.Local(Time), _state);
            ActorIo.Write(_state, puppet, HostPuppet(_state.Num[ActorIo.RidingWith]), ++_stamp);
        }
    }

    /// <summary>The puppet a passenger rides with: the lane of the player its recording names, if on screen.</summary>
    private int HostPuppet(long peer)
    {
        if (peer == 0) return 0;
        for (int lane = 0; lane < Project.Lanes.Count; lane++)
            if (Project.Lanes[lane].PeerId == peer && lane < _active.Count && _active[lane] != null) return PuppetBase + lane;
        return 0;
    }

    private void AddPuppet(int lane)
    {
        var puppet = new FootPlayer { Name = (PuppetBase + lane).ToString(), Origin = _origin, Puppet = true };
        puppet.SetMultiplayerAuthority(PuppetBase + lane);   // not us: it takes the remote path
        AddChild(puppet);
        puppet.Visible = false;
        puppet.ProcessMode = ProcessModeEnum.Disabled;
        _puppets.Add(puppet);
    }
}
