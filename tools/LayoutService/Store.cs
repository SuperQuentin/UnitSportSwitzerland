using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace UnitSport.LayoutService;

public sealed record Claim(
    long Id, long UserId, string Email, uint Egid, string Status, string Address, string Note,
    double E, double N, double YawDeg, string LayoutJson, string PlanJson, string? PlanFile,
    string Reason, long Created, long Updated, long? Submitted, long? Reviewed);

/// <summary>
/// SQLite: users, one-time login tokens, sessions and claims. A claim is one person's layout of one
/// building, from draft to pending (submitted) to approved or rejected; withdrawn or replaced at the end.
/// Tokens are stored hashed, so a copy of the database logs nobody in.
///
/// <para>
/// SQLite by default, PostgreSQL when <c>LAYOUTS_DB</c> is a Npgsql connection string ("Host=...") or a
/// postgres:// URL. Only the schema differs; every query is plain SQL both run (@params, RETURNING,
/// ON CONFLICT, the partial unique index).
/// </para>
/// </summary>
public sealed class Store
{
    public static readonly string[] Open = ["draft", "pending"];
    private readonly Func<DbConnection> _open;
    public bool Postgres { get; }

    /// <summary><paramref name="db"/>: a SQLite file path, a Npgsql connection string or a postgres:// URL.</summary>
    public Store(string db)
    {
        Postgres = db.StartsWith("postgres://") || db.StartsWith("postgresql://") || db.Contains("Host=", StringComparison.OrdinalIgnoreCase);
        if (Postgres)
        {
            var src = NpgsqlDataSource.Create(db.Contains("://") ? FromUrl(db) : db);
            _open = () => src.OpenConnection();
        }
        else
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Cache = SqliteCacheMode.Shared }.ToString();
            _open = () => { var c = new SqliteConnection(cs); c.Open(); return c; };
        }
        using var conn = _open();
        // ids and floats are the only words the two schemas spell differently
        string id = Postgres ? "BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY" : "INTEGER PRIMARY KEY";
        string i64 = Postgres ? "BIGINT" : "INTEGER", f64 = Postgres ? "DOUBLE PRECISION" : "REAL";
        if (!Postgres) Exec(conn, null, "PRAGMA journal_mode = WAL;");
        Exec(conn, null, $"""
            CREATE TABLE IF NOT EXISTS users (id {id}, email TEXT NOT NULL UNIQUE, created {i64} NOT NULL);
            CREATE TABLE IF NOT EXISTS login_tokens (hash TEXT PRIMARY KEY, email TEXT NOT NULL, expires {i64} NOT NULL, used INTEGER NOT NULL DEFAULT 0, created {i64} NOT NULL);
            CREATE INDEX IF NOT EXISTS login_tokens_email ON login_tokens (email, created);
            CREATE TABLE IF NOT EXISTS sessions (hash TEXT PRIMARY KEY, user_id {i64} NOT NULL, expires {i64} NOT NULL);
            CREATE TABLE IF NOT EXISTS claims (
                id {id}, user_id {i64} NOT NULL, egid {i64} NOT NULL, status TEXT NOT NULL,
                address TEXT NOT NULL DEFAULT '', note TEXT NOT NULL DEFAULT '',
                e {f64} NOT NULL, n {f64} NOT NULL, yaw {f64} NOT NULL,
                layout TEXT NOT NULL, plan TEXT NOT NULL DEFAULT '', plan_file TEXT, reason TEXT NOT NULL DEFAULT '',
                created {i64} NOT NULL, updated {i64} NOT NULL, submitted {i64}, reviewed {i64}, reviewer TEXT);
            CREATE UNIQUE INDEX IF NOT EXISTS claims_one_open ON claims (egid) WHERE status IN ('draft', 'pending');
            CREATE INDEX IF NOT EXISTS claims_user ON claims (user_id);
            """);
    }

    /// <summary>postgres://user:pass@host:port/db to a Npgsql connection string.</summary>
    private static string FromUrl(string url)
    {
        var u = new Uri(url);
        var userInfo = u.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = u.Host, Port = u.Port > 0 ? u.Port : 5432, Database = u.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        }.ToString();
    }

    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private DbConnection Conn() => _open();

    private static int Exec(DbConnection c, DbTransaction? tx, string sql, params (string, object?)[] args) =>
        Cmd(c, tx, sql, args).ExecuteNonQuery();

    private static DbCommand Cmd(DbConnection c, DbTransaction? tx, string sql, (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx; // SQLite refuses a command outside the connection's open transaction
        foreach (var (k, v) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = k;
            p.Value = v ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private int Exec(string sql, params (string, object?)[] args)
    {
        using var c = Conn();
        return Exec(c, null, sql, args);
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var c = Conn();
        return Cmd(c, null, sql, args).ExecuteScalar();
    }

    private static bool UniqueViolation(DbException ex) =>
        ex is SqliteException { SqliteErrorCode: 19 } || ex.SqlState == PostgresErrorCodes.UniqueViolation;

    // --- login ---

    /// <summary>A new login token for the address, or null when it already had three in the last hour.</summary>
    public string? NewLoginToken(string email)
    {
        long now = Now;
        if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM login_tokens WHERE email = @e AND created > @t", ("@e", email), ("@t", now - 3600))) >= 3)
            return null;
        Exec("DELETE FROM login_tokens WHERE expires < @t", ("@t", now - 86400));
        var token = NewToken();
        Exec("INSERT INTO login_tokens (hash, email, expires, created) VALUES (@h, @e, @x, @t)",
            ("@h", Hash(token)), ("@e", email), ("@x", now + 1800), ("@t", now));
        return token;
    }

    /// <summary>Spends a login token: a new session token for its user, or null when unknown, used or expired.</summary>
    public string? Redeem(string token)
    {
        using var c = Conn();
        using var tx = c.BeginTransaction();
        var email = Cmd(c, tx, "UPDATE login_tokens SET used = 1 WHERE hash = @h AND used = 0 AND expires > @t RETURNING email",
            [("@h", Hash(token)), ("@t", Now)]).ExecuteScalar() as string;
        if (email == null) return null;
        Exec(c, tx, "INSERT INTO users (email, created) VALUES (@e, @t) ON CONFLICT (email) DO NOTHING", ("@e", email), ("@t", Now));
        long user = Convert.ToInt64(Cmd(c, tx, "SELECT id FROM users WHERE email = @e", [("@e", email)]).ExecuteScalar());
        var session = NewToken();
        Exec(c, tx, "INSERT INTO sessions (hash, user_id, expires) VALUES (@h, @u, @x)",
            ("@h", Hash(session)), ("@u", user), ("@x", Now + 30 * 86400));
        Exec(c, tx, "DELETE FROM sessions WHERE expires < @t", ("@t", Now));
        tx.Commit();
        return session;
    }

    public (long Id, string Email)? User(string? session)
    {
        if (string.IsNullOrEmpty(session)) return null;
        using var c = Conn();
        using var r = Cmd(c, null, "SELECT u.id, u.email FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.hash = @h AND s.expires > @t",
            [("@h", Hash(session)), ("@t", Now)]).ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetString(1)) : null;
    }

    public void Logout(string session) => Exec("DELETE FROM sessions WHERE hash = @h", ("@h", Hash(session)));

    // --- claims ---

    private const string ClaimCols = """
        SELECT c.id, c.user_id, u.email, c.egid, c.status, c.address, c.note, c.e, c.n, c.yaw, c.layout, c.plan,
               c.plan_file, c.reason, c.created, c.updated, c.submitted, c.reviewed
        FROM claims c JOIN users u ON u.id = c.user_id
        """;

    private List<Claim> Claims(string where, params (string, object?)[] args)
    {
        using var c = Conn();
        using var r = Cmd(c, null, ClaimCols + " " + where, args).ExecuteReader();
        var list = new List<Claim>();
        while (r.Read())
            list.Add(new Claim(r.GetInt64(0), r.GetInt64(1), r.GetString(2), (uint)r.GetInt64(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetDouble(7), r.GetDouble(8), r.GetDouble(9), r.GetString(10),
                r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetString(13), r.GetInt64(14),
                r.GetInt64(15), r.IsDBNull(16) ? null : r.GetInt64(16), r.IsDBNull(17) ? null : r.GetInt64(17)));
        return list;
    }

    public Claim? Get(long id) => Claims("WHERE c.id = @id", ("@id", id)).FirstOrDefault();

    public List<Claim> OfUser(long user) => Claims("WHERE c.user_id = @u ORDER BY c.updated DESC", ("@u", user));

    public List<Claim> WithStatus(string status) => Claims("WHERE c.status = @s ORDER BY c.submitted, c.updated", ("@s", status));

    public List<Claim> OfBuilding(uint egid) => Claims("WHERE c.egid = @g ORDER BY c.updated DESC", ("@g", (long)egid));

    /// <summary>A new draft, or null when the building already has an open claim.</summary>
    public long? Create(long user, uint egid, string address, string note, double e, double n, double yaw, string layout)
    {
        try
        {
            long now = Now;
            return Convert.ToInt64(Scalar("""
                INSERT INTO claims (user_id, egid, status, address, note, e, n, yaw, layout, plan, created, updated)
                VALUES (@u, @g, 'draft', @a, @no, @e, @n, @y, @l, '{}', @t, @t) RETURNING id
                """, ("@u", user), ("@g", (long)egid), ("@a", address), ("@no", note), ("@e", e), ("@n", n),
                ("@y", yaw), ("@l", layout), ("@t", now)));
        }
        catch (DbException ex) when (UniqueViolation(ex)) // the one-open-claim index
        {
            return null;
        }
    }

    /// <summary>Saves the drawing; a rejected claim goes back to draft. False unless draft or rejected.</summary>
    public bool SaveLayout(long id, string layout, string plan, string note) => Exec("""
        UPDATE claims SET layout = @l, plan = @p, note = @no, updated = @t,
            status = CASE status WHEN 'rejected' THEN 'draft' ELSE status END
        WHERE id = @id AND status IN ('draft', 'rejected')
        """, ("@l", layout), ("@p", plan), ("@no", note), ("@t", Now), ("@id", id)) == 1;

    public bool SetPlanFile(long id, string file) =>
        Exec("UPDATE claims SET plan_file = @f, updated = @t WHERE id = @id AND status IN ('draft', 'rejected')",
            ("@f", file), ("@t", Now), ("@id", id)) == 1;

    public bool Submit(long id) =>
        Exec("UPDATE claims SET status = 'pending', submitted = @t, updated = @t, reason = '' WHERE id = @id AND status = 'draft'",
            ("@t", Now), ("@id", id)) == 1;

    public bool Withdraw(long id) =>
        Exec("UPDATE claims SET status = 'withdrawn', updated = @t WHERE id = @id AND status IN ('draft', 'pending', 'approved', 'rejected')",
            ("@t", Now), ("@id", id)) == 1;

    /// <summary>Approves a pending claim; the building's earlier approved layout becomes 'replaced'.</summary>
    public bool Approve(long id, string reviewer)
    {
        using var c = Conn();
        using var tx = c.BeginTransaction();
        var egid = Cmd(c, tx, "SELECT egid FROM claims WHERE id = @id AND status = 'pending'", [("@id", id)]).ExecuteScalar();
        if (egid == null) return false;
        Exec(c, tx, "UPDATE claims SET status = 'replaced', updated = @t WHERE egid = @g AND status = 'approved'", ("@g", egid), ("@t", Now));
        Exec(c, tx, "UPDATE claims SET status = 'approved', reviewed = @t, reviewer = @r, updated = @t, reason = '' WHERE id = @id",
            ("@t", Now), ("@r", reviewer), ("@id", id));
        tx.Commit();
        return true;
    }

    public bool Reject(long id, string reviewer, string reason) =>
        Exec("UPDATE claims SET status = 'rejected', reason = @why, reviewed = @t, reviewer = @r, updated = @t WHERE id = @id AND status IN ('draft', 'pending')",
            ("@why", reason), ("@t", Now), ("@r", reviewer), ("@id", id)) == 1;

    public List<string> ApprovedLayouts()
    {
        using var c = Conn();
        using var r = Cmd(c, null, "SELECT layout FROM claims WHERE status = 'approved' ORDER BY egid", []).ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
