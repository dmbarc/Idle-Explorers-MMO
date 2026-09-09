using System;
using System.Collections.Generic;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Deciding whether two answers are the same answer.
    ///
    /// ══ WHAT COUNTS AS A DIVERGENCE, AND WHAT DOES NOT ════════════════════════════
    ///
    /// This is the whole difficulty of shadow mode. Compare too strictly and every run
    /// is a wall of noise nobody reads; compare too loosely and the off-by-one it
    /// exists to catch slips through.
    ///
    /// The rules applied here:
    ///
    ///   · Ids differ legitimately. Two backends creating a character assign two
    ///     GUIDs, and that is not a bug.
    ///
    ///   · Anything ROLLED differs legitimately. The two use different seeds, so a
    ///     bird's nest that dropped for one and not the other says nothing. Rolled
    ///     quantities are compared as "both present" rather than as equal.
    ///
    ///   · Anything DERIVED must match exactly. Action counts, experience, levels,
    ///     kill counts, gate state. These come from the shared rules, so a difference
    ///     is either a genuine formula divergence or a difference in what the two were
    ///     told -- and both are worth a look.
    ///
    ///   · Elapsed time is compared loosely. The two ran their clocks moments apart,
    ///     and a settlement window differing by a fraction of a second is arithmetic,
    ///     not disagreement.
    /// </summary>
    public static class Compare
    {
        /// <summary>
        /// How far apart two elapsed-time figures may be.
        ///
        /// Generous, because the local and remote calls happen sequentially and a slow
        /// request genuinely does make the server's window longer. What matters is
        /// whether they are measuring the same THING, not the same instant.
        /// </summary>
        public const double SecondsTolerance = 2.0;

        public static List<string> Accounts(AccountSnapshot local, AccountSnapshot remote)
        {
            var differences = new List<string>();
            if (local == null || remote == null) return Missing(local, remote, differences);

            // accountId is not compared: two backends legitimately identify an account
            // differently, and the local one has no Supabase user at all.

            Longs(differences, "accountXp", local.accountXp, remote.accountXp);
            Ints(differences, "characters", Count(local.characters), Count(remote.characters));

            foreach (string currency in new[] { "coins", "relic_coins" })
                Longs(differences, $"wallet.{currency}", local.BalanceOf(currency), remote.BalanceOf(currency));

            return differences;
        }

        public static List<string> Characters(CharacterSnapshot local, CharacterSnapshot remote)
        {
            var differences = new List<string>();
            if (local == null || remote == null) return Missing(local, remote, differences);

            Longs(differences, "xp", local.xp, remote.xp);
            Ints(differences, "level", local.level, remote.level);

            // Every skill either side knows about, so a skill missing entirely on one
            // is a divergence rather than an absence nobody notices.
            foreach (string skillId in Union(SkillIds(local), SkillIds(remote)))
            {
                Longs(differences, $"skill.{skillId}.xp",
                      SkillXp(local, skillId), SkillXp(remote, skillId));
            }

            foreach (string itemId in Union(ItemIds(local), ItemIds(remote)))
            {
                Longs(differences, $"inventory.{itemId}",
                      Held(local, itemId), Held(remote, itemId));
            }

            foreach (string monsterId in Union(MonsterIds(local), MonsterIds(remote)))
            {
                Longs(differences, $"kills.{monsterId}.active",
                      ActiveKills(local, monsterId), ActiveKills(remote, monsterId));
            }

            return differences;
        }

        public static List<string> Settlements(SettlementSnapshot local, SettlementSnapshot remote)
        {
            var differences = new List<string>();
            if (local == null || remote == null) return Missing(local, remote, differences);

            // The integral itself. These MUST match: same rules, same window.
            Longs(differences, "actions", local.actions, remote.actions);
            Longs(differences, "supervisedActions", local.supervisedActions, remote.supervisedActions);
            Longs(differences, "xpGained", local.xpGained, remote.xpGained);

            Seconds(differences, "elapsedSeconds", local.elapsedSeconds, remote.elapsedSeconds);

            if (local.stoppedForRoom != remote.stoppedForRoom)
                differences.Add($"stoppedForRoom local={local.stoppedForRoom} remote={remote.stoppedForRoom}");

            if (local.ranOutOfInputs != remote.ranOutOfInputs)
                differences.Add($"ranOutOfInputs local={local.ranOutOfInputs} remote={remote.ranOutOfInputs}");

            // ── Items, carefully ──────────────────────────────────────────────
            //
            // A gathered quantity equals the action count and must match. A ROLLED
            // one -- a special find, a loot table entry -- comes from two different
            // seeds and legitimately differs. Comparing those for equality would
            // bury every real finding under noise, so they are compared for
            // PRESENCE: both dropped it, or neither did.
            foreach (string itemId in Union(StackIds(local), StackIds(remote)))
            {
                long here  = local.QuantityOf(itemId);
                long there = remote.QuantityOf(itemId);

                if (here > 0 == there > 0) continue;

                differences.Add($"item.{itemId} local={here} remote={there}");
            }

            foreach (string currency in new[] { "coins", "relic_coins" })
            {
                long here  = local.EarnedOf(currency);
                long there = remote.EarnedOf(currency);

                if (here > 0 == there > 0) continue;

                differences.Add($"currency.{currency} local={here} remote={there}");
            }

            return differences;
        }

        public static List<string> Gates(BossGateSnapshot local, BossGateSnapshot remote)
        {
            var differences = new List<string>();
            if (local == null || remote == null) return Missing(local, remote, differences);

            Longs(differences, "activeKills", local.activeKills, remote.activeKills);
            Longs(differences, "required", local.required, remote.required);

            // The one that decides whether a player can fight the boss. Compared even
            // though it is derived from the two above, because the derivation is the
            // thing most likely to differ -- one side using > where the other uses >=.
            if (local.open != remote.open)
                differences.Add($"open local={local.open} remote={remote.open}");

            return differences;
        }

        // ── Comparators ───────────────────────────────────────────────────────

        private static void Longs(List<string> into, string what, long local, long remote)
        {
            if (local == remote) return;

            into.Add($"{what} local={local} remote={remote}");
        }

        private static void Ints(List<string> into, string what, int local, int remote)
        {
            if (local == remote) return;

            into.Add($"{what} local={local} remote={remote}");
        }

        private static void Seconds(List<string> into, string what, double local, double remote)
        {
            if (Math.Abs(local - remote) <= SecondsTolerance) return;

            into.Add($"{what} local={local:0.00} remote={remote:0.00}");
        }

        private static List<string> Missing(object local, object remote, List<string> into)
        {
            if (local == null)  into.Add("local returned nothing");
            if (remote == null) into.Add("remote returned nothing");

            return into;
        }

        // ── Digging things out ────────────────────────────────────────────────

        private static int Count(Array array) => array?.Length ?? 0;

        private static IEnumerable<string> Union(IEnumerable<string> a, IEnumerable<string> b)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string value in a) if (!string.IsNullOrEmpty(value)) seen.Add(value);
            foreach (string value in b) if (!string.IsNullOrEmpty(value)) seen.Add(value);

            return seen;
        }

        private static IEnumerable<string> SkillIds(CharacterSnapshot from)
        {
            if (from?.skills == null) yield break;

            foreach (var skill in from.skills)
                if (skill != null) yield return skill.skillId;
        }

        private static long SkillXp(CharacterSnapshot from, string skillId)
        {
            if (from?.skills == null) return 0L;

            foreach (var skill in from.skills)
                if (skill != null && skill.skillId == skillId) return skill.xp;

            return 0L;
        }

        private static IEnumerable<string> ItemIds(CharacterSnapshot from)
        {
            if (from?.inventory == null) yield break;

            foreach (var slot in from.inventory)
                if (slot != null) yield return slot.itemId;
        }

        /// <summary>Summed across slots: the same item can occupy several.</summary>
        private static long Held(CharacterSnapshot from, string itemId)
        {
            if (from?.inventory == null) return 0L;

            long total = 0L;

            foreach (var slot in from.inventory)
                if (slot != null && slot.itemId == itemId) total += slot.quantity;

            return total;
        }

        private static IEnumerable<string> MonsterIds(CharacterSnapshot from)
        {
            if (from?.kills == null) yield break;

            foreach (var kill in from.kills)
                if (kill != null) yield return kill.monsterId;
        }

        private static long ActiveKills(CharacterSnapshot from, string monsterId)
        {
            if (from?.kills == null) return 0L;

            foreach (var kill in from.kills)
                if (kill != null && kill.monsterId == monsterId) return kill.activeKills;

            return 0L;
        }

        private static IEnumerable<string> StackIds(SettlementSnapshot from)
        {
            if (from?.items == null) yield break;

            foreach (var stack in from.items)
                if (stack != null) yield return stack.itemId;
        }
    }
}
