using System;
using System.Collections.Generic;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// Armour that has left the character but is expected back.
    ///
    /// ══ THE BUG THIS EXISTS TO FIX ════════════════════════════════════════════════
    ///
    /// The tin set has a 4-piece bonus that hurls a worn piece at the enemy, and a
    /// 6-piece bonus that scoops thrown armour back up. Both are counted by asking how
    /// many pieces are currently WORN -- so the instant the throw lands, the count
    /// drops from six to five and every 6-piece bonus switches off. Including the one
    /// whose entire job is fetching the piece back.
    ///
    /// The set disarms itself on its own proc, permanently, and takes the durability
    /// siphon with it. Nothing in the game says so; it simply stops working.
    ///
    /// ══ WHY A LEDGER RATHER THAN A SPECIAL CASE ═══════════════════════════════════
    ///
    /// Making armorThrow not decrement the count would fix this set. It would not fix
    /// the next bonus that temporarily removes gear -- and the whole shape of this
    /// game invites them. A piece in flight is a real state the character can be in,
    /// so it is worth a real name and a real record.
    ///
    /// ══ WHY THE GRACE WINDOW EXPIRES ══════════════════════════════════════════════
    ///
    /// Counting an in-flight piece forever would be a free set bonus: throw one, walk
    /// away, keep the six-piece for the rest of your life. Forty-five seconds is long
    /// enough to fight through the proc and pick the piece up, and short enough that
    /// abandoning it costs you the bonus.
    ///
    /// ══ NO CLOCK ══════════════════════════════════════════════════════════════════
    ///
    /// "Now" is an argument, as everywhere in this tree. The client passes its play
    /// clock; the server passes the database one. The alternative -- reading a clock
    /// in here -- would make the grace window untestable except by waiting.
    /// </summary>
    public sealed class InFlightPieces
    {
        /// <summary>How long a thrown piece keeps counting as worn.</summary>
        public const double GraceSeconds = 45d;

        private readonly Dictionary<string, Entry> _byItemId = new Dictionary<string, Entry>();

        private struct Entry
        {
            public string SlotId;
            public double ThrownAt;
        }

        /// <summary>How many pieces are currently out. Diagnostic; nothing gates on it.</summary>
        public int Count => _byItemId.Count;

        /// <summary>
        /// Notes that a piece has left the character.
        ///
        /// Keyed by item id rather than by slot, because the question asked later is
        /// "is this set still complete", and a set is a list of item ids. The slot is
        /// kept so the piece can be put back where it came from.
        /// </summary>
        public void Record(string itemId, string slotId, double nowSeconds)
        {
            if (string.IsNullOrEmpty(itemId)) return;

            _byItemId[itemId] = new Entry { SlotId = slotId, ThrownAt = nowSeconds };
        }

        /// <summary>
        /// Whether this piece is out and still inside its grace window.
        ///
        /// The caller must already know the piece is not worn. Checking that here
        /// would need the equipment paperdoll, which this cannot see and should not:
        /// keeping the two questions separate is what stops a re-equipped piece being
        /// counted twice, once as worn and once as in flight.
        /// </summary>
        public bool IsInFlight(string itemId, double nowSeconds)
        {
            if (string.IsNullOrEmpty(itemId)) return false;
            if (!_byItemId.TryGetValue(itemId, out Entry entry)) return false;

            return nowSeconds - entry.ThrownAt <= GraceSeconds;
        }

        /// <summary>The slot a piece came off, or null if it is not recorded.</summary>
        public string SlotOf(string itemId) =>
            !string.IsNullOrEmpty(itemId) && _byItemId.TryGetValue(itemId, out Entry entry)
                ? entry.SlotId
                : null;

        /// <summary>Forgets a piece: scavenged, re-equipped by hand, or written off.</summary>
        public bool Clear(string itemId) =>
            !string.IsNullOrEmpty(itemId) && _byItemId.Remove(itemId);

        /// <summary>
        /// Drops every entry past its grace window.
        ///
        /// Not required for correctness -- IsInFlight already refuses a lapsed entry --
        /// but a character who throws armour for an hour and never picks it up should
        /// not accumulate a record per piece for the session.
        /// </summary>
        public void Prune(double nowSeconds)
        {
            List<string> lapsed = null;

            foreach (var pair in _byItemId)
            {
                if (nowSeconds - pair.Value.ThrownAt <= GraceSeconds) continue;

                lapsed ??= new List<string>();
                lapsed.Add(pair.Key);
            }

            if (lapsed == null) return;

            foreach (string itemId in lapsed)
                _byItemId.Remove(itemId);
        }

        /// <summary>Forgets everything. For a character switch, or a fresh session.</summary>
        public void Clear() => _byItemId.Clear();
    }
}
