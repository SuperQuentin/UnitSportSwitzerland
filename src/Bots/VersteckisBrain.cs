using Godot;
using UnitSport.Adventure.Versteckis;

namespace UnitSport.Bots;

/// <summary>
/// A bot playing Versteckis (#1268, plan "Bots"), in both roles, through the same server paths a player's RPCs call
/// (<c>ServeDisguise</c>, <c>ServeTag</c>, <c>ServeSniff</c>, <c>ServeDecoy</c>, <c>ServeSprint</c>, <c>ServeMoo</c>,
/// <c>ServeObjective</c>), with its choices in <see cref="VsTactics"/>.
/// <list type="bullet">
/// <item>A hider becomes the kind the village has most of, walks to a spot beside a prop of that kind away from the
/// seeker's door and the other hiders, and holds still; it flees with a sprint when the seeker comes close facing it,
/// moos when the seeker is far and looking away, leaves its decoy once, and goes for an objective when the seeker is far
/// from it (one bot at a time).</item>
/// <item>A seeker uses honest senses only: the props in its cone within 40 m (the decor every peer draws, and the bodies
/// drawn as props), the sounds a player hears, its own sniffs' puffs. It suspects what moved, what stands where its kind
/// never does, and what made a sound, tags the most suspicious within reach, sniffs when nothing is, and sweeps the
/// circle in rings. Its base suspicion of everything makes real misses.</item>
/// </list>
/// Docs: <c>docs/notes/bots/versteckis.md</c>.
/// </summary>
public sealed class VersteckisBrain : IBotBrain
{
    private readonly VersteckisManager _vs;
    private readonly int _game;
    private readonly Random _rng;
    private int _round = -1;
    private VsSuspicion? _suspicion;
    private readonly HashSet<string> _caught = new();
    private readonly List<(double E, double N)> _heard = new(), _puffs = new();
    private Vector3? _spot;
    private float _spotYaw;
    private VsProp _prop;
    private double _fleeUntil, _decoyAt, _nextMooRoll, _lastSniff = -100, _objectiveFrom = -1;
    private int _objective = -1, _objectiveStep;
    private int _sweep;
    private string _status = "";

    public VersteckisBrain(VersteckisManager vs, int game, int seed)
    {
        _vs = vs;
        _game = game;
        _rng = new Random(seed);
    }

    public string Status => _status;

    /// <summary>The server: a new round started (a fresh memory, a new spot).</summary>
    public void NewRound(int round)
    {
        _round = round;
        _suspicion = null;
        _caught.Clear();
        _heard.Clear();
        _puffs.Clear();
        _spot = null;
        _objective = -1;
        _sweep = _rng.Next(32);
        _decoyAt = 0;
    }

    /// <summary>The server: this seeker caught <paramref name="hider"/>.</summary>
    public void Caught(long hider)
    {
        _caught.Add("b" + hider);
        _suspicion?.Forget("b" + hider);
    }

    public void Heard(double e, double n) => _heard.Add((e, n));
    public void Puff(double e, double n) => _puffs.Add((e, n));

    public double Think(Bot bot, double now)
    {
        bot.Intent = BotIntent.Stand;
        if (_vs.StateOf(_game) is not { } s || s.Find(bot.Id) is not { Left: false } me || bot.Body.Eliminated) return 0.5;
        if (s.Round != _round) NewRound(s.Round);
        return s.Phase switch
        {
            VsPhase.Hide when me.Role == VsRole.Hider && !me.Caught => Hide(bot, s, me, now),
            VsPhase.Seek when me.Role == VsRole.Hider && !me.Caught => HiderSeek(bot, s, me, now),
            VsPhase.Seek when me.Role == VsRole.Seeker => Seek(bot, s, me, now),
            _ => Idle(s, me),
        };
    }

    private double Idle(VsState s, VsEntrant me)
    {
        _status = $"{s.Phase} {me.Role}";
        return 0.5;
    }

    private (double E, double N) Lv95(Vector3 world) => _vs.Origin.ToLv95(world);

    private Vector3 World(Bot bot, double e, double n) => _vs.Origin.ToWorld(e, n, 0) with { Y = bot.Body.GlobalPosition.Y };

    // ---- hiding ----------------------------------------------------------------------------------

