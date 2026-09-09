using System.Collections.Generic;
using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Keeps the world populated: says where we are, and draws everybody else.
    ///
    /// ══ WHY POLLING ═══════════════════════════════════════════════════════════════
    ///
    /// Unity WebGL has no usable socket. System.Net is excluded from the player, so
    /// ClientWebSocket is unavailable — and it fails by HANGING rather than by
    /// refusing to compile, which is the worst way for a transport to be missing.
    ///
    /// A real-time hub is worth building for a game where position decides something.
    /// Nothing here does: no loot, no rates, no combat advantage turns on where a
    /// character stands. So the cost of polling is that somebody's walk is a couple of
    /// seconds behind, and the benefit is a feature that works today on a transport
    /// that already exists.
    ///
    /// The seam is deliberate. The server's presence model does not know how it is
    /// read, so a socket later replaces this file and nothing else.
    ///
    /// ══ WHY IT RECONCILES RATHER THAN REBUILDS ════════════════════════════════════
    ///
    /// Each report is the whole truth about a map, so the naive version is to destroy
    /// every view and build them again. That would restart every walk animation twice
    /// a second and throw away the interpolation that makes movement look like
    /// movement.
    ///
    /// Instead: update the ones still here, spawn the new, destroy the departed.
    /// </summary>
    public class PresenceSync : MonoBehaviour
    {
        /// <summary>The one instance. Attached to the managers object, like ServerSync.</summary>
        private static PresenceSync _instance;

        private readonly Dictionary<string, RemotePlayerView> _views = new();

        private float      _nextReportAt;
        private bool       _busy;
        private GameObject _localRig;

        /// <summary>
        /// What we said and have not yet sent.
        ///
        /// Queued rather than sent immediately, so a line rides the next presence poll
        /// -- one round trip for position and speech, at the same rate, about the same
        /// map. At worst it is two seconds late, which is a conversation rather than a
        /// problem.
        /// </summary>
        private static readonly Queue<string> _pending = new();

        /// <summary>
        /// The last line drawn, per speaker.
        ///
        /// Every poll returns a WINDOW of recent chat, so the same line comes back
        /// several times. Without this, a bubble would be re-raised on every poll and
        /// never fade.
        /// </summary>
        private readonly Dictionary<string, long> _lastDrawn = new();

        /// <summary>Everybody currently drawn. Read by the inspect panel and the group screen.</summary>
        public static IReadOnlyCollection<RemotePlayerView> Others =>
            _instance != null ? _instance._views.Values : System.Array.Empty<RemotePlayerView>();

        /// <summary>Finds a drawn player by id, or null.</summary>
        public static RemotePlayerView Find(string characterId) =>
            _instance != null && characterId != null &&
            _instance._views.TryGetValue(characterId, out var view) ? view : null;

        /// <summary>
        /// Starts reporting, once.
        ///
        /// Attached to the managers object so it lives exactly as long as they do —
        /// the same arrangement as ServerSync, and for the same reason: a component on
        /// a map object would stop reporting the moment somebody travelled.
        /// </summary>
        public static void Attach(GameObject host)
        {
            if (_instance != null || host == null) return;

            _instance = host.AddComponent<PresenceSync>();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            if (_busy || Time.time < _nextReportAt) return;

            // ══ A DISPLACED CLIENT STOPS TALKING ═══════════════════════════════
            //
            // It cannot win the account back by asking again -- the other browser
            // holds it -- so retrying every two seconds is a 409 on a timer for as
            // long as the tab is open. It also keeps a stale body standing in
            // everybody else's world.
            if (ServerState.Displaced)
            {
                if (_views.Count > 0) Clear();
                enabled = false;
                return;
            }

            if (!ServerState.IsAuthoritative || string.IsNullOrEmpty(ServerState.CharacterId))
            {
                // Offline. Anything left over is from a session that had a server.
                if (_views.Count > 0) Clear();
                return;
            }

            _nextReportAt = Time.time + (float)Rules.Presence.ReportSeconds;

            _ = ReportAsync();
        }

        private async Awaitable ReportAsync()
        {
            _busy = true;

            try
            {
                GameObject rig = LocalRig();

                // No rig means no map — character select, or a scene still loading.
                // Nothing to report and nobody to draw.
                if (rig == null)
                {
                    if (_views.Count > 0) Clear();
                    return;
                }

                string mapId = CharacterManager.Current?.lastMapId ?? "";

                if (string.IsNullOrEmpty(mapId)) return;

                Vector3 here = rig.transform.position;

                string say = _pending.Count > 0 ? _pending.Dequeue() : "";

                PresenceSnapshot seen = await GameBackend.Current.ReportPresenceAsync(
                    ServerState.CharacterId, mapId, here.x, here.z, say);

                if (seen == null) return;

                Reconcile(rig, seen.others);
                Speak(rig, seen.chat);

                // ══ AND THE GROUP ═════════════════════════════════════════════
                //
                // Raised rather than acted on. This file knows how to READ the world;
                // what a throne call means is the group panel and the countdown
                // modal is business, and putting either here would make a transport
                // file responsible for a piece of UI.
                //
                // Fired even when there is no party, because "you are no longer in a
                // group" is news the panel has to hear too.
                GameEvents.FirePartyChanged(seen.party);


                // The world, from the same answer. MonsterSync draws it; this file
                // stays the one place that talks to the presence endpoint, so a
                // socket later replaces exactly one file.
                MonsterSync.Adopt(seen.monsters);

                // Only ONE queued line rides each report. Somebody who typed three
                // in a row would otherwise wait six seconds for the last of them, so
                // a backlog drains at network speed rather than on the clock.
                if (_pending.Count > 0) _nextReportAt = 0f;
            }
            catch (BackendException e)
            {
                // Displacement is the exception to the quiet: it is permanent, and the
                // loop has to be told to stop rather than keep asking.
                ServerState.NoteIfDisplaced(e);

                // Otherwise quiet. This runs on a timer, the next one is two seconds
                // away, and a toast every two seconds during a blip is worse than the
                // blip.
                Debug.LogWarning($"[PresenceSync] {e.Message}");
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// Brings the drawn set into line with the reported one.
        /// </summary>
        private void Reconcile(GameObject rig, RemotePlayer[] reported)
        {
            reported ??= System.Array.Empty<RemotePlayer>();

            var present = new HashSet<string>();

            foreach (RemotePlayer other in reported)
            {
                if (other == null || string.IsNullOrEmpty(other.characterId)) continue;

                // Never draw ourselves. The server already excludes us, but a stale
                // row from a previous character on the same account would arrive as a
                // twin standing where we are, which is a memorable bug to ship.
                if (other.characterId == ServerState.CharacterId) continue;

                present.Add(other.characterId);

                if (_views.TryGetValue(other.characterId, out var view) && view != null)
                {
                    view.Apply(other);
                    continue;
                }

                RemotePlayerView spawned = RemotePlayerView.Spawn(rig, other);

                if (spawned != null) _views[other.characterId] = spawned;
            }

            // Gone: logged out, travelled, or simply stopped reporting for long enough
            // that the server no longer believes them.
            var departed = new List<string>();

            foreach (var pair in _views)
                if (!present.Contains(pair.Key)) departed.Add(pair.Key);

            foreach (string id in departed) Remove(id);
        }

        /// <summary>
        /// Queues something to say. It goes out with the next position report.
        /// </summary>
        public static void Say(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            // Bounded, so a client that queues faster than it polls cannot grow this
            // without limit -- and because nobody needs a backlog of their own
            // shouting delivered a minute later.
            if (_pending.Count >= 8) _pending.Dequeue();

            _pending.Enqueue(message.Trim());

            // ══ SAID NOW, NOT IN UP TO TWO SECONDS ════════════════════════════
            //
            // A position report can wait for its turn; a person cannot. Waiting for
            // the timer meant your own line appeared in your own log anything up to
            // two seconds after you pressed Enter, and a second line typed behind it
            // waited another two -- so a fast exchange arrived in slow motion, out of
            // step with the fight it was about. That is most of what "the chat log is
            // not synced to what is actually going on" was.
            //
            // Bringing the next report forward rather than sending separately keeps
            // the one-round-trip design: speech still rides a position report, it just
            // rides the next one immediately instead of the next one on the clock.
            if (_instance != null) _instance._nextReportAt = 0f;
        }

        /// <summary>
        /// Raises a bubble over whoever said something, ourselves included.
        ///
        /// ══ WHY OUR OWN LINE COMES BACK FROM THE SERVER ══════════════════════
        ///
        /// Rather than being drawn the moment it is typed. One code path draws every
        /// bubble, so our view of a conversation cannot drift from the view other
        /// people have of it -- and if a line never reaches the server, we do not see
        /// it either, which is the honest outcome.
        /// </summary>
        private void Speak(GameObject rig, ChatLine[] lines)
        {
            if (lines == null) return;

            foreach (ChatLine line in lines)
            {
                if (line == null || string.IsNullOrEmpty(line.body)) continue;

                // The window returns the same line on every poll until it ages out.
                if (_lastDrawn.TryGetValue(line.characterId, out long seen) && seen >= line.id)
                    continue;

                _lastDrawn[line.characterId] = line.id;

                Transform speaker = line.characterId == ServerState.CharacterId
                    ? rig.transform
                    : Find(line.characterId)?.transform;

                if (speaker != null) ChatBubble.Say(speaker, line.body);

                // Into the log as well, so somebody who was looking away can read back.
                ChatLog.Say($"{line.name}: {line.body}", ChatTone.Local);
            }
        }

        private void Remove(string characterId)
        {
            if (_views.TryGetValue(characterId, out var view) && view != null)
                Destroy(view.gameObject);

            _views.Remove(characterId);
        }

        private void Clear()
        {
            foreach (var view in _views.Values)
                if (view != null) Destroy(view.gameObject);

            _views.Clear();
        }

        /// <summary>
        /// The local player object, cached until it goes away.
        ///
        /// Re-found rather than held across maps: travelling destroys the old scene and
        /// the reference with it, and a stale one is a null every two seconds.
        /// </summary>
        private GameObject LocalRig()
        {
            if (_localRig == null) _localRig = GameObject.Find("PlayerCharacter");

            return _localRig;
        }
    }
}
