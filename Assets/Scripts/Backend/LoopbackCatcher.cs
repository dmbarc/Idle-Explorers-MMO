#if !UNITY_WEBGL || UNITY_EDITOR

using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Catches the browser coming back after a sign-in, on the desktop.
    ///
    /// ══ WHY A LOCAL WEB SERVER ════════════════════════════════════════════════════
    ///
    /// An OAuth provider finishes by redirecting a BROWSER somewhere. In a web game
    /// that somewhere is the page itself; on a desktop there is no page, so the app
    /// briefly becomes a web server on loopback and the redirect lands there.
    ///
    /// This is the standard native OAuth pattern, and it is the reason PKCE is not
    /// optional here: anything else running on the machine could try to answer on the
    /// same port, so the code that arrives has to be useless without a verifier that
    /// never left this process.
    ///
    /// ══ WHY A FIXED PORT ══════════════════════════════════════════════════════════
    ///
    /// Because the redirect URL has to be registered in Supabase's allow-list ahead of
    /// time, and an allow-list cannot contain a port chosen at random. The cost is that
    /// sign-in fails if something else holds the port -- which is reported plainly
    /// rather than hanging, since a sign-in button that does nothing is the worst
    /// possible symptom.
    ///
    /// ══ NOT COMPILED FOR WEBGL ════════════════════════════════════════════════════
    ///
    /// System.Net is excluded from that build entirely. The web path redirects the page
    /// and reads the code back off its own URL -- see GoogleSignIn.
    /// </summary>
    public sealed class LoopbackCatcher : IDisposable
    {
        /// <summary>
        /// The port the browser is sent back to.
        ///
        /// High, fixed, and unlikely to collide with a dev server. Whatever it is, it
        /// has to match the redirect URL registered with Supabase exactly.
        /// </summary>
        public const int Port = 54391;

        public static string RedirectUri => $"http://localhost:{Port}/";

        /// <summary>
        /// How long to wait for somebody to finish signing in.
        ///
        /// Three minutes. Long enough to find a password manager and a second factor,
        /// short enough that an abandoned attempt releases the port rather than holding
        /// it until the game closes.
        /// </summary>
        public static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

        private readonly HttpListener _listener = new();

        public LoopbackCatcher()
        {
            _listener.Prefixes.Add(RedirectUri);
        }

        /// <summary>
        /// Starts listening. Returns false when the port is unavailable.
        ///
        /// False rather than an exception, because "something else is using the port"
        /// is an ordinary condition on a developer's machine and the caller wants to
        /// say so in the UI rather than log a stack trace nobody reads.
        /// </summary>
        public bool Start()
        {
            try
            {
                _listener.Start();
                return true;
            }
            catch (HttpListenerException e)
            {
                Debug.LogWarning($"[OAuth] Could not listen on {RedirectUri}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Waits for the redirect and returns the authorisation code.
        ///
        /// Empty when it timed out, or when the provider redirected with an error
        /// instead -- which happens when somebody presses cancel, and is not a failure
        /// worth a stack trace either.
        /// </summary>
        public async Task<string> AwaitCodeAsync()
        {
            using var deadline = new CancellationTokenSource(Patience);

            try
            {
                Task<HttpListenerContext> incoming = _listener.GetContextAsync();

                // GetContextAsync ignores cancellation, so the timeout is a race
                // against it rather than a token passed in. Losing the race leaves the
                // listener task orphaned, which Dispose then tears down.
                Task finished = await Task.WhenAny(
                    incoming, Task.Delay(Patience, deadline.Token));

                if (finished != incoming)
                {
                    Debug.LogWarning("[OAuth] Timed out waiting for the browser.");
                    return "";
                }

                HttpListenerContext context = await incoming;

                string code  = context.Request.QueryString["code"] ?? "";
                string error = context.Request.QueryString["error"] ?? "";

                // ══ WHY THE BROWSER GETS A REAL PAGE ══════════════════════════
                //
                // Whatever happens, the tab the player is looking at shows something
                // that tells them to go back to the game. A blank page or a connection
                // error after signing in reads as "it did not work", and they try
                // again -- which fails, because the code has already been used.
                Respond(context, error.Length > 0
                    ? "Sign-in was cancelled. You can close this tab."
                    : "Signed in. You can close this tab and return to the game.");

                if (error.Length > 0)
                {
                    Debug.Log($"[OAuth] Provider returned '{error}'.");
                    return "";
                }

                return code;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[OAuth] Waiting for the redirect failed: {e.Message}");
                return "";
            }
        }

        private static void Respond(HttpListenerContext context, string message)
        {
            try
            {
                // Deliberately a bare, self-contained page: no fonts, no scripts, no
                // requests to anywhere. It renders instantly and offline, and it cannot
                // leak the URL it was reached by to a third party.
                byte[] body = Encoding.UTF8.GetBytes(
                    "<!doctype html><meta charset=\"utf-8\">" +
                    "<title>Idle Explorers</title>" +
                    "<body style=\"font:16px system-ui;padding:3rem;background:#12151f;color:#e8e8ea\">" +
                    $"<p>{message}</p></body>");

                context.Response.ContentType     = "text/html; charset=utf-8";
                context.Response.ContentLength64 = body.Length;

                context.Response.OutputStream.Write(body, 0, body.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception)
            {
                // The browser may already have given up. The code is what mattered and
                // it has been read; failing to draw a courtesy page is not a failure.
            }
        }

        public void Dispose()
        {
            try   { _listener.Close(); }
            catch (Exception) { /* already closed, or never opened */ }
        }
    }
}

#endif
