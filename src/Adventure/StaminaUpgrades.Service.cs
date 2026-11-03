using Godot;

namespace UnitSport.Adventure;

public static partial class StaminaUpgrades
{
    /// <summary>
    /// Client: this player's steps as the server last sent them (offline: its own progress), null until
    /// the section arrives. The local <c>FootPlayer</c> follows it (<c>TickStamina</c>).
    /// </summary>
    public static int? MySteps { get; private set; }

    /// <summary>
    /// Wires the plan into the service (on the server, offline and on every client): the reward kind on
    /// the authority, <c>Provides("stamina")</c> everywhere (quests with stamina steps are offered), and
    /// the owner's steps from each <c>SectionSync</c> of the section.
    /// </summary>
    public static void Attach(AdventureService service)
    {
        MySteps = null;
        service.Provides(Kind);
        if (service.Authority)
            service.RegisterReward(Kind, (peer, progress, arg) =>
                Grant(peer, progress, arg, service.Active, service.Events) is { } why ? why
                    : Log(progress, $"{Kind} {arg}: {progress.Get(Section).Steps} steps"));
        service.SectionChanged += id =>
        {
            if (id == Kind) MySteps = service.Mine.Get(Section).Steps;
        };
        // offline the progress is this very one: read it now (a networked client waits for the sync)
        if (service.Authority && !service.Networked) MySteps = service.Mine.Get(Section).Steps;
    }

    private static string? Log(PlayerProgress p, string what)
    {
        GD.Print($"[stamina] {(p.Key.Length > 13 ? p.Key[..13] : p.Key)}: {what}");
        return null;
    }
}
