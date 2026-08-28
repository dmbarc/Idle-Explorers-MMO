using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// The clock that keeps the client's copy honest.
    ///
    /// ══ TWO TIMERS, TWO DIFFERENT JOBS ════════════════════════════════════════════
    ///
    /// The HEARTBEAT says "somebody is watching". It is the only evidence the server
    /// has for the difference between a player at the keyboard and a tab left open, and
    /// that difference is worth about 1.6x on current rates. Nothing else is claimed by
    /// it -- it asserts presence by arriving, so there is nothing to send and nothing
    /// to forge beyond the arrival itself.
    ///
    /// The SETTLE asks what the elapsed time earned. It is idempotent by construction,
    /// because the answer is a function of the interval between two server timestamps:
    /// calling it twice in an instant pays nothing the second time. That property is
    /// the entire reason this can be a dumb timer with no bookkeeping.
    ///
    /// ══ WHY SETTLING IS SLOWER THAN THE HEARTBEAT ═════════════════════════════════
    ///
    /// A settle writes rows and takes the character lock; a heartbeat touches one
    /// column. Settling on the heartbeat's cadence would triple the write load for a
    /// progress bar that is already animating smoothly from the shared rules.
    ///
    /// ══ WHY THIS EXISTS AT ALL RATHER THAN SETTLING ON DEMAND ═════════════════════
    ///
    /// Because the numbers on screen have to move. Settlement is exact whenever it is
    /// called, so correctness never needed a timer -- but a player who mines for ten
    /// minutes and sees nothing until they open a panel has, as far as they can tell,
    /// been mining nothing.
    /// </summary>
    public class ServerSync : MonoBehaviour
    {
        /// <summary>
        /// Seconds between heartbeats.
        ///
        /// Twenty, matching the window the server counts as supervised. Slower and a
        /// player at the keyboard would drop in and out of the active rate; faster
        /// buys nothing, since the server rounds to the same window either way.
        /// </summary>
        public const float HeartbeatSeconds = 20f;

        /// <summary>
        /// Seconds between settles.
        /// </summary>
        public const float SettleSeconds = 30f;

        private float _nextHeartbeat;
        private float _nextSettle;
        private bool  _busy;

        /// <summary>
        /// Attaches to whatever object should own it, once.
        ///
        /// Idempotent, because the bootstrap scene is loaded once but this is the kind
        /// of thing that acquires a second caller -- and two sync loops would double
        /// every heartbeat and race their own settles.
        /// </summary>
        public static ServerSync Attach(GameObject host)
        {
            if (host == null) return null;

            return host.GetComponent<ServerSync>() ?? host.AddComponent<ServerSync>();
        }

        private void Update()
        {
        // A displaced client must not keep settling: the account is being played
        // somewhere else, and every request from here is a 409 on a timer.
        if (IdleExplorers.Backend.ServerState.Displaced) { enabled = false; return; }

            // Nothing at all offline or in shadow mode. The local managers are the game
            // in both, and settling against a server whose answers are not used would
            // write rows nobody reads.
            if (!ServerState.IsAuthoritative) return;

            if (CharacterManager.Current == null) return;

            if (Time.time >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.time + HeartbeatSeconds;
                Beat();
            }

            if (Time.time >= _nextSettle)
            {
                _nextSettle = Time.time + SettleSeconds;
                Settle();
            }
        }

        private async void Beat()
        {
            await ServerState.HeartbeatAsync();
        }

        /// <summary>
        /// Settles, unless one is already in flight.
        ///
        /// The guard matters: a slow settle on a bad connection would otherwise have a
        /// second one queued behind it, both taking the character lock, and the second
        /// paying nothing while holding the row. Skipping is free -- the elapsed time
        /// it did not claim is still there thirty seconds later.
        /// </summary>
        private async void Settle()
        {
            if (_busy) return;

            _busy = true;

            try   { await ServerState.SettleAsync(); }
            finally { _busy = false; }
        }

        /// <summary>
        /// One last settle on the way out.
        ///
        /// Not required for correctness -- the next login settles the whole gap from
        /// last_settled_at, so nothing is ever lost. It is here so a player who stops
        /// playing and looks at their character list sees the run they just finished,
        /// rather than numbers from ten minutes ago.
        /// </summary>
        private void OnApplicationQuit()
        {
            if (!ServerState.IsAuthoritative) return;

            // Fire and forget. Unity will not wait for this, and blocking a quit on a
            // network call is how an application stops responding while closing.
            _ = ServerState.SettleAsync();
        }

        /// <summary>
        /// Settles when the tab comes back.
        ///
        /// A backgrounded browser tab stops calling Update, so the timers above stop
        /// with it. Without this, a player who alt-tabs for an hour returns to a screen
        /// showing the state from before they left, and has to wait another thirty
        /// seconds to see the hour they earned.
        /// </summary>
        private void OnApplicationFocus(bool focused)
        {
            if (!focused || !ServerState.IsAuthoritative) return;

            _nextSettle    = Time.time;
            _nextHeartbeat = Time.time;
        }
    }
}
