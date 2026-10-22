// UnitSport's Nakama runtime module (#1121, docs/notes/net/nakama.md). Plain JavaScript run by Nakama's own JS
// runtime, so the server needs no Node or build step. Nakama looks registered functions up by name, so every one
// of them is a top-level function declaration (never an arrow or a function expression).

var MODULE_VERSION = '1';
var COURSE_ID = /^[a-z0-9_-]{1,64}$/;   // a course id as the race server names it: course:<id> is its leaderboard
var PLAYER_KEY = /^[A-Za-z0-9_-]{6,128}$/; // a PlayerKey fingerprint (base64url), Nakama's custom-id length limits
var MAX_TIME_MS = 24 * 3600 * 1000;
var MAX_WATTS = 3000;

var Code = { INVALID_ARGUMENT: 3, PERMISSION_DENIED: 7 }; // gRPC codes; Nakama maps them to HTTP 400 and 403

function InitModule(ctx, logger, nk, initializer) {
  initializer.registerRpc('healthcheck', rpcHealthcheck);
  initializer.registerRpc('submit_result', rpcSubmitResult);
  logger.info('UnitSport module %s loaded', MODULE_VERSION);
}

function rpcHealthcheck(ctx, logger, nk, payload) {
  return JSON.stringify({ ok: true, module: MODULE_VERSION });
}

// A finished race, posted by the race server with the runtime http key (server to server). A player's session never
// may: the race server is the one that checked the checkpoints and timed the finish. Body:
// { course, playerKey, name?, timeMs, avgWatts?, event? } -> { leaderboard, rank, best }
function rpcSubmitResult(ctx, logger, nk, payload) {
  if (ctx.userId) throw { message: 'results come from race servers only', code: Code.PERMISSION_DENIED };
  var r;
  try { r = JSON.parse(payload || '{}'); } catch (e) { throw { message: 'body is not JSON', code: Code.INVALID_ARGUMENT }; }
  if (typeof r.course !== 'string' || !COURSE_ID.test(r.course)) bad('course: [a-z0-9_-]{1,64}');
  if (typeof r.playerKey !== 'string' || !PLAYER_KEY.test(r.playerKey)) bad('playerKey: [A-Za-z0-9_-]{6,128}');
  if (!isInt(r.timeMs) || r.timeMs <= 0 || r.timeMs > MAX_TIME_MS) bad('timeMs: 1..' + MAX_TIME_MS);
  if (r.avgWatts !== undefined && (!isInt(r.avgWatts) || r.avgWatts < 0 || r.avgWatts > MAX_WATTS)) bad('avgWatts: 0..' + MAX_WATTS);
  if (r.event !== undefined && (typeof r.event !== 'string' || !COURSE_ID.test(r.event))) bad('event: [a-z0-9_-]{1,64}');
  var name = typeof r.name === 'string' ? r.name.substring(0, 32) : '';

  // The rider's account: their PlayerKey fingerprint is the custom id, created on their first result
  var account = nk.authenticateCustom(r.playerKey, undefined, true);
  var board = 'course:' + r.course;
  // authoritative: only the server writes; ascending + best: a rider's fastest time stands. Existing boards are kept.
  nk.leaderboardCreate(board, true, 'ascending', 'best', null, { course: r.course }, true);
  var rec = nk.leaderboardRecordWrite(board, account.userId, name || undefined, r.timeMs, 0,
    { avgWatts: r.avgWatts === undefined ? null : r.avgWatts, event: r.event || null });
  logger.info('result %s %s %d ms', board, account.userId, r.timeMs);
  return JSON.stringify({ leaderboard: board, rank: rec.rank, best: rec.score });
}

function bad(what) { throw { message: 'invalid ' + what, code: Code.INVALID_ARGUMENT }; }
function isInt(v) { return typeof v === 'number' && Math.floor(v) === v; }
