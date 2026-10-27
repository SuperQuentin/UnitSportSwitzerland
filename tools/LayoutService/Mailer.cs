using System.Net;
using System.Net.Mail;

namespace UnitSport.LayoutService;

/// <summary>
/// Sends the login link. SMTP from the environment (<c>SMTP_HOST</c>, <c>SMTP_PORT</c>, <c>SMTP_USER</c>,
/// <c>SMTP_PASS</c>, <c>SMTP_FROM</c>); with no host the link goes to the log instead, for a local run.
/// </summary>
public sealed class Mailer(ILogger<Mailer> log)
{
    private readonly string? _host = Environment.GetEnvironmentVariable("SMTP_HOST");
    private readonly int _port = int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) ? p : 587;
    private readonly string? _user = Environment.GetEnvironmentVariable("SMTP_USER");
    private readonly string? _pass = Environment.GetEnvironmentVariable("SMTP_PASS");
    private readonly string _from = Environment.GetEnvironmentVariable("SMTP_FROM") ?? "unitsport@localhost";

    public async Task SendLoginLink(string to, string link)
    {
        if (string.IsNullOrEmpty(_host))
        {
            log.LogWarning("no SMTP_HOST: login link for {To}: {Link}", to, link);
            return;
        }
        using var smtp = NewClient();
        using var msg = new MailMessage(_from, to)
        {
            Subject = "UnitSport: your login link for house layouts",
            Body = "Open this link to log in to the UnitSport house layout editor (valid 30 minutes, once):\n\n"
                   + link + "\n\nIf you did not ask for it, ignore this email.",
        };
        await smtp.SendMailAsync(msg);
    }

    private SmtpClient NewClient()
    {
        var smtp = new SmtpClient(_host, _port) { EnableSsl = true, Timeout = 15_000 };
        if (!string.IsNullOrEmpty(_user)) smtp.Credentials = new NetworkCredential(_user, _pass);
        return smtp;
    }

    /// <summary><c>LayoutService --smtp-check &lt;to&gt;</c>: sends a test mail with the real settings and says why it failed
    /// (the password is never printed). Returns the exit code.</summary>
    public async Task<int> Check(string to)
    {
        if (string.IsNullOrEmpty(_host)) { Console.Error.WriteLine("SMTP_HOST is empty: nothing to check (login links only go to the log)."); return 1; }
        Console.WriteLine($"SMTP host={_host} port={_port} ssl=on user={(string.IsNullOrEmpty(_user) ? "(none)" : _user)} from={_from} to={to}");
        try
        {
            using var smtp = NewClient();
            using var msg = new MailMessage(_from, to)
            {
                Subject = "UnitSport: SMTP check",
                Body = "The UnitSport house layout service can send email with its current SMTP settings.",
            };
            await smtp.SendMailAsync(msg).WaitAsync(TimeSpan.FromSeconds(15));
            Console.WriteLine("OK: test mail accepted by the server.");
            return 0;
        }
        catch (Exception e)
        {
            var status = e is SmtpException se ? $"{se.StatusCode}: " : "";
            var inner = e.InnerException is { } i ? $" (inner: {i.GetType().Name}: {i.Message})" : "";
            Console.Error.WriteLine($"FAILED: {status}{e.GetType().Name}: {e.Message}{inner}");
            return 1;
        }
    }
}
