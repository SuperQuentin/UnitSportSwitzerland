// The house layout service (#1186, docs/notes/net/house-layouts.md): people claim their building, draw
// its floors on houses.html, submit; an admin approves; approved layouts are exported for the game server.
// Listens on loopback only, behind Caddy at /api/. Configured by environment variables:
//   LAYOUTS_DATA     dir for layouts.db and uploads/ (default ./layouts-data)
//   LAYOUTS_DB       the database: a SQLite file (default $LAYOUTS_DATA/layouts.db), or PostgreSQL as a
//                    Npgsql connection string ("Host=...;Database=...") or a postgres://user:pass@host/db URL
//   LAYOUTS_CHUNKS   the terrain tile dir (the .bldg files; default ./terrain_chunks)
//   LAYOUTS_EXPORT   the approved layouts file the game server reads (default $LAYOUTS_DATA/house-layouts.json)
//   PUBLIC_URL       the site the login link points at (default http://localhost:5080)
//   ADMIN_EMAILS     comma-separated reviewer addresses
//   COOKIE_SECURE    0 for a plain-http local run (default 1)
//   LAYOUTS_URLS     where to listen (default http://127.0.0.1:5080); SMTP_*: see Mailer
using System.Net.Mail;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using UnitSport.LayoutService;
using UnitSport.Terrain.Format;

string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
var dataDir = Path.GetFullPath(Env("LAYOUTS_DATA", "layouts-data"));
var uploads = Path.Combine(dataDir, "uploads");
Directory.CreateDirectory(uploads);
var exportPath = Path.GetFullPath(Env("LAYOUTS_EXPORT", Path.Combine(dataDir, "house-layouts.json")));
var publicUrl = Env("PUBLIC_URL", "http://localhost:5080").TrimEnd('/');
var admins = Env("ADMIN_EMAILS", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(a => a.ToLowerInvariant()).ToHashSet();
bool secure = Env("COOKIE_SECURE", "1") != "0";
const string Cookie = "hl_session";
const long MaxPlanBytes = 10 << 20, MaxLayoutBytes = 1 << 20;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Env("LAYOUTS_URLS", "http://127.0.0.1:5080"));
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxPlanBytes + 64 * 1024);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
});
// behind the edge proxy and Caddy, both on the way in: the visitor is the first forwarded address
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    o.ForwardLimit = 2;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
    o.AddPolicy("write", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddSingleton(new Store(Env("LAYOUTS_DB", Path.Combine(dataDir, "layouts.db"))));
builder.Services.AddSingleton(new Buildings(Path.GetFullPath(Env("LAYOUTS_CHUNKS", "terrain_chunks"))));
builder.Services.AddSingleton<Mailer>();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseRateLimiter();
var log = app.Logger;
var store = app.Services.GetRequiredService<Store>();
var buildings = app.Services.GetRequiredService<Buildings>();

// CSRF: the session cookie is SameSite=Strict, and every state change must carry a header a form cannot send
app.Use(async (ctx, next) =>
{
    if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
        && ctx.Request.Headers["X-Requested-With"] != "houses")
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { error = "missing X-Requested-With" });
        return;
    }
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers.CacheControl = "no-store";
    await next();
});

(long Id, string Email)? Me(HttpContext ctx) => store.User(ctx.Request.Cookies[Cookie]);
bool IsAdmin((long Id, string Email)? u) => u is { } x && admins.Contains(x.Email);
IResult Bad(string msg, int code = 400) => Results.Json(new { error = msg }, statusCode: code);

// a claim the caller may read: its owner or an admin
(Claim? claim, IResult? fail) Mine(HttpContext ctx, long id, bool write = false)
{
    var u = Me(ctx);
    if (u == null) return (null, Bad("log in first", 401));
    var c = store.Get(id);
    if (c == null || (c.UserId != u.Value.Id && (write || !IsAdmin(u)))) return (null, Bad("no such claim", 404));
    return (c, null);
}