    /// <summary>Hide: become a prop, walk to a spot beside one of its kind, settle.</summary>
    private double Hide(Bot bot, VsState s, VsEntrant me, double now)
    {
        var decor = _vs.DecorOf(_game);
        if (_prop == VsProp.None || !s.Kinds.Contains(_prop))
        {
            // the kind the village has most of blends best; now and then another
            var counts = decor.GroupBy(d => d.Kind).Where(gk => gk.Key != VsProp.Cow).OrderByDescending(gk => gk.Count()).Select(gk => gk.Key).ToList();
            _prop = counts.Count == 0 ? s.Kinds.FirstOrDefault() : counts[_rng.NextDouble() < 0.7 ? 0 : _rng.Next(counts.Count)];
        }
        if (me.Prop != _prop) _vs.ServeDisguise(bot.Id, (int)_prop);
        if (_spot == null) PickSpot(bot, s, decor);
        return GoToSpot(bot, now, s);
    }

    private void PickSpot(Bot bot, VsState s, IReadOnlyList<VsDecor> decor)
    {
        var others = new List<(double E, double N)>();
        foreach (var e in s.Entrants)
            if (e.Role == VsRole.Hider && e.Peer != bot.Id && _vs.PositionOf(e.Peer) is { } p) others.Add((p.E, p.N));
        var space = bot.World.Space;
        var door = _vs.Origin.ToWorld(s.DoorE, s.DoorN, 0) with { Y = bot.Body.GlobalPosition.Y + 1.6f };
        Func<double, double, bool> hidden = (e, n) =>
        {
            var at = _vs.Origin.ToWorld(e, n, 0) with { Y = door.Y };
            return !BotRays.Clear(space, door, at, bot.Body.GetRid());
        };
        var spot = VsTactics.HiderSpot(decor, _prop, s.DoorE, s.DoorN, others, hidden, (int)bot.Profile.Difficulty, _rng, s.E, s.N, s.Radius);
        if (spot is { } sp)
        {
            _spot = World(bot, sp.E, sp.N);
            _spotYaw = sp.Yaw;
        }
        else
        {
            // no prop of its kind fits: anywhere inside, away from the door
            double a = _rng.NextDouble() * Math.Tau, r = s.Radius * (0.4 + 0.4 * _rng.NextDouble());
            _spot = World(bot, s.E + Math.Cos(a) * r, s.N + Math.Sin(a) * r);
            _spotYaw = (float)(_rng.NextDouble() * Math.Tau);
        }
    }

    private double GoToSpot(Bot bot, double now, VsState s)
    {
        if (_spot is not { } spot) return 0.5;
        if (!BotSkills.MoveTo(bot, spot, false, 0.8f, bot.World.Space, now))
        {
            _status = $"to its spot {bot.Body.GlobalPosition.DistanceTo(spot):F0} m";
            return 0.25;
        }
        // still and settled, facing the way the props round it face
        bot.Intent.FaceYaw = _spotYaw;
        bot.Intent.Crouch = true;
        _status = $"hidden as {VsProps.A(_prop)}";
        return 0.4;
    }

