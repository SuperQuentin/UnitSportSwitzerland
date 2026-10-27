// The website's accounts (#1207, docs/plans/web-dashboard.md): sign in or create an account on Nakama, the same accounts as
// the game, through /nakama/ (Caddy). The server key comes from nakama.json, which the deploy writes; it is not a secret.
// The session (Nakama's token and refresh token) is kept in localStorage and renewed before it runs out.
// A page puts <div data-account></div> where the account control goes and loads this file; admin.html uses
// window.UnitSportAccount (ready, me, rpc, signOut) for its calls.
(function () {
  "use strict";
  var KEY = "unitsport.session";
  var config = null, session = null, current = null, listeners = [];

  function load() { try { return JSON.parse(localStorage.getItem(KEY) || "null"); } catch (e) { return null; } }
  function save(s) { session = s; try { s ? localStorage.setItem(KEY, JSON.stringify(s)) : localStorage.removeItem(KEY); } catch (e) {} }
  function expiry(jwt) {
    try { return JSON.parse(atob(jwt.split(".")[1].replace(/-/g, "+").replace(/_/g, "/"))).exp * 1000; } catch (e) { return 0; }
  }

  function getConfig() {
    if (config) return Promise.resolve(config);
    return fetch("nakama.json", { cache: "no-store" }).then(function (r) {
      if (!r.ok) throw new Error("Accounts are not set up on this server yet.");
      return r.json();
    }).then(function (c) { config = c; return c; });
  }

  // a Nakama call: its own message on a refusal ("Invalid credentials.", "User account banned.")
  function call(path, opts) {
    return getConfig().then(function (c) {
      return fetch(c.url + path, opts).then(function (r) {
        return r.text().then(function (t) {
          var body = null; try { body = t ? JSON.parse(t) : null; } catch (e) {}
          if (!r.ok) {
            var err = new Error((body && body.message) || ("The account server answered " + r.status + "."));
            err.status = r.status; throw err;
          }
          return body;
        });
      });
    });
  }
  function basic(c) { return { "Authorization": "Basic " + btoa(c.serverKey + ":"), "Content-Type": "application/json" }; }

  function authenticate(email, password, create, username) {
    return getConfig().then(function (c) {
      var q = "?create=" + (create ? "true" : "false") + (create && username ? "&username=" + encodeURIComponent(username) : "");
      return call("/v2/account/authenticate/email" + q, { method: "POST", headers: basic(c), body: JSON.stringify({ email: email, password: password }) });
    }).then(function (s) { save({ token: s.token, refresh: s.refresh_token }); return refreshMe(); });
  }

  // a token that still runs for a minute, renewed from the refresh token when not; null when signed out
  function token() {
    if (!session) return Promise.resolve(null);
    if (expiry(session.token) - Date.now() > 60000) return Promise.resolve(session.token);
    if (!session.refresh || expiry(session.refresh) < Date.now()) { save(null); return Promise.resolve(null); }
    return getConfig().then(function (c) {
      return call("/v2/account/session/refresh", { method: "POST", headers: basic(c), body: JSON.stringify({ token: session.refresh }) });
    }).then(function (s) { save({ token: s.token, refresh: s.refresh_token || session.refresh }); return s.token; },
      function () { save(null); return null; });
  }

  // an RPC of the module, as the signed-in player
  function rpc(id, payload) {
    return token().then(function (t) {
      if (!t) { var e = new Error("Sign in first."); e.status = 401; throw e; }
      return call("/v2/rpc/" + id + "?unwrap", {
        method: "POST", headers: { "Authorization": "Bearer " + t, "Content-Type": "application/json" },
        body: JSON.stringify(payload || {}),
      });
    });
  }

  function refreshMe() {
    return (session ? rpc("me", {}).catch(function (e) { if (e.status === 401) save(null); return null; }) : Promise.resolve(null))
      .then(function (m) { current = m; listeners.forEach(function (f) { f(m); }); render(); return m; });
  }

  function signOut() {
    var s = session;
    save(null); current = null;
    if (s) getConfig().then(function (c) {
      return fetch(c.url + "/v2/session/logout", { method: "POST", headers: { "Authorization": "Bearer " + s.token, "Content-Type": "application/json" },
        body: JSON.stringify({ token: s.token, refresh_token: s.refresh }) });
    }).catch(function () {});
    listeners.forEach(function (f) { f(null); });
    render();
  }

  // ---- the control and its dialog -----------------------------------------------------------------------------------
  var css = [
    ".acct{display:flex;align-items:center;gap:10px;font:500 14px system-ui,sans-serif;color:var(--text,#fbf4ee)}",
    ".acct button,.acct a{font:600 14px system-ui,sans-serif;color:var(--text,#fbf4ee);background:var(--glass,rgba(26,16,26,.55));",
    "border:1px solid var(--line,rgba(255,220,200,.14));border-radius:999px;padding:8px 14px;cursor:pointer;text-decoration:none}",
    ".acct button:hover,.acct a:hover{border-color:var(--glow,#ff7a45)}",
    ".acct a.dash{background:var(--glow,#ff7a45);color:#1a0d10;border-color:transparent}",
    ".acct .who{max-width:160px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--muted,#c2b3b0)}",
    "dialog.acct-dlg{border:1px solid var(--line,rgba(255,220,200,.14));border-radius:18px;padding:0;background:var(--bg2,#1a121c);",
    "color:var(--text,#fbf4ee);width:min(380px,calc(100vw - 32px));font:400 15px system-ui,sans-serif}",
    "dialog.acct-dlg::backdrop{background:rgba(8,4,10,.6)}",
    ".acct-dlg form{display:flex;flex-direction:column;gap:12px;padding:24px}",
    ".acct-dlg .tabs{display:flex;gap:6px}",
    ".acct-dlg .tabs button{flex:1;background:none;border:1px solid var(--line,rgba(255,220,200,.14));color:var(--muted,#c2b3b0);",
    "border-radius:10px;padding:8px;font:600 14px system-ui,sans-serif;cursor:pointer}",
    ".acct-dlg .tabs button[aria-pressed=true]{background:var(--glow,#ff7a45);color:#1a0d10;border-color:transparent}",
    ".acct-dlg label{display:flex;flex-direction:column;gap:4px;font-size:13px;color:var(--muted,#c2b3b0)}",
    ".acct-dlg [hidden]{display:none}",
    ".acct-dlg input{font:400 15px system-ui,sans-serif;padding:10px 12px;border-radius:10px;border:1px solid var(--line,rgba(255,220,200,.14));",
    "background:rgba(255,255,255,.05);color:var(--text,#fbf4ee)}",
    ".acct-dlg input:focus{outline:2px solid var(--glow,#ff7a45);outline-offset:1px}",
    ".acct-dlg .go{font:700 15px system-ui,sans-serif;padding:11px;border-radius:10px;border:none;background:var(--glow,#ff7a45);color:#1a0d10;cursor:pointer}",
    ".acct-dlg .go[disabled]{opacity:.6;cursor:wait}",
    ".acct-dlg .err{color:var(--rose,#e0567a);font-size:13px;min-height:1em}",
    ".acct-dlg .close{align-self:flex-end;background:none;border:none;color:var(--dim,#8d7e80);font-size:13px;cursor:pointer}",
    ".acct-dlg .linkish{align-self:flex-start;background:none;border:none;padding:0;color:var(--glow2,#ffb36b);font-size:13px;cursor:pointer;text-decoration:underline}",
    ".acct-dlg .hint{font-size:12px;color:var(--dim,#8d7e80)}",
  ].join("");

  var dialog = null, mode = "in";
  function buildDialog() {
    var d = document.createElement("dialog");
    d.className = "acct-dlg";
    d.innerHTML =
      '<form method="dialog" novalidate>' +
      '<div class="tabs" role="group" aria-label="Account"><button type="button" data-mode="in">Sign in</button><button type="button" data-mode="new">Create account</button></div>' +
      '<label>Email<input id="acct-email" name="email" type="email" autocomplete="email" required></label>' +
      '<label>Password<input id="acct-password" name="password" type="password" autocomplete="current-password" minlength="8" required></label>' +
      '<label class="newonly">Name in the game<input id="acct-name" name="name" type="text" maxlength="20" autocomplete="nickname" placeholder="Heidi_92"></label>' +
      '<div class="hint newonly">8 characters or more. The same account works in the game (Settings, Account).</div>' +
      '<button class="forgot linkish" type="button">Forgot your password?</button>' +
      '<div class="err" role="alert"></div>' +
      '<button class="go" type="submit">Sign in</button>' +
      '<button class="close" type="button">Close</button></form>';
    document.body.appendChild(d);
    var form = d.querySelector("form"), err = d.querySelector(".err"), go = d.querySelector(".go");
    function setMode(m) {
      mode = m;
      d.querySelectorAll(".tabs button").forEach(function (b) { b.setAttribute("aria-pressed", b.dataset.mode === m ? "true" : "false"); });
      d.querySelectorAll(".newonly").forEach(function (el) { el.hidden = m !== "new"; });
      // "forgot" (#1257): only the email, and a mailed link that opens reset.html
      d.querySelector("#acct-password").parentNode.hidden = m === "forgot";
      d.querySelector(".forgot").hidden = m !== "in";
      d.querySelectorAll(".tabs button").forEach(function (b) { b.hidden = m === "forgot"; });
      d.querySelector("#acct-password").autocomplete = m === "new" ? "new-password" : "current-password";
      go.textContent = m === "new" ? "Create account" : m === "forgot" ? "Email me a link" : "Sign in";
      err.textContent = ""; err.style.color = "";
    }
    d.querySelectorAll(".tabs button").forEach(function (b) { b.addEventListener("click", function () { setMode(b.dataset.mode); }); });
    d.querySelector(".close").addEventListener("click", function () { d.close(); });
    d.querySelector(".forgot").addEventListener("click", function () { setMode("forgot"); });
    form.addEventListener("submit", function (ev) {
      ev.preventDefault();
      var email = form.email.value.trim(), pw = form.password.value, name = form.name.value.trim();
      if (mode === "forgot") {
        if (!email) { err.textContent = "Type the email of your account."; return; }
        go.disabled = true; err.textContent = "";
        fetch("/api/password/forgot", { method: "POST", headers: { "Content-Type": "application/json", "X-Requested-With": "houses" }, body: JSON.stringify({ email: email }) })
          .then(function (r) { return r.json().catch(function () { return {}; }).then(function (j) { return { ok: r.ok, j: j }; }); })
          .then(function (x) {
            if (!x.ok) { err.textContent = x.j.error || "Could not send the email."; return; }
            err.style.color = "var(--ok,#4fe08a)"; err.textContent = "If an account has this email, a link to choose a new password is on its way (30 minutes).";
          }, function () { err.textContent = "The server does not answer."; })
          .then(function () { go.disabled = false; });
        return;
      }
      if (!email || pw.length < 8) { err.textContent = "An email and a password of 8 characters or more."; return; }
      if (mode === "new" && name && !/^[A-Za-z0-9_-]{2,20}$/.test(name)) { err.textContent = "The name: 2 to 20 letters, digits, - and _."; return; }
      go.disabled = true; err.textContent = "";
      authenticate(email, pw, mode === "new", mode === "new" ? name : "").then(function () {
        form.password.value = ""; d.close();
      }, function (e) {
        err.textContent = e.status === 409 ? "That name is taken: choose another." : e.message;
      }).then(function () { go.disabled = false; });
    });
    d.setMode = setMode;
    return d;
  }
  function open(m) { dialog = dialog || buildDialog(); dialog.setMode(m || "in"); dialog.showModal(); dialog.querySelector("#acct-email").focus(); }

  function render() {
    document.querySelectorAll("[data-account]").forEach(function (host) {
      host.className = "acct";
      host.textContent = "";
      if (current) {
        var who = document.createElement("span"); who.className = "who"; who.textContent = current.username; host.appendChild(who);
        if (current.admin) { var a = document.createElement("a"); a.className = "dash"; a.href = "admin.html"; a.textContent = "Dashboard"; host.appendChild(a); }
        var out = document.createElement("button"); out.type = "button"; out.textContent = "Sign out"; out.addEventListener("click", signOut); host.appendChild(out);
      } else {
        var b = document.createElement("button"); b.type = "button"; b.textContent = "Sign in"; b.addEventListener("click", function () { open("in"); }); host.appendChild(b);
      }
    });
  }

  var style = document.createElement("style"); style.textContent = css; document.head.appendChild(style);
  session = load();
  var ready = refreshMe();
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", render); else render();

  window.UnitSportAccount = {
    ready: ready,
    me: function () { return current; },
    onChange: function (f) { listeners.push(f); },
    rpc: rpc,
    token: token,
    signOut: signOut,
    open: open,
  };
})();