object Summary(Claim c) => new
{
    c.Id, c.Egid, c.Status, c.Address, c.Note, c.Reason, c.Created, c.Updated, c.Submitted, c.Reviewed,
    hasPlan = c.PlanFile != null,
};

object Outline(BuildingInfo b) => new
{
    egid = b.Building.Egid,
    kind = b.Building.Kind.ToString(),
    floors = b.Building.Floors,
    yearBuilt = b.Building.YearBuilt,
    height = b.Building.MaxY - b.Building.MinY,
    frame = new { e = b.Outline.CenterE, n = b.Outline.CenterN, yawDeg = b.Outline.YawDeg, length = b.Outline.Length, width = b.Outline.Width },
    rings = b.Outline.Rings.Select(r => r.Select(p => new[] { Math.Round(p.E, 2), Math.Round(p.N, 2) })),
};

List<(double X, double Z)[]>? LocalRings(Claim c)
{
    var b = buildings.Find(c.Egid, c.E, c.N);
    return b?.Outline.Rings.Select(r => r.Select(p => BuildingOutline.ToLocal(c.E, c.N, c.YawDeg, p.E, p.N)).ToArray()).ToList();
}

void Export()
{
    var layouts = store.ApprovedLayouts().Select(j => JsonSerializer.Deserialize<JsonElement>(j)).ToList();
    var tmp = exportPath + ".tmp";
    File.WriteAllText(tmp, JsonSerializer.Serialize(new { version = 1, updated = Store.Now, layouts }));
    File.Move(tmp, exportPath, overwrite: true);
    log.LogInformation("exported {Count} approved layouts to {Path}", layouts.Count, exportPath);
}
if (!File.Exists(exportPath)) Export();

var api = app.MapGroup("/api");

api.MapPost("/login", async (LoginBody body, Mailer mail) =>
{
    string email;
    try { email = new MailAddress(body.Email?.Trim() ?? "").Address.ToLowerInvariant(); }
    catch (FormatException) { return Bad("not an email address"); }
    if (email.Length > 254) return Bad("not an email address");
    var token = store.NewLoginToken(email);
    if (token != null)
    {
        try { await mail.SendLoginLink(email, $"{publicUrl}/houses.html#login={token}"); }
        catch (Exception ex) { log.LogError(ex, "login mail to {Email} failed", email); return Bad("could not send the email", 502); }
    }
    return Results.Ok(new { ok = true }); // the same answer when throttled: it says nothing about the address
}).RequireRateLimiting("login");

