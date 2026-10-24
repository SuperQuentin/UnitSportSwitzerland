// UnitSport's Nakama runtime module (#1121, docs/notes/net/nakama.md). Plain JavaScript run by Nakama's own JS
// runtime, so the server needs no Node or build step. Nakama looks registered functions up by name, so every one
// of them is a top-level function declaration (never an arrow or a function expression).

var MODULE_VERSION = '1';
var COURSE_ID = /^[a-z0-9_-]{1,64}$/;   // a course id as the race server names it: course:<id> is its leaderboard
var PLAYER_KEY = /^[A-Za-z0-9_-]{6,128}$/; // a PlayerKey fingerprint (base64url), Nakama's custom-id length limits
var MAX_TIME_MS = 24 * 3600 * 1000;
var MAX_WATTS = 3000;

var Code = { INVALID_ARGUMENT: 3, NOT_FOUND: 5, PERMISSION_DENIED: 7, UNAUTHENTICATED: 16 };
var ADMINS = 'admins';   // the Nakama group of the admins (#1207) // gRPC codes; Nakama maps them to HTTP 400 and 403

function InitModule(ctx, logger, nk, initializer) {
  initializer.registerRpc('healthcheck', rpcHealthcheck);
  initializer.registerRpc('submit_result', rpcSubmitResult);
  // the website's accounts and the admin dashboard (#1207, docs/plans/web-dashboard.md)
  initializer.registerRpc('me', rpcMe);
  initializer.registerRpc('admin_users', rpcAdminUsers);
  initializer.registerRpc('admin_user', rpcAdminUser);
  initializer.registerRpc('admin_set_admin', rpcAdminSetAdmin);
  initializer.registerRpc('admin_ban', rpcAdminBan);
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

// ---- the website's accounts and the admin dashboard (#1207) ---------------------------------------------------------
// An admin is a member of the group "admins". The first admins are bootstrapped by email: ADMIN_EMAILS (the runtime env,
// from NAKAMA_ADMIN_EMAILS in .env) lists them, and such an account joins the group on its first `me`. Every admin RPC
// checks the caller's membership itself: the website only hides the dashboard button.

function bootstrapEmails(ctx) {
  var raw = (ctx.env && ctx.env.ADMIN_EMAILS) || '';
  return raw.split(',').map(function (e) { return e.trim().toLowerCase(); }).filter(function (e) { return e.length > 0; });
}

function adminsGroup(nk) {
  var found = nk.groupsList(ADMINS, null, null, null, 1, null);
  var groups = (found && found.groups) || [];
  for (var i = 0; i < groups.length; i++) if (groups[i].name === ADMINS) return groups[i];
  return null;
}

function isAdmin(nk, userId) {
  var group = adminsGroup(nk);
  if (!group) return false;
  var mine = nk.userGroupsList(userId, 100, null, null);
  var list = (mine && mine.userGroups) || [];
  // states 0 (superadmin), 1 (admin) and 2 (member) are in the group; 3 is a join request
  for (var i = 0; i < list.length; i++) if (list[i].group.id === group.id && list[i].state <= 2) return true;
  return false;
}

function makeAdmin(nk, logger, userId) {
  var group = adminsGroup(nk);
  if (!group) {
    // the first admin founds the group (closed: nobody may ask to join it)
    nk.groupCreate(userId, ADMINS, userId, 'en', 'UnitSport admins: the dashboard and admin rights', '', false, {}, 100);
    logger.info('admins group created by %s', userId);
    return;
  }
  nk.groupUsersAdd(group.id, [userId], null);
}

function requireSession(ctx) {
  if (!ctx.userId) throw { message: 'sign in first', code: Code.UNAUTHENTICATED };
}

function requireAdmin(ctx, nk) {
  requireSession(ctx);
  if (!isAdmin(nk, ctx.userId)) throw { message: 'admins only', code: Code.PERMISSION_DENIED };
}

function parse(payload) {
  try { return JSON.parse(payload || '{}'); } catch (e) { throw { message: 'body is not JSON', code: Code.INVALID_ARGUMENT }; }
}

// Who is signed in: { id, username, email, admin }. A bootstrap email joins the admins here.
function rpcMe(ctx, logger, nk, payload) {
  requireSession(ctx);
  var account = nk.accountGetId(ctx.userId);
  var email = (account.email || '').toLowerCase();
  var admin = isAdmin(nk, ctx.userId);
  if (!admin && email && bootstrapEmails(ctx).indexOf(email) >= 0) {
    makeAdmin(nk, logger, ctx.userId);
    admin = true;
    logger.info('bootstrap admin %s (%s)', account.user.username, email);
  }
  return JSON.stringify({ id: ctx.userId, username: account.user.username, email: account.email || '', admin: admin });
}

// Accounts by name or email, newest first: { users: [{ id, username, email, created, banned }] }
function rpcAdminUsers(ctx, logger, nk, payload) {
  requireAdmin(ctx, nk);
  // LIKE's own wildcards and its escape, typed by the admin, match themselves
  var q = String(parse(payload).q || '').trim().substring(0, 64).replace(/[\\%_]/g, '\\$&');
  var rows = nk.sqlQuery(
    "SELECT id, username, email, create_time, disable_time FROM users " +
    "WHERE id <> '00000000-0000-0000-0000-000000000000' AND (username ILIKE $1 OR email ILIKE $1) " +
    "ORDER BY create_time DESC LIMIT 50", ['%' + q + '%']);
  return JSON.stringify({ users: rows.map(function (r) {
    return { id: r.id, username: r.username, email: r.email || '', created: r.create_time, banned: isBanned(r.disable_time) };
  }) });
}

function isBanned(disableTime) {
  if (!disableTime) return false;
  var t = new Date(disableTime).getTime();
  return t > 0;   // Nakama keeps 1970-01-01 for "never"
}

// One account: { id, username, email, created, updated, banned, admin, devices }
function rpcAdminUser(ctx, logger, nk, payload) {
  requireAdmin(ctx, nk);
  var id = String(parse(payload).id || '');
  var account;
  try { account = nk.accountGetId(id); } catch (e) { throw { message: 'no such account', code: Code.NOT_FOUND }; }
  return JSON.stringify({
    id: account.user.userId || id,
    username: account.user.username,
    email: account.email || '',
    created: account.user.createTime,
    updated: account.user.updateTime,
    banned: isBanned(account.disableTime),
    admin: isAdmin(nk, id),
    devices: (account.devices || []).length,
  });
}

// { id, admin }: add to or remove from the admins. An admin cannot remove their own rights (no lock-out by mistake).
function rpcAdminSetAdmin(ctx, logger, nk, payload) {
  requireAdmin(ctx, nk);
  var r = parse(payload);
  var id = String(r.id || '');
  if (!id) throw { message: 'invalid id', code: Code.INVALID_ARGUMENT };
  if (r.admin === true) makeAdmin(nk, logger, id);
  else {
    if (id === ctx.userId) throw { message: 'you cannot remove your own admin rights', code: Code.INVALID_ARGUMENT };
    var group = adminsGroup(nk);
    if (group) nk.groupUsersKick(group.id, [id], null);
  }
  logger.info('admin %s set admin=%s on %s', ctx.userId, r.admin === true, id);
  return JSON.stringify({ id: id, admin: r.admin === true });
}

// { id, ban }: a banned account cannot sign in, its sessions end, and servers that check accounts refuse it.
function rpcAdminBan(ctx, logger, nk, payload) {
  requireAdmin(ctx, nk);
  var r = parse(payload);
  var id = String(r.id || '');
  if (!id) throw { message: 'invalid id', code: Code.INVALID_ARGUMENT };
  if (id === ctx.userId) throw { message: 'you cannot ban yourself', code: Code.INVALID_ARGUMENT };
  if (r.ban === true) nk.usersBanId([id]); else nk.usersUnbanId([id]);
  logger.info('admin %s set ban=%s on %s', ctx.userId, r.ban === true, id);
  return JSON.stringify({ id: id, banned: r.ban === true });
}
