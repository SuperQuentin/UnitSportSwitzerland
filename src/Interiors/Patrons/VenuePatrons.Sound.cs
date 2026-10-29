using Godot;
using UnitSport.Audio;

namespace UnitSport.Interiors.Patrons;

/// <summary>
/// The patrons' sound (#1244, <see cref="VenueAmbience"/>): each room with guests murmurs (a terrace too), as loud as
/// it is full, with its bed under it (cutlery where they eat, the popcorn machine in a cinema's lobby, a table football
/// rattling while it is played); the café's grinder, the bar's tap and an arcade's jingles now and then; one-shots on
/// the nearest guests' own moves (the pool shot, a dart, a coin as an arcade player starts, a settee's cheer). Inside an
/// interior, so heard only there (3 km under the street); the street hears the venue's music at its door
/// (<see cref="VenueMusic.AtDoor"/>).
/// </summary>
public partial class VenuePatrons
{
    /// <summary>A room's murmur at one guest and how much louder each doubling makes it, dB.</summary>
    private const float MurmurOne = -26f, MurmurPerDoubling = 4f;
    /// <summary>How much quieter a cinema hall's audience is than a bar's, dB: they whisper through the film (#1365).</summary>
    private const float HallHush = 24f;
    /// <summary>How far a guest's one-shot carries, m; how many play at once.</summary>
    private const float ShotRange = 18f;
    private const int ShotVoices = 4;

    /// <summary>A room's (or a terrace's) loops.</summary>
    private sealed class Room
    {
        public AudioStreamPlayer3D Murmur = null!;
        public AudioStreamPlayer3D? Bed;
        public float Db = -80f, Target = -80f;
        public int Count;
        public Vector3 Sum;
        public bool Dining, Lobby, Hall, Cafe, Bar, Arcade;
        public Vector3? Counter, Tap, Cabinet;
    }

    private readonly Dictionary<int, Room> _rooms = new();
    private readonly AudioStreamPlayer3D[] _shots = new AudioStreamPlayer3D[ShotVoices];
    private int _nextShot;
    private readonly Random _soundRng = new(1244);
    /// <summary>Each table football's rattle, by its group's first spot.</summary>
    private readonly Dictionary<int, AudioStreamPlayer3D> _rattles = new();

    /// <summary>On each schedule tick: who is in each room, its loudness, the room's own now-and-then sounds.</summary>
    private void SoundTick(double now)
    {
        if (!_soundReady) SoundSetup();
        foreach (var r in _rooms.Values) (r.Count, r.Sum) = (0, Vector3.Zero);
        foreach (var p in _shown)
        {
            if (!_rooms.TryGetValue(RoomKey(p.Spot), out var r)) continue;
            r.Count++;
            r.Sum += At(p);
        }
        foreach (var (key, r) in _rooms)
        {
            r.Target = r.Count == 0 ? -80f : MurmurOne + MurmurPerDoubling * MathF.Log2(r.Count) - (r.Hall ? HallHush : 0f);
            // a terrace's murmur follows its guests; a room's stays in its middle
            if (key < 0 && r.Count > 0) r.Murmur.Position = r.Sum / r.Count + Vector3.Up;
            if (r.Count == 0) continue;
            // a room's now-and-then sounds: on windows of the shared clock, the same on every client
            long w = (long)Math.Floor(now / 35), was = (long)Math.Floor((now - Tick) / 35);
            if (w == was || PatronSchedule.Unit((uint)key * 7919u, (uint)w) > 0.6) continue;
            if (r.Cafe && r.Counter is { } c) Shot(VenueAmbience.Unit(_soundRng.Next(2) == 0 ? VenueAmbience.Grinder : VenueAmbience.Steamer), c, -10f);
            else if (r.Bar && r.Tap is { } tap) Shot(VenueAmbience.Unit(VenueAmbience.Tap), tap, -12f);
            else if (r.Arcade && r.Cabinet is { } cab) Shot(VenueAmbience.Jingles.Pick(_soundRng), cab, -14f);
        }
        foreach (var g in _groups)
        {
            if (g.Activity != PatronActivity.TableFootball) continue;
            bool on = _seated[g.First] != null || g.Count > 1 && _seated[g.First + 1] != null;
            if (!_rattles.TryGetValue(g.First, out var rattle))
            {
                if (!on) continue;
                rattle = Player("Rattle", VenueAmbience.Foosball, _spots[g.First].At, 2f);
                rattle.VolumeDb = -14f;
                _rattles[g.First] = rattle;
            }
            if (rattle.Playing != on)
            {
                if (on) rattle.Play();
                else rattle.Stop();
            }
        }
    }

    private bool _soundReady;

