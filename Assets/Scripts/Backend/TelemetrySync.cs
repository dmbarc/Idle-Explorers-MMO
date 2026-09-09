using System.Collections.Generic;
using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Sends the client's half of the telemetry, and — the reason this exists at all —
    /// every exception the player hits.
    ///
    /// ══ WHAT WAS MISSING ══════════════════════════════════════════════════════════
    ///
    /// The ingest endpoint was built, tested, given a feature flag, a batch cap, a
    /// payload cap, a reportable-event allow-list and a security row for anything
    /// outside it. Nothing in the game ever called it.
    ///
    /// So the server's own events — settles, boss fights, purchases — were being
    /// recorded, and everything that happens ON the client was invisible: which screens
    /// people open, where they go, and above all what breaks.
    ///
    /// ══ WHY THE ERROR HOOK IS THE POINT ═══════════════════════════════════════════
    ///
    /// A WebGL player who hits a null reference sees a game that stops responding. They
    /// do not open the browser console, they do not know what a stack trace is, and
    /// they will describe it as "it froze". The exception is sitting right there in
    /// their browser and nobody will ever read it.
    ///
    /// Application.logMessageReceived catches it wherever it was thrown, including
    /// inside Unity's own systems and inside code nobody thought to wrap. Posting it
    /// turns "it froze" into a stack trace with an account id against it.
    ///
    /// ══ WHY IT IS RATE-LIMITED RATHER THAN COMPLETE ═══════════════════════════════
    ///
    /// An exception thrown from Update is thrown sixty times a second. Reporting all of
    /// them would fill the table from one broken frame and drown the one occurrence
    /// anybody needed to see. The first few of each distinct message are what carry the
    /// information; the rest are the same line again.
    /// </summary>
    public class TelemetrySync : MonoBehaviour
    {
        private static TelemetrySync _instance;

        private readonly List<Telemetry.Event> _queue = new();

        /// <summary>How many times each distinct error message has been reported.</summary>
        private readonly Dictionary<string, int> _seen = new(System.StringComparer.Ordinal);

        private float _nextFlushAt;
        private bool  _flushing;

        /// <summary>
        /// How often the queue is sent.
        ///
        /// Slow on purpose. Telemetry is the least important traffic the game makes and
        /// must never compete with a settle or a presence poll; ten seconds is well
        /// inside the window where losing the tail of a session matters.
        /// </summary>
        private const float FlushSeconds = 10f;

        /// <summary>
        /// Most events held before the oldest are dropped.
        ///
        /// A client that cannot reach the server must not grow this without limit for
        /// the length of a session. Dropping the OLDEST keeps the most recent picture,
        /// which is the one that explains whatever is happening now.
        /// </summary>
        private const int MaxQueued = 200;

        /// <summary>Times one distinct error message is reported before it is muted.</summary>
        private const int MaxPerMessage = 3;

        /// <summary>Most events in one request. Matches the server's cap.</summary>
        private const int MaxBatch = 64;

        /// <summary>Longest stack trace sent. The server caps the whole payload at 4 KB.</summary>
        private const int MaxTraceLength = 2000;

        /// <summary>
        /// Starts reporting, once.
        ///
        /// On the managers object, like PresenceSync and ServerSync, so it lives as
        /// long as the game does — and so the error hook is installed for the whole
        /// session rather than for whichever scene happened to own it.
        /// </summary>
        public static void Attach(GameObject host)
        {
            if (_instance != null || host == null) return;

            _instance = host.AddComponent<TelemetrySync>();
        }

        private void OnEnable()  => Application.logMessageReceived += OnLog;
        private void OnDisable() => Application.logMessageReceived -= OnLog;

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        // ── What goes in ──────────────────────────────────────────────────────

        /// <summary>
        /// Records something worth knowing. Dropped silently when there is no server.
        ///
        /// Silently because telemetry is not something the player asked for: a toast
        /// saying "could not report your screen change" would be the instrumentation
        /// making itself the story.
        /// </summary>
        public static void Report(string name, params (string Key, string Value)[] fields)
        {
            if (_instance == null || string.IsNullOrEmpty(name)) return;

            _instance.Enqueue(name, fields);
        }

        private void Enqueue(string name, (string Key, string Value)[] fields)
        {
            var payload = new List<Telemetry.Field>();

            if (fields != null)
                foreach (var (key, value) in fields)
                    if (!string.IsNullOrEmpty(key))
                        payload.Add(new Telemetry.Field { k = key, v = value ?? "" });

            _queue.Add(new Telemetry.Event
            {
                @event      = name,
                characterId = ServerState.CharacterId ?? "",
                payload     = payload.ToArray(),
            });

            // Oldest first, so what remains is the most recent picture.
            if (_queue.Count > MaxQueued) _queue.RemoveRange(0, _queue.Count - MaxQueued);
        }

        /// <summary>
        /// Every error and exception Unity raises anywhere in the process.
        ///
        /// ══ WHY NOT WARNINGS ══════════════════════════════════════════════════
        ///
        /// This project logs warnings deliberately and often — a missing icon, a rig
        /// without a layer, a presence poll that blipped. They are useful in a console
        /// and they are noise in a table, and including them would mean the errors that
        /// matter arrive buried in a hundred of them.
        /// </summary>
        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error) return;
            if (string.IsNullOrEmpty(message)) return;

            // ══ THE SAME BREAK, SIXTY TIMES A SECOND ══════════════════════════
            //
            // An exception thrown from Update repeats every frame. The first few carry
            // everything; the rest would fill the table from one broken frame and bury
            // whatever else was happening.
            _seen.TryGetValue(message, out int already);

            if (already >= MaxPerMessage) return;

            _seen[message] = already + 1;

            string trace = stackTrace ?? "";

            if (trace.Length > MaxTraceLength) trace = trace[..MaxTraceLength];

            Enqueue("client_error", new[]
            {
                ("type",    type.ToString()),
                ("message", message.Length > 500 ? message[..500] : message),
                ("stack",   trace),
                ("map",     CharacterManager.Current?.lastMapId ?? ""),

                // Which occurrence this is, so a single report still says whether it
                // happened once or is happening continuously.
                ("count",   (already + 1).ToString()),
            });
        }

        // ── What goes out ─────────────────────────────────────────────────────

        private void Update()
        {
            if (_flushing || Time.unscaledTime < _nextFlushAt) return;

            _nextFlushAt = Time.unscaledTime + FlushSeconds;

            if (_queue.Count == 0) return;
            if (!ServerState.IsAuthoritative) return;

            _ = FlushAsync();
        }

        private async Awaitable FlushAsync()
        {
            _flushing = true;

            try
            {
                // Taken before awaiting: OnLog writes this list from anywhere, and
                // enumerating it across an await is a collection modified mid-iteration.
                int take = Mathf.Min(MaxBatch, _queue.Count);

                var batch = _queue.GetRange(0, take);
                _queue.RemoveRange(0, take);

                await GameBackend.Current.ReportTelemetryAsync(batch.ToArray());
            }
            catch (BackendException e)
            {
                // Dropped rather than retried, and quiet.
                //
                // Retrying telemetry means a client that cannot reach the server spends
                // the session trying to tell it so, and a queue that grows for as long
                // as the failure lasts. The events are worth having and they are not
                // worth one extra request during an outage.
                Debug.LogWarning($"[Telemetry] Dropped a batch: {e.Message}");
            }
            catch (System.Exception e)
            {
                // Deliberately broad, and deliberately NOT logged as an error.
                //
                // An error here would be caught by OnLog above, queued, and sent —
                // which fails again, logs again, and queues again. Instrumentation
                // must never be able to feed itself.
                Debug.LogWarning($"[Telemetry] Dropped a batch: {e.Message}");
            }
            finally
            {
                _flushing = false;
            }
        }
    }
}
