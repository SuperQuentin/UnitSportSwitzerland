using Godot;
using UnitSport.Core;

namespace UnitSport.Net.Accounts;

/// <summary>
/// <c>--accountprobe admit|admit:&lt;name&gt;|refuse</c> with <c>--connect</c> (driven by <c>tools/accountnetcheck.sh</c>,
/// #1123): waits for the server's account step and prints <c>[accountprobe] RESULT: ok</c> when it went as expected
/// (admitted, as that account name when one is given, or refused), then quits.
/// </summary>
public partial class AccountProbe : Node
{
    public static readonly string? Expect = CmdArgs.Value("--accountprobe");

    private const double TimeoutSeconds = 120;
    private readonly AccountGate _gate;
    private double _waited;
    private bool _done;

    public AccountProbe(AccountGate gate)
    {
        _gate = gate;
        Name = "AccountProbe";
    }

    public AccountProbe() : this(null!) { }

    public override void _Ready()
    {
        _gate.Welcomed += name =>
        {
            string want = Expect ?? "";
            if (want == "refuse") Done(false, $"admitted{(name.Length > 0 ? " as " + name : "")}, expected a refusal");
            else if (want.StartsWith("admit:") && want[6..] != name) Done(false, $"admitted as '{name}', expected '{want[6..]}'");
            else Done(true, $"admitted{(name.Length > 0 ? " as " + name : " offline")}");
        };
        _gate.Refused += reason => Done(Expect == "refuse", $"refused: {reason}");
    }

    public override void _Process(double delta)
    {
        if (_done || (_waited += delta) < TimeoutSeconds) return;
        Done(false, $"no answer from the account step in {TimeoutSeconds:0} s");
    }

    private void Done(bool ok, string what)
    {
        if (_done) return;
        _done = true;
        GD.Print($"[accountprobe] {what}");
        GD.Print(ok ? "[accountprobe] RESULT: ok" : $"[accountprobe] RESULT: FAILED {what}");
        // the tree, not this node: a refused world is torn down before the timer runs
        var tree = GetTree();
        tree.CreateTimer(0.5).Timeout += () => tree.Quit(ok ? 0 : 1);
    }
}
