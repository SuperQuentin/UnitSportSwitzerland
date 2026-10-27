using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace UnitSport.LayoutService;

/// <summary>
/// Sends the login link. SMTP from the environment (<c>SMTP_HOST</c>, <c>SMTP_PORT</c>, <c>SMTP_USER</c>,
/// <c>SMTP_PASS</c>, <c>SMTP_FROM</c>); with no host the link goes to the log instead, for a local run.
/// Port 465 is implicit TLS (SMTPS); any other port (587) upgrades with STARTTLS, which is required.
/// </summary>
public sealed class Mailer(ILogger<Mailer> log)
{
    private const int TimeoutMs = 15_000;
    private readonly string? _host = Environment.GetEnvironmentVariable("SMTP_HOST");
    private readonly int _port = int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) ? p : 587;
    private readonly string? _user = Environment.GetEnvironmentVariable("SMTP_USER");
    private readonly string? _pass = Environment.GetEnvironmentVariable("SMTP_PASS");
    private readonly string _from = Environment.GetEnvironmentVariable("SMTP_FROM") ?? "unitsport@localhost";

    public Task SendLoginLink(string to, string link) => Deliver(to, "login link", "UnitSport: your login link for house layouts",
        "Open this link to log in to the UnitSport house layout editor (valid 30 minutes, once):\n\n" + link
        + "\n\nIf you did not ask for it, ignore this email.");

    public Task SendPasswordReset(string to, string link) => Deliver(to, "password reset link", "UnitSport: reset your password",
        "Open this link to choose a new password for your UnitSport account (valid 30 minutes, once):\n\n" + link
        + "\n\nIf you did not ask for it, ignore this email: your password stays as it is.");

    private async Task Deliver(string to, string what, string subject, string text)
    {
        if (string.IsNullOrEmpty(_host))
        {
            log.LogWarning("no SMTP_HOST: {What} for {To}: {Text}", what, to, text);
            return;
        }
        await Send(to, subject, text);
    }

    /// <summary>The one connect, authenticate and send path (login links and the check).</summary>
    private async Task Send(string to, string subject, string body)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(_from));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };
        using var cts = new CancellationTokenSource(TimeoutMs);
        using var smtp = new SmtpClient { Timeout = TimeoutMs };
        await smtp.ConnectAsync(_host!, _port,
            _port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, cts.Token);
        if (!string.IsNullOrEmpty(_user)) await smtp.AuthenticateAsync(_user, _pass ?? "", cts.Token);
        await smtp.SendAsync(msg, cts.Token);
        await smtp.DisconnectAsync(true, cts.Token);
    }

    /// <summary><c>LayoutService --smtp-check &lt;to&gt;</c>: sends a test mail with the real settings and says why it failed
    /// (the password is never printed). Returns the exit code.</summary>
    public async Task<int> Check(string to)
    {
        if (string.IsNullOrEmpty(_host)) { Console.Error.WriteLine("SMTP_HOST is empty: nothing to check (login links only go to the log)."); return 1; }
        var tls = _port == 465 ? "implicit TLS" : "STARTTLS";
        Console.WriteLine($"SMTP host={_host} port={_port} tls={tls} user={(string.IsNullOrEmpty(_user) ? "(none)" : _user)} from={_from} to={to}");
        try
        {
            await Send(to, "UnitSport: SMTP check",
                "The UnitSport house layout service can send email with its current SMTP settings.");
            Console.WriteLine("OK: test mail accepted by the server.");
            return 0;
        }
        catch (Exception e)
        {
            var status = e switch
            {
                SmtpCommandException c => $"{(int)c.StatusCode} {c.StatusCode} ({c.ErrorCode}): ",
                AuthenticationException => "authentication failed: ",
                SslHandshakeException => "TLS handshake failed: ",
                OperationCanceledException or TimeoutException => "timed out after 15 s: ",
                SocketException s => $"{s.SocketErrorCode}: ",
                _ => "",
            };
            var inner = e.InnerException is { } i ? $" (inner: {i.GetType().Name}: {FirstLine(i.Message)})" : "";
            Console.Error.WriteLine($"FAILED: {status}{e.GetType().Name}: {FirstLine(e.Message)}{inner}");
            return 1;
        }
    }

    private static string FirstLine(string m) => m.Split('\n')[0].TrimEnd();
}
