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
        using var smtp = new SmtpClient(_host, _port) { EnableSsl = true };
        if (!string.IsNullOrEmpty(_user)) smtp.Credentials = new NetworkCredential(_user, _pass);
        using var msg = new MailMessage(_from, to)
        {
            Subject = "UnitSport: your login link for house layouts",
            Body = "Open this link to log in to the UnitSport house layout editor (valid 30 minutes, once):\n\n"
                   + link + "\n\nIf you did not ask for it, ignore this email.",
        };
        await smtp.SendMailAsync(msg);
    }
}
