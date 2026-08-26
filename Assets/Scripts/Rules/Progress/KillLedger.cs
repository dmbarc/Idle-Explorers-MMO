using System;
using System.Collections.Generic;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// How many of a thing a character has killed, split by whether anyone was watching.
    ///
    /// ══ WHY THE SPLIT ═════════════════════════════════════════════════════════════
    ///
    /// The boss portal opens on a thousand goblins, and it counts ACTIVE kills only.
    /// Not because AFK kills are cheating -- the whole game is built on them -- but
    /// because a gate an idle character walks through on its own is not a gate. The
    /// thousandth goblin should be a thing the player did.
    ///
    /// Both halves are kept, because the AFK number is still worth showing: a portal
    /// reading "412 / 1000" beside a character who has killed nine thousand goblins in
    /// their sleep needs to explain itself, and it can only do that if it knows.
    ///
    /// ══ WHY THIS IS NOT A DICTIONARY ══════════════════════════════════════════════
    ///
    /// The client mirror of this is serialised by JsonUtility, which cannot write a
    /// Dictionary -- it produces an empty object, without warning or error. A save
    /// written that way loses every kill count silently. So the storage is a list,
    /// and the index is built at runtime.
    ///
    /// ══ WHERE THE NUMBERS COME FROM ═══════════════════════════════════════════════
    ///
    /// Settlement, and nowhere else. There is no "I killed a goblin" call: that would
    /// make the client the author of kills, which is the whole thing being removed.
    /// SettlementResult carries Actions and SupervisedActions out of one integral, and
    /// both land here together.
    /// </summary>
    public sealed class KillLedger
    {
        /// <summary>One monster's tally. A class, not a struct, so it can be edited in place.</summary>
        [Serializable]
        public sealed class Tally
        {
            public string monsterId;
            public long   activeKills;
            public long   afkKills;

            public long Total => Sum(activeKills, afkKills);
        }

        private readonly List<Tally> _rows = new List<Tally>();
        private readonly Dictionary<string, Tally> _index = new Dictionary<string, Tally>();

        /// <summary>Every tally, in first-killed order. This is what gets persisted.</summary>
        public IReadOnlyList<Tally> Rows => _rows;

        /// <summary>Rebuilds the index after loading rows from storage.</summary>
        public void Load(IEnumerable<Tally> rows)
        {
            _rows.Clear();
            _index.Clear();

            if (rows == null) return;

            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.monsterId)) continue;

                // Two rows for one monster would each be half a tally, and which one
                // the gate read would depend on iteration order. Fold instead.
                if (_index.TryGetValue(row.monsterId, out Tally existing))
                {
                    existing.activeKills = Sum(existing.activeKills, row.activeKills);
                    existing.afkKills    = Sum(existing.afkKills,    row.afkKills);
                    continue;
                }

                var copy = new Tally
                {
                    monsterId   = row.monsterId,
                    activeKills = Math.Max(0L, row.activeKills),
                    afkKills    = Math.Max(0L, row.afkKills),
                };

                _rows.Add(copy);
                _index[copy.monsterId] = copy;
            }
        }

        /// <summary>
        /// Credits one settlement window.
        /// </summary>
        /// <param name="monsterId">What was being fought.</param>
        /// <param name="kills">Total kills the window produced.</param>
        /// <param name="supervisedKills">
        /// How many of those happened while someone was watching. Clamped to the total,
        /// because a supervised count above the total could only be a caller bug, and
        /// the one it would inflate is the boss gate.
        /// </param>
        public void Credit(string monsterId, long kills, long supervisedKills)
        {
            if (string.IsNullOrEmpty(monsterId) || kills <= 0L) return;

            long active = RulesMath.Clamp(supervisedKills, 0L, kills);
            long afk    = kills - active;

            var tally = Row(monsterId);
            tally.activeKills = Sum(tally.activeKills, active);
            tally.afkKills    = Sum(tally.afkKills,    afk);
        }

        /// <summary>Credits a settlement result directly, so the two cannot disagree.</summary>
        public void Credit(string monsterId, SettlementResult result)
        {
            if (result == null) return;

            Credit(monsterId, result.Actions, result.SupervisedActions);
        }

        public long ActiveKills(string monsterId) => Get(monsterId)?.activeKills ?? 0L;
        public long AfkKills(string monsterId)    => Get(monsterId)?.afkKills    ?? 0L;
        public long TotalKills(string monsterId)  => Get(monsterId)?.Total       ?? 0L;

        /// <summary>
        /// Whether a gate requiring this many ACTIVE kills is open.
        ///
        /// One function rather than a comparison at each call site, because the portal
        /// UI, the travel check and the server's engage endpoint all have to agree
        /// about it, and "&gt;=" versus "&gt;" is exactly the kind of thing that ends up
        /// answered differently in three places.
        /// </summary>
        public bool GateOpen(string monsterId, long requiredActiveKills) =>
            ActiveKills(monsterId) >= Math.Max(0L, requiredActiveKills);

        /// <summary>How many more active kills a gate needs. Zero once it is open.</summary>
        public long Remaining(string monsterId, long requiredActiveKills) =>
            Math.Max(0L, Math.Max(0L, requiredActiveKills) - ActiveKills(monsterId));

        private Tally Get(string monsterId) =>
            !string.IsNullOrEmpty(monsterId) && _index.TryGetValue(monsterId, out Tally row)
                ? row
                : null;

        private Tally Row(string monsterId)
        {
            if (_index.TryGetValue(monsterId, out Tally existing)) return existing;

            var created = new Tally { monsterId = monsterId };
            _rows.Add(created);
            _index[monsterId] = created;

            return created;
        }

        /// <summary>
        /// Saturating addition.
        ///
        /// A kill count that wraps to negative would close a gate the player had
        /// already opened, and no amount of further play would reopen it.
        /// </summary>
        private static long Sum(long a, long b)
        {
            if (a < 0L) a = 0L;
            if (b < 0L) b = 0L;

            return a > long.MaxValue - b ? long.MaxValue : a + b;
        }
    }
}
