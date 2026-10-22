using Godot;
using UnitSport.Net.Accounts;

namespace UnitSport.Ui;

/// <summary>
/// Settings → Account (#1123): play offline (no account, the default) or online, signed in to an account server
/// (Nakama: its address and server key, then email and password). Saved in <c>user://account.json</c> by
/// <see cref="Account"/>, never the password. A server that requires an account refuses an offline player with a
/// reason that points here.
/// </summary>
public static class AccountPage
{
    public static void Fill(VBoxContainer rows)
    {
        var status = UiKit.Text(Account.Status, UiTheme.FontBody, UiTheme.Amber, wrap: true);
        rows.AddChild(status);
        rows.AddChild(UiKit.Spacer(6));

        var online = UiKit.VBox(4);
        UiKit.OptionRow(rows, "Mode", new[] { "Offline", "Online" }, (int)Account.Mode, chosen =>
        {
            Account.SetMode((AccountMode)chosen);
            online.Visible = Account.Mode == AccountMode.Online;
        }, "Offline joins servers that do not ask for an account. Online signs in, for the servers that require one", commit: () => { });
        rows.AddChild(online);
        online.Visible = Account.Mode == AccountMode.Online;

        online.AddChild(UiKit.Section("Account server"));
        var url = UiKit.TextRow(online, "Address", Account.Url, "http://host:7350", "The account server your servers name (Nakama's API)");
        var key = UiKit.TextRow(online, "Server key", Account.ServerKey, "from the server's admin", "Not a password: the server's admin hands it out");
        void Server() => Account.SetServer(url.Text, key.Text);
        url.TextSubmitted += _ => Server();
        url.FocusExited += Server;
        key.TextSubmitted += _ => Server();
        key.FocusExited += Server;

        online.AddChild(UiKit.Section("Sign in"));
        var email = UiKit.TextRow(online, "Email", Account.Email, "you@example.ch");
        var password = UiKit.TextRow(online, "Password", "", "8 characters or more", secret: true);
        var name = UiKit.TextRow(online, "Name", "", "for a new account only", "Your name on the scoreboards; empty picks one");

        var message = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextDim, wrap: true);
        var buttons = UiKit.HBox(8);
        buttons.Alignment = BoxContainer.AlignmentMode.End;
        var signIn = UiKit.Button("Sign in", primary: true);
        var create = UiKit.Button("Create account");
        var signOut = UiKit.Button("Sign out");
        buttons.AddChild(signIn);
        buttons.AddChild(create);
        buttons.AddChild(signOut);
        online.AddChild(buttons);
        online.AddChild(message);

        async void Go(bool makeNew)
        {
            Server();
            signIn.Disabled = create.Disabled = true;
            message.Text = makeNew ? "Creating the account..." : "Signing in...";
            string? why = await Account.SignIn(email.Text, password.Text, makeNew, makeNew ? name.Text.Trim() : null);
            if (!GodotObject.IsInstanceValid(message)) return;   // the settings closed meanwhile
            signIn.Disabled = create.Disabled = false;
            if (why == null) password.Text = "";
            message.Text = why ?? "";
        }
        signIn.Pressed += () => Go(false);
        create.Pressed += () => Go(true);
        password.TextSubmitted += _ => Go(false);
        signOut.Pressed += () => { Account.SignOut(); message.Text = "Signed out."; };

        void Refresh()
        {
            if (!GodotObject.IsInstanceValid(status)) { Account.Changed -= Refresh; return; }
            status.Text = Account.Status;
            signOut.Disabled = !Account.SignedIn;
        }
        Account.Changed += Refresh;
        rows.TreeExiting += () => Account.Changed -= Refresh;
        Refresh();
    }
}