    /// <summary>Seek, hiding: hold still; flee, moo, decoy and go for an objective by the seeker's moves.</summary>
    private double HiderSeek(Bot bot, VsState s, VsEntrant me, double now)
    {
        var (be, bn) = Lv95(bot.Body.GlobalPosition);
        // the seeker as the bot perceives it: seen in its cone, or its last known place (its name is on everyone's HUD)
        bot.Senses.Look(bot, bot.World.Bodies(), bot.World.Space, now);
        double dist = 999;
        bool facing = false;
        (double E, double N)? seeker = null;
        foreach (var seen in bot.Senses.Seen)
            if (seen.Id == s.Seeker)
            {
                var (se, sn) = Lv95(seen.At);
                seeker = (se, sn);
                dist = Math.Sqrt((se - be) * (se - be) + (sn - bn) * (sn - bn));
                float yaw = seen.Body.NetYaw;
                facing = VsTactics.Facing(se, sn, -Math.Sin(yaw), Math.Cos(yaw), be, bn);
            }
        if (seeker == null && bot.Senses.Memory.TryGetValue(s.Seeker, out var mem) && now - mem.When < 20)
        {
            var (se, sn) = Lv95(mem.At);
            seeker = (se, sn);
            dist = Math.Sqrt((se - be) * (se - be) + (sn - bn) * (sn - bn));
        }

        // fleeing: a sprint, then away from the seeker, then a new spot
        if (now < _fleeUntil && seeker is { } from)
        {
            var away = new Vector3((float)(be - from.E), 0, (float)-(bn - from.N));
            if (away.LengthSquared() < 0.01f) away = Vector3.Forward;
            BotSkills.MoveTo(bot, bot.Body.GlobalPosition + away.Normalized() * 20f, false, 1f, bot.World.Space, now);
            _status = "fleeing";
            return 0.25;
        }
        if (VsTactics.Flee(dist, facing))
        {
            _vs.ServeSprint(bot.Id);
            _fleeUntil = now + 4;
            _spot = null;
            if (_objective >= 0) { _vs.ServeObjective(bot.Id, _objective, 2); _objective = -1; _vs.FreeObjective(_game, bot.Id); }
            return 0.1;
        }
        if (_spot == null) PickSpot(bot, s, _vs.DecorOf(_game));

        // an objective, one bot at a time, when the seeker is far from it
        if (_objective < 0 && _objectiveFrom < 0 && bot.Profile.Boldness > 0.2f && _rng.NextDouble() < 0.15)
            for (int i = 0; i < s.Objectives.Count; i++)
            {
                var o = s.Objectives[i];
                double fromSeeker = seeker is { } sk ? Math.Sqrt((o.E - sk.E) * (o.E - sk.E) + (o.N - sk.N) * (o.N - sk.N)) : 999;
                if (VsTactics.GoForObjective(fromSeeker, o.Done, o.Carrier != 0) && _vs.ClaimObjective(_game, bot.Id))
                {
                    _objective = i;
                    _objectiveStep = 0;
                    break;
                }
            }
        if (_objective >= 0) return Objective(bot, s, me, now);

        // a moo now and then when the seeker is far and looking away
        if (now >= _nextMooRoll)
        {
            _nextMooRoll = now + 3;
            if (VsTactics.Moo(dist, facing, bot.Profile.Boldness, _rng.NextDouble())) _vs.ServeMoo(bot.Id);
        }
        // the decoy, once, at a moment of its own; then on to another spot
        if (_decoyAt <= 0) _decoyAt = now + 15 + _rng.NextDouble() * 40;
        if (!me.DecoyUsed && now >= _decoyAt && dist > 25)
        {
            _vs.ServeDecoy(bot.Id);
            _spot = null;
            return 0.2;
        }
        return GoToSpot(bot, now, s);
    }

    /// <summary>The objective run: to the site (the can's farm door, then the bench), the hold, back to hiding.</summary>
    private double Objective(Bot bot, VsState s, VsEntrant me, double now)
    {
        var o = s.Objectives[_objective];
        if (o.Done || o.Carrier != 0 && o.Carrier != bot.Id)
        {
            _objective = -1;
            _objectiveFrom = -1;
            _vs.FreeObjective(_game, bot.Id);
            _spot = null;
            return 0.2;
        }
        bool can = o.Site == VsSite.Can;
        bool carrying = can && o.Carrier == bot.Id;
        double te = can && !carrying ? o.FromE : o.E, tn = can && !carrying ? o.FromN : o.N;
        var target = World(bot, te, tn);
        if (!BotSkills.MoveTo(bot, target, false, VsRules.SiteReach - 0.6f, bot.World.Space, now))
        {
            _status = $"to the {o.Site}";
            return 0.25;
        }
        if (can)
        {
            _vs.ServeObjective(bot.Id, _objective, carrying ? 1 : 0);
            if (carrying) { _objective = -1; _vs.FreeObjective(_game, bot.Id); _spot = null; }
            return 0.3;
        }
        if (_objectiveFrom < 0)
        {
            _vs.ServeObjective(bot.Id, _objective, 0);
            _objectiveFrom = now;
            _status = $"holding the {o.Site}";
            return 0.25;
        }
        if (now - _objectiveFrom >= VsRules.HoldSeconds(o.Site) + 0.3)
        {
            _vs.ServeObjective(bot.Id, _objective, 1);
            _objective = -1;
            _objectiveFrom = -1;
            _vs.FreeObjective(_game, bot.Id);
            _spot = null;
        }
        return 0.25;
    }

