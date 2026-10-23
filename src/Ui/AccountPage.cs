using Godot;
using UnitSport.Net.Accounts;

namespace UnitSport.Ui;

/// <summary>
/// Settings → Account and the welcome's last page (#1123): play offline (no account, the default) or online.
/// <list type="bullet">
/// <item>Signed in: one card, "Signed in as …", and Sign out.</item>
/// <item>Signed out: two choices, "I have an account" (email, password, Sign in) and "New account" (email, password, a
/// name, Create account).</item>
/// <item>The account server is the build's official one (<see cref="Account.Official"/>); Advanced, folded away, sets
/// another, and opens by itself while the build names none.</item>
/// </list>
/// Saved in <c>user://online_account.json</c> by <see cref="Account"/>, never the password.
/// </summary>
public static class AccountPage
{
    /// <summary>The email typed so far, kept when the page switches between its two choices.</summary>
    private static string? _typedEmail;

    public static void Fill(VBoxContainer rows)
    {
        UiKit.OptionRow(rows, "Mode", new[] { "Offline", "Online" }, (int)Account.Mode, chosen => Account.SetMode((AccountMode)chosen),
            "Offline joins the servers that do not ask for an account. Online signs in, for the ones that require one", commit: () => { });
        rows.AddChild(UiKit.Spacer(10));
        var body = UiKit.VBox(10);
        rows.AddChild(body);

        bool creating = false;
        string message = "";
        void Build()
        {
            if (!GodotObject.IsInstanceValid(body)) { Account.Changed -= Build; return; }
            foreach (var c in body.GetChildren()) { body.RemoveChild(c); c.QueueFree(); }
            if (Account.Mode == AccountMode.Offline)
                body.AddChild(UiKit.Text("No account: solo play and every server that does not require one. Your progress stays on this machine.",
                    UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
            else if (Account.SignedIn) SignedIn(body, () => { message = "Signed out."; Account.SignOut(); });
            else SignInForm(body, creating, c => { creating = c; message = ""; Build(); }, message, m => message = m);
        }
        Account.Changed += Build;
        rows.TreeExiting += () => Account.Changed -= Build;
        Build();
    }

    /// <summary>The card of a signed-in player.</summary>
    private static void SignedIn(VBoxContainer into, System.Action signOut)
    {
        var card = UiKit.VBox(6);
        card.AddChild(UiKit.Text($"Signed in as {Account.Username}", UiTheme.FontHeading, bold: true));
        string where = Account.CustomServer ? $" · {Account.Url}" : "";
        card.AddChild(UiKit.Text($"{Account.Email}{where}", UiTheme.FontSmall, UiTheme.TextDim));
        card.AddChild(UiKit.Text("Servers that require an account let you in under this name.", UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        var row = UiKit.HBox(8);
        row.AddChild(UiKit.Spacer(expand: true));
        var outButton = UiKit.Button("Sign out");
        outButton.Pressed += signOut;
        row.AddChild(outButton);
        card.AddChild(row);
        into.AddChild(UiKit.Card(card));
    }

    /// <summary>"I have an account" or "New account": email and password, a name for a new one, one button.</summary>
    private static void SignInForm(VBoxContainer into, bool creating, System.Action<bool> choose, string message, System.Action<string> remember)
    {
        var tabs = UiKit.HBox(8);
        var group = new ButtonGroup();
        foreach (var (label, isNew) in new[] { ("I have an account", false), ("New account", true) })
        {
            var b = UiKit.Button(label, primary: isNew == creating);
            b.ToggleMode = true;
            b.ButtonGroup = group;
            b.ButtonPressed = isNew == creating;
            b.Pressed += () => { if (isNew != creating) choose(isNew); };
            tabs.AddChild(b);
        }
        into.AddChild(tabs);

        var email = UiKit.TextRow(into, "Email", _typedEmail ?? Account.Email, "you@example.ch");
        email.TextChanged += t => _typedEmail = t;
        var password = UiKit.TextRow(into, "Password", "", "8 characters or more", secret: true);
        LineEdit? name = creating ? UiKit.TextRow(into, "Name", "", "e.g. Heidi_92", "Your name on the scoreboards; empty picks one") : null;

        var note = UiKit.Text(message, UiTheme.FontSmall, UiTheme.Amber, wrap: true);
        note.Visible = message.Length > 0;
        var go = UiKit.Button(creating ? "Create account" : "Sign in", primary: true, minWidth: 180);
        var actions = UiKit.HBox(12);
        actions.AddChild(note);
        note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        actions.AddChild(go);
        into.AddChild(actions);

        async void Go()
        {
            go.Disabled = true;
            note.Text = creating ? "Creating the account..." : "Signing in...";
            note.Visible = true;
            string? why = await Account.SignIn(email.Text, password.Text, creating, creating ? name?.Text.Trim() : null);
            remember(why ?? "");
            // on success Account.Changed rebuilds the page into the signed-in card
            if (!GodotObject.IsInstanceValid(note)) return;
            go.Disabled = false;
            note.Text = why ?? "";
            note.Visible = why != null;
        }
        go.Pressed += Go;
        password.TextSubmitted += _ => Go();

        Advanced(into);
    }

    /// <summary>The account server, folded away: the build's official one unless set here.</summary>
    private static void Advanced(VBoxContainer into)
    {
        bool none = Account.Official.Url.Length == 0;
        var fold = UiKit.Button((none ? "▾" : "▸") + " Advanced: account server");
        fold.Flat = true;
        fold.Alignment = HorizontalAlignment.Left;
        UiKit.DimFont(fold);
        into.AddChild(fold);
        var box = UiKit.VBox(4);
        box.Visible = none || Account.CustomServer;
        into.AddChild(box);
        fold.Pressed += () =>
        {
            box.Visible = !box.Visible;
            fold.Text = (box.Visible ? "▾" : "▸") + " Advanced: account server";
        };
        box.AddChild(UiKit.Text(none
            ? "This build names no official account server. Ask the server's admin for its address and key."
            : $"The official one is {Account.Official.Url}. Set another only to sign in to a private server.",
            UiTheme.FontTiny, UiTheme.TextFaint, wrap: true));
        var url = UiKit.TextRow(box, "Address", Account.CustomServer ? Account.Url : "", none ? "http://host:7350" : Account.Official.Url);
        var key = UiKit.TextRow(box, "Server key", Account.CustomServer ? Account.ServerKey : "", "from the server's admin", "Not a password");
        void Server() => Account.SetServer(url.Text, key.Text);
        url.TextSubmitted += _ => Server();
        url.FocusExited += Server;
        key.TextSubmitted += _ => Server();
        key.FocusExited += Server;
    }
}
