using Godot;

namespace UnitSport.World;

/// <summary>One race as the Activities screen lists it (#840).</summary>
public sealed record RaceRow(int Id, string Host, string What, float Km, int Entrants, float SecondsLeft, bool Running, bool Mine, bool Joinable);

/// <summary>
/// The races open on the server, for the Activities screen's Race tab (#840): asked for while the tab
/// is up, answered to the asker alone. Joining and the rest still go through <c>/race</c>.
/// </summary>
public partial class RaceManager
{
    /// <summary>Client: the last list the server sent, newest race first.</summary>
    public IReadOnlyList<RaceRow> Rows { get; private set; } = Array.Empty<RaceRow>();

    /// <summary>Client: a new list came in.</summary>
    public event Action? RowsChanged;

    /// <summary>Client: asks the server for the list (the screen does, once a second while it shows it).</summary>
    public void RequestRows()
    {
        if (Net.NetLink.Online(this)) RpcId(1, MethodName.SubmitRowsRequest);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitRowsRequest()
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        var races = _races.Values.OrderByDescending(r => r.Id).ToList();
        int mine = _raceOf.GetValueOrDefault(sender);
        RpcId(sender, MethodName.ReceiveRows,
            races.Select(r => r.Id).ToArray(),
            races.Select(r => Who(r.Host)).ToArray(),
            races.Select(r => r.What).ToArray(),
            races.Select(r => r.Km).ToArray(),
            races.Select(r => r.Entrants.Count).ToArray(),
            races.Select(r => r.Phase == Phase.Entry ? (float)Math.Max(0, r.EntryEnds - _clock) : 0f).ToArray(),
            races.Select(r => (r.Phase == Phase.Running ? 1 : 0) | (r.Id == mine ? 2 : 0)
                | (r.Phase != Phase.Running && mine == 0 && (r.Invited == 0 || r.Invited == sender) && r.Entrants.Count < MaxEntrants ? 4 : 0)).ToArray());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveRows(int[] ids, string[] hosts, string[] whats, float[] kms, int[] entrants, float[] left, int[] flags)
    {
        var rows = new RaceRow[ids.Length];
        for (int i = 0; i < ids.Length; i++)
            rows[i] = new RaceRow(ids[i], hosts[i], whats[i], kms[i], entrants[i], left[i], (flags[i] & 1) != 0, (flags[i] & 2) != 0, (flags[i] & 4) != 0);
        Rows = rows;
        RowsChanged?.Invoke();
    }
}