    // ---- seeking ---------------------------------------------------------------------------------

    private const float SeekSight = 40f;

    private double Seek(Bot bot, VsState s, VsEntrant me, double now)
    {
        _suspicion ??= new VsSuspicion((int)bot.Profile.Difficulty);
        var sus = _suspicion;
        var body = bot.Body;
        var (be, bn) = Lv95(body.GlobalPosition);
        float facingYaw = bot.Intent.FaceYaw ?? body.Rotation.Y;
        double fe = -Math.Sin(facingYaw), fn = Math.Cos(facingYaw);
        // sight: the decor in its cone within 40 m, and the bodies it sees drawn (as props, or as people)
        var decor = _vs.DecorOf(_game);
        for (int i = 0; i < decor.Count; i++)
        {
            var d = decor[i];
            double de = d.E - be, dn = d.N - bn, dd = de * de + dn * dn;
            if (dd > SeekSight * SeekSight || !VsTactics.Facing(be, bn, fe, fn, d.E, d.N) && dd > 36) continue;
            sus.Look("d" + i, d.Kind, d.E, d.N, now, inPlace: true);
        }
        bot.Senses.Look(bot, bot.World.Bodies(), bot.World.Space, now);
        foreach (var seen in bot.Senses.Seen)
        {
            if (seen.Distance > SeekSight || seen.Id == bot.Id) continue;
            var (se, sn) = Lv95(seen.At);
            if (!VsRules.Inside(s.E, s.N, s.Radius, se, sn)) continue;
            string key = "b" + seen.Id;
            if (_caught.Contains(key)) continue;
            var looks = _vs.Looks(_game, seen.Id);
            // a person (not a prop) inside the line in Seek: someone at an objective, or a bystander
            if (looks == VsProp.None) { sus.Look(key, VsProp.None, se, sn, now, inPlace: false); continue; }
            sus.Look(key, looks, se, sn, now, inPlace: HidePlan.InPlace(decor, looks, se, sn, 6f));
        }
        foreach (var (e, n) in _heard) sus.Heard(e, n);
        _heard.Clear();
        foreach (var (e, n) in _puffs) sus.Puff(e, n);
        _puffs.Clear();

        // the most suspicious: tag it in reach, else walk to it
        float threshold = now - s.PhaseStarted > 90 ? 0.2f : 0.35f;
        if (sus.Best(be, bn, threshold) is { } best)
        {
            double d = Math.Sqrt((best.E - be) * (best.E - be) + (best.N - bn) * (best.N - bn));
            var at = World(bot, best.E, best.N);
            if (d <= VsRules.TagReach - 0.2)
            {
                bot.Intent.FaceYaw = Mathf.Atan2(-(float)(best.E - be), (float)(best.N - bn));
                _vs.ServeTag(bot.Id, best.E, best.N, (float)body.Global.Alt);
                if (!_caught.Contains(best.Key)) sus.Miss(best.Key);
                _status = $"tagged {best.Key}";
                return 0.5;
            }
            BotSkills.MoveTo(bot, at, true, VsRules.TagReach - 0.6f, bot.World.Space, now);
            _status = $"to {best.Key} ({best.Suspicion:F2}, {d:F0} m)";
            return 0.25;
        }
        // nothing stands out: a sniff, then the sweep
        if (me.Sniffs > 0 && now - _lastSniff > 20 && now - s.PhaseStarted > 5)
        {
            _lastSniff = now;
            _vs.ServeSniff(bot.Id);
            return 0.3;
        }
        // the sweep: rings at 0.3, 0.6 and 0.85 of the radius, eight points each
        int ring = _sweep / 8 % 3, k = _sweep % 8;
        double r = s.Radius * (0.3 + 0.28 * ring), a = Math.Tau * k / 8 + ring * 0.4;
        var waypoint = World(bot, s.E + Math.Cos(a) * r, s.N + Math.Sin(a) * r);
        if (BotSkills.MoveTo(bot, waypoint, true, 4f, bot.World.Space, now)) _sweep++;
        _status = $"sweeping ring {ring} point {k}";
        return 0.3;
    }
}
