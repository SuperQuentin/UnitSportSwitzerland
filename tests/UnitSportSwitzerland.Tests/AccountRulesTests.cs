using System;
using System.Text;
using UnitSport.Net.Accounts;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The account rules (src/Net/Accounts/AccountRules.cs, linked in, #1123).</summary>
public class AccountRulesTests
{
    [Theory]
    [InlineData("off", ServerAccounts.Off)]
    [InlineData("Optional", ServerAccounts.Optional)]
    [InlineData(" REQUIRED ", ServerAccounts.Required)]
    public void The_server_mode_parses_in_any_case(string arg, ServerAccounts mode) =>
        Assert.Equal(mode, AccountRules.Parse(arg));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("require")]
    public void Anything_else_is_no_mode(string? arg) => Assert.Null(AccountRules.Parse(arg));

    [Fact]
    public void Off_and_optional_admit_everyone()
    {
        foreach (var mode in new[] { ServerAccounts.Off, ServerAccounts.Optional })
        {
            Assert.Null(AccountRules.Refusal(mode, presented: false, verified: false, "http://a:7350"));
            // a token the account server refused: optional still lets the player in, as an offline one
            Assert.Null(AccountRules.Refusal(mode, presented: true, verified: false, "http://a:7350"));
            Assert.Null(AccountRules.Refusal(mode, presented: true, verified: true, "http://a:7350"));
        }
    }

    [Fact]
    public void Required_admits_a_checked_account_only_and_says_why_not()
    {
        Assert.Null(AccountRules.Refusal(ServerAccounts.Required, presented: true, verified: true, "http://a:7350"));
        string? offline = AccountRules.Refusal(ServerAccounts.Required, presented: false, verified: false, "http://a:7350");
        Assert.Contains("requires an online account on http://a:7350", offline);
        Assert.Contains("Settings › Account", offline);
        string? expired = AccountRules.Refusal(ServerAccounts.Required, presented: true, verified: false, "http://a:7350");
        Assert.Contains("could not check", expired);
    }

    [Theory]
    [InlineData("http://host:7350/", "http://host:7350")]
    [InlineData("  host:7350 ", "http://host:7350")]
    [InlineData("https://acc.example.ch", "https://acc.example.ch")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void An_account_server_has_one_spelling(string? url, string normal) =>
        Assert.Equal(normal, AccountRules.NormalizeUrl(url));

    [Fact]
    public void The_same_server_however_written_but_never_an_empty_one()
    {
        Assert.True(AccountRules.SameServer("HTTP://Host:7350/", "host:7350"));
        Assert.False(AccountRules.SameServer("host:7350", "host:7351"));
        Assert.False(AccountRules.SameServer("", ""));
    }

    private static string Jwt(string payload)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"HS256\"}")}.{B64(payload)}.sig";
    }

    [Fact]
    public void A_token_runs_out_at_its_exp()
    {
        var exp = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        string token = Jwt($"{{\"uid\":\"u\",\"exp\":{exp.ToUnixTimeSeconds()}}}");
        Assert.Equal(exp, AccountRules.TokenExpiry(token));
        Assert.True(AccountRules.Fresh(token, exp.AddMinutes(-5), TimeSpan.FromMinutes(1)));
        Assert.False(AccountRules.Fresh(token, exp.AddSeconds(-30), TimeSpan.FromMinutes(1)));
        Assert.False(AccountRules.Fresh(token, exp.AddSeconds(1), TimeSpan.Zero));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.!!!.c")]
    [InlineData("a.e30.c")]   // {} : no exp
    public void Not_a_token_never_counts_as_fresh(string? token)
    {
        Assert.Null(AccountRules.TokenExpiry(token));
        Assert.False(AccountRules.Fresh(token, DateTimeOffset.UnixEpoch, TimeSpan.Zero));
    }
}