api.MapPost("/login/verify", (TokenBody body, HttpContext ctx) =>
{
    var session = body.Token is { Length: > 10 and < 100 } t ? store.Redeem(t) : null;
    if (session == null) return Bad("this link is used or expired: ask for a new one", 401);
    ctx.Response.Cookies.Append(Cookie, session, new CookieOptions
    {
        HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Strict, Path = "/api", MaxAge = TimeSpan.FromDays(30),
    });
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("login");

api.MapPost("/logout", (HttpContext ctx) =>
{
    if (ctx.Request.Cookies[Cookie] is { } s) store.Logout(s);
    ctx.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/api" });
    return Results.Ok(new { ok = true });
});

api.MapGet("/me", (HttpContext ctx) =>
{
    var u = Me(ctx);
    if (u == null) return Bad("log in first", 401);
    return Results.Ok(new { email = u.Value.Email, admin = IsAdmin(u), claims = store.OfUser(u.Value.Id).Select(Summary) });
});

// public: the footprint is open data, and whether the building already has a layout or an open claim
api.MapGet("/building", (uint? egid, double e, double n) =>
{
    if (!double.IsFinite(e) || !double.IsFinite(n)) return Bad("bad position");
    var b = buildings.Find(egid ?? 0, e, n);
    if (b == null) return Bad("no building here in the game's map", 404);
    var claims = b.Building.Egid == 0 ? [] : store.OfBuilding(b.Building.Egid);
    return Results.Ok(new
    {
        building = Outline(b),
        claimable = b.Building.Egid != 0 && b.Building.Kind != BuildingKind.UnderConstruction,
        approved = claims.Any(c => c.Status == "approved"),
        open = claims.Any(c => Store.Open.Contains(c.Status)),
    });
});

api.MapPost("/claims", (ClaimBody body, HttpContext ctx) =>
{
    var u = Me(ctx);
    if (u == null) return Bad("log in first", 401);
    if (store.OfUser(u.Value.Id).Count(c => Store.Open.Contains(c.Status)) >= 5) return Bad("five open claims at most");
    var b = buildings.Find(body.Egid, body.E, body.N);
    if (b == null || b.Building.Egid != body.Egid || body.Egid == 0) return Bad("this building cannot be claimed", 404);
    var o = b.Outline;
    int above = Math.Clamp(b.Building.Floors == 0 ? 1 : (int)b.Building.Floors, 1, HouseLayout.MaxLevel + 1);
    var layout = new HouseLayout
    {
        Egid = body.Egid, E = o.CenterE, N = o.CenterN, YawDeg = o.YawDeg,
        Floors = Enumerable.Range(0, above).Select(l => new HouseFloor { Level = l }).ToList(),
    };
    var id = store.Create(u.Value.Id, body.Egid, Trim(body.Address, 200), Trim(body.Note, 2000), o.CenterE, o.CenterN, o.YawDeg, layout.ToJson());
    return id == null ? Bad("someone already has an open claim on this building", 409) : Results.Ok(new { id });
}).RequireRateLimiting("write");

api.MapGet("/claims/{id:long}", (long id, HttpContext ctx) =>
{
    var (c, fail) = Mine(ctx, id);
    if (fail != null) return fail;
    var b = buildings.Find(c!.Egid, c.E, c.N);
    return Results.Ok(new
    {
        claim = Summary(c),
        email = IsAdmin(Me(ctx)) ? c.Email : null,
        layout = JsonSerializer.Deserialize<JsonElement>(c.LayoutJson),
        plan = JsonSerializer.Deserialize<JsonElement>(c.PlanJson),
        building = b == null ? null : Outline(b),
        footprint = LocalRings(c)?.Select(r => r.Select(p => new[] { Math.Round(p.X, 3), Math.Round(p.Z, 3) })),
    });
});

api.MapPut("/claims/{id:long}/layout", async (long id, HttpContext ctx) =>
{
    var (c, fail) = Mine(ctx, id, write: true);
    if (fail != null) return fail;
    if (ctx.Request.ContentLength is null or > MaxLayoutBytes) return Bad("layout too large", 413);
    SaveBody? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SaveBody>(HouseLayout.Json); }
    catch (JsonException) { return Bad("not a layout"); }
    if (body?.Layout == null) return Bad("not a layout");
    var l = body.Layout;
    // the frame is the claim's, whatever the page sent
    (l.Egid, l.E, l.N, l.YawDeg, l.Version) = (c!.Egid, c.E, c.N, c.YawDeg, HouseLayout.CurrentVersion);
    var plan = body.Plan is { } p ? JsonSerializer.Serialize(p) : "{}";
    if (plan.Length > 4096) return Bad("plan settings too large");
    if (!store.SaveLayout(id, l.ToJson(), plan, Trim(body.Note ?? c.Note, 2000))) return Bad("this claim can no longer be edited", 409);
    return Results.Ok(new { saved = true, problems = l.Validate(LocalRings(c)) });
}).RequireRateLimiting("write");

api.MapPost("/claims/{id:long}/plan", async (long id, HttpContext ctx) =>
{
    var (c, fail) = Mine(ctx, id, write: true);
    if (fail != null) return fail;
    if (!ctx.Request.HasFormContentType) return Bad("send the plan as a form file");
    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.GetFile("plan");
    if (file == null || file.Length == 0 || file.Length > MaxPlanBytes) return Bad("a PNG or JPEG plan of at most 10 MB");
    var head = new byte[8];
    await using (var s = file.OpenReadStream()) _ = await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
    string? ext = head is [0x89, 0x50, 0x4E, 0x47, ..] ? "png" : head is [0xFF, 0xD8, 0xFF, ..] ? "jpg" : null;
    if (ext == null) return Bad("a PNG or JPEG plan of at most 10 MB");
    var name = $"{id}-{Store.NewToken()[..12]}.{ext}";
    await using (var o = File.Create(Path.Combine(uploads, name))) await file.CopyToAsync(o);
    if (!store.SetPlanFile(id, name)) { File.Delete(Path.Combine(uploads, name)); return Bad("this claim can no longer be edited", 409); }
    if (c!.PlanFile is { } old) File.Delete(Path.Combine(uploads, old));
    return Results.Ok(new { ok = true });
}).RequireRateLimiting("write");

api.MapGet("/claims/{id:long}/plan", (long id, HttpContext ctx) =>
{
    var (c, fail) = Mine(ctx, id);
    if (fail != null) return fail;
    if (c!.PlanFile == null) return Bad("no plan", 404);
    ctx.Response.Headers.CacheControl = "private, max-age=3600";
    return Results.File(Path.Combine(uploads, c.PlanFile), c.PlanFile.EndsWith(".png") ? "image/png" : "image/jpeg");
});

api.MapPost("/claims/{id:long}/submit", (long id, HttpContext ctx) =>
{
    var (c, fail) = Mine(ctx, id, write: true);
    if (fail != null) return fail;
    var problems = HouseLayout.FromJson(c!.LayoutJson)?.Validate(LocalRings(c)) ?? ["not a layout"];
    if (problems.Count > 0) return Results.Json(new { error = "fix these first", problems }, statusCode: 400);
    return store.Submit(id) ? Results.Ok(new { ok = true }) : Bad("only a draft can be submitted", 409);
});

api.MapPost("/claims/{id:long}/withdraw", (long id, HttpContext ctx) =>
{
    var (c, fail) = Mine(ctx, id, write: true);
    if (fail != null) return fail;
    if (!store.Withdraw(id)) return Bad("already closed", 409);
    if (c!.Status == "approved") Export();
    return Results.Ok(new { ok = true });
});

// --- review ---
var admin = api.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
    IsAdmin(Me(ctx.HttpContext)) ? await next(ctx) : Bad("admins only", 403));