    /// <summary>The rooms with guest spots, each a murmur at its middle and its bed, and the one-shot voices.</summary>
    private void SoundSetup()
    {
        _soundReady = true;
        for (int i = 0; i < ShotVoices; i++)
        {
            _shots[i] = new AudioStreamPlayer3D { Name = $"Shot{i}", Bus = SfxBus.Name, UnitSize = 3f, MaxDistance = ShotRange };
            AddChild(_shots[i]);
        }
        for (int i = 0; i < _spots.Count; i++)
        {
            var s = _spots[i];
            int key = RoomKey(i);
            if (_rooms.ContainsKey(key)) continue;
            var room = new Room();
            Vector3 middle = s.At;
            if (_node != null && s.Room >= 0)
            {
                var l = _node.Layout;
                var piece = l.Furniture[s.Piece];
                var rp = l.Floors[piece.Floor].Rooms[s.Room];
                middle = new Vector3((rp.X0 + rp.X1) / 2, l.FloorY(piece.Floor) + 1.6f, (rp.Z0 + rp.Z1) / 2);
                var type = rp.Type;
                room.Lobby = type == RoomType.CinemaLobby;
                room.Hall = type == RoomType.Auditorium;
                room.Arcade = type == RoomType.ArcadeHall;
                room.Cafe = type == RoomType.CafeRoom;
                room.Bar = type is RoomType.BarRoom or RoomType.PubRoom or RoomType.ClubRoom or RoomType.Gaststube or RoomType.Concourse;
                foreach (var q in l.Furniture)
                {
                    if (q.Floor != piece.Floor || q.X < rp.X0 || q.X > rp.X1 || q.Z < rp.Z0 || q.Z > rp.Z1) continue;
                    var at = new Vector3(q.X, l.FloorY(q.Floor) + 1.1f, q.Z);
                    if (q.Type is FurnitureType.PastryCase or FurnitureType.ConcessionStand) room.Counter ??= at;
                    if (q.Type == FurnitureType.BarCounter) room.Tap ??= at;
                    if (q.Type == FurnitureType.ArcadeCabinet) room.Cabinet ??= at;
                }
            }
            room.Dining = s.Activity is PatronActivity.Dine or PatronActivity.Coffee or PatronActivity.Snack;
            float spread = 3f;
            if (_node != null && s.Room >= 0)
            {
                var rp = _node.Layout.Floors[_node.Layout.Furniture[s.Piece].Floor].Rooms[s.Room];
                spread = Math.Clamp(new Vector2(rp.X1 - rp.X0, rp.Z1 - rp.Z0).Length() / 2, 3f, 14f);
            }
            room.Murmur = Player("Murmur", VenueAmbience.Chatter, middle, spread);
            room.Murmur.VolumeDb = -80f;
            room.Murmur.Play(_soundRng.NextSingle() * 7f);
            var bed = room.Lobby && room.Counter is { } pop ? (VenueAmbience.Popcorn, pop) : room.Dining ? (VenueAmbience.Cutlery, middle) : default;
            if (bed.Item1 != null)
            {
                room.Bed = Player("Bed", bed.Item1, bed.Item2, spread);
                room.Bed.VolumeDb = -80f;
                room.Bed.Play(_soundRng.NextSingle() * 3f);
            }
            _rooms[key] = room;
        }
    }

    /// <summary>A spot's room across the plan's floors (-1 a terrace): a room's index is its floor's own.</summary>
    private int RoomKey(int spot)
    {
        var s = _spots[spot];
        if (_node == null || s.Room < 0) return -1;
        return _node.Layout.Furniture[s.Piece].Floor * 1000 + s.Room;
    }

    private AudioStreamPlayer3D Player(string name, AudioStream stream, Vector3 at, float unit)
    {
        var p = new AudioStreamPlayer3D
        {
            Name = name, Stream = stream, Bus = SfxBus.Name, UnitSize = unit, MaxDistance = 60f, Position = at,
        };
        AddChild(p);
        return p;
    }

    /// <summary>Every frame: the rooms' loudness eased to where it is going.</summary>
    private void SoundFrame(double delta)
    {
        foreach (var r in _rooms.Values)
        {
            if (Math.Abs(r.Db - r.Target) < 0.05f) continue;
            r.Db = Mathf.MoveToward(r.Db, r.Target, (float)delta * 12f);
            r.Murmur.VolumeDb = r.Db;
            if (r.Bed != null) r.Bed.VolumeDb = r.Db - 4f;
        }
    }

    /// <summary>The near guests' moves that sound, since the last frame.</summary>
    private void SoundOf(Patron p, double now)
    {
        double since = p.SoundT;
        p.SoundT = now;
        if (since < 0 || p.Dist > ShotRange * ShotRange) return;
        var spot = _spots[p.Spot];
        switch (PatronPoser.EventIn(spot.Activity, spot.Place, p.Look, since, now))
        {
            case PatronPoser.Sound.PoolShot: Shot(VenueAmbience.Pool.Pick(_soundRng), spot.At + Vector3.Up, -6f); break;
            case PatronPoser.Sound.Dart: Shot(VenueAmbience.Darts.Pick(_soundRng), spot.At + new Vector3(0, 1.7f, 0), -8f); break;
        }
    }

    /// <summary>A settee's cheer, a coin as an arcade player starts: where the guest is.</summary>
    private void SoundAt(PatronActivity a, Vector3 at)
    {
        if (!_soundReady) return;
        if (a == PatronActivity.Arcade) Shot(VenueAmbience.Coins.Pick(_soundRng), at + Vector3.Up, -10f);
        else if (a == PatronActivity.Settee) Shot(VenueAmbience.Unit(VenueAmbience.Cheer), at + Vector3.Up, -8f);
    }

    private void Shot((AudioStreamWav Stream, float Pitch, float VolumeDb) pick, Vector3 at, float db)
    {
        var p = _shots[_nextShot];
        _nextShot = (_nextShot + 1) % ShotVoices;
        p.Stream = pick.Stream;
        p.PitchScale = pick.Pitch;
        p.VolumeDb = db + pick.VolumeDb;
        p.Position = at;
        p.Play();
    }
}
