using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

public partial class FootPlayer
{
    /// <summary>
    /// The local body's alcohol effects (#1271), set by the <see cref="Adventure.Alcohol.AlcoholService"/> on the player it owns
    /// only: lag and drift on the intents, no free climbing, stumbles, passing out. Null: sober (everyone else's copy).
    /// </summary>
    public Adventure.Alcohol.AlcoholBody? Drunk { get; set; }

    /// <summary>Passed out for the drink (<see cref="Down"/> = 1, replicated like a knock-out, without its costs).</summary>
    public bool PassedOut => Drunk is { State.Out: true };

    /// <summary>A stumble: a short loss of control, flat on the ground like a stun (the body pose is replicated, others see it).</summary>
    public void Fall(float seconds)
    {
        if (KnockedOut || Downed || _creative) return;
        _stunTimer = Mathf.Max(_stunTimer, seconds);
    }

    /// <summary>Passes out (<paramref name="on"/>) or wakes. Out cold the body lies still, <see cref="Down"/> = 1; waking it stands up after a beat.</summary>
    public void PassOut(bool on)
    {
        if (KnockedOut || Downed) return;
        if (on)
        {
            Down = 1;
            _stunTimer = float.PositiveInfinity;
        }
        else if (Down == 1)
        {
            Down = 0;
            _stunTimer = 0.6f;
        }
    }
}
