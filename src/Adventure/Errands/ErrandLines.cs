namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/errands.md).

/// <summary>What a giver says (#1267 part 3): a Swiss greeting by language, a line by reputation level, and a word of its trade. English text.</summary>
public static class ErrandLines
{
    /// <summary>The giver's greeting line for a player: by the reputation level in the giver's canton (0 a stranger, 3 and up an old friend).</summary>
    public static string Greeting(Giver giver, int level, string player)
    {
        string hello = GiverNames.Greeting(giver.Canton, giver.Id);
        string name = string.IsNullOrWhiteSpace(player) ? "friend" : player;
        string open = level switch
        {
            <= 0 => $"{hello}! You're not from here?",
            1 => $"{hello}, {name}. Back again?",
            2 => $"{hello}, {name}! Good to see you.",
            _ => $"Ah, {name}! Again for us?",
        };
        return $"{open} {Trade(giver.Kind)}";
    }

    public static string Trade(GiverKind kind) => kind switch
    {
        GiverKind.HutWarden => "The hut never stands still.",
        GiverKind.StationMaster => "The timetable waits for no one.",
        GiverKind.ShipMaster => "The lake is calm today.",
        GiverKind.Farmer => "The animals won't wait.",
        GiverKind.Senn => "The cheese is made when the cheese is made.",
        GiverKind.Dairy => "Fresh from the vat.",
        GiverKind.Postmaster => "Every letter has somewhere to be.",
        _ => "Sit down, there is soup.",
    };

    /// <summary>The one-line reward of a card: "CHF 55, +3 VS" (and "+1 BE" for a destination in another canton).</summary>
    public static string Reward(Offer offer)
    {
        string text = $"CHF {offer.Chf}, +{Postkarte.RepHome} {offer.Giver.Canton}";
        if (offer.DestCanton.Length == 2 && offer.DestCanton != offer.Giver.Canton) text += $", +{Postkarte.RepAway} {offer.DestCanton}";
        return text;
    }

    /// <summary>Why a card cannot be taken, in the dialogue's words.</summary>
    public static string StateText(ErrandCardState state) => state switch
    {
        ErrandCardState.Taken => "Taken today",
        ErrandCardState.Running => "Running",
        ErrandCardState.Done => "Done",
        _ => "",
    };
}

/// <summary>Where a card stands for a player (pure twin of <c>ErrandService.CardState</c>, for the lines).</summary>
public enum ErrandCardState { Open, Taken, Running, Done }