admin.MapGet("/claims", (string? status) =>
    Results.Ok(store.WithStatus(status ?? "pending").Select(c => new { claim = Summary(c), email = c.Email })));

admin.MapPost("/claims/{id:long}/approve", (long id, HttpContext ctx) =>
{
    var c = store.Get(id);
    if (c == null) return Bad("no such claim", 404);
    var problems = HouseLayout.FromJson(c.LayoutJson)?.Validate(LocalRings(c)) ?? ["not a layout"];
    if (problems.Count > 0) return Results.Json(new { error = "the layout does not pass the rules", problems }, statusCode: 400);
    if (!store.Approve(id, Me(ctx)!.Value.Email)) return Bad("only a pending claim can be approved", 409);
    Export();
    return Results.Ok(new { ok = true });
});

admin.MapPost("/claims/{id:long}/reject", (long id, ReasonBody body, HttpContext ctx) =>
{
    var why = Trim(body.Reason, 1000);
    if (why.Length == 0) return Bad("say why, for the person who drew it");
    return store.Reject(id, Me(ctx)!.Value.Email, why) ? Results.Ok(new { ok = true }) : Bad("only an open claim can be rejected", 409);
});

app.Run();

static string Trim(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();

record LoginBody(string? Email);
record TokenBody(string? Token);
record ClaimBody(uint Egid, double E, double N, string? Address, string? Note);
record ReasonBody(string? Reason);
record SaveBody(HouseLayout? Layout, JsonElement? Plan, string? Note);
