using System.Collections.Generic;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// Whether a character may wear a thing that belongs to a class.
    ///
    /// ══ WHY IT IS A SHARED RULE AND NOT A CHECK IN THE ENDPOINT ═══════════════════
    ///
    /// Because both hosts have to ask it and they have to get the same answer. The
    /// server refuses the equip; the client greys out the button, writes the refusal
    /// into the tooltip and declines to offer "Equip" in the item menu. Those are four
    /// places, and four places asking a question four ways is how a player ends up
    /// looking at an enabled button that always fails.
    ///
    /// The server's answer is the one that decides. The client's exists so the player
    /// finds out before they click rather than after.
    ///
    /// ══ WHY EVERY CLASS COUNTS, NOT JUST THE PRIMARY ══════════════════════════════
    ///
    /// character.class_id is the PRIMARY class -- the name, the rig, what the roster
    /// shows. character_class is every tree the character may spend in. A warrior who
    /// unlocked ranger has earned the ranger's bow, and refusing it because their
    /// portrait still says warrior would make a second class strictly worse than the
    /// first for reasons nothing on screen explains.
    ///
    /// That distinction is exactly the one the multiclass bug was: the server built
    /// talent trees from class_id alone, and spending a point in the second class came
    /// back "no such talent".
    /// </summary>
    public static class ClassLock
    {
        /// <summary>
        /// True when <paramref name="item"/> may be worn by a character holding
        /// <paramref name="classIds"/>.
        ///
        /// An item with no classReq is wearable by anyone, which is almost everything
        /// in the game -- so the common case is one string comparison against empty.
        /// </summary>
        public static bool Allows(ItemData item, IEnumerable<string> classIds)
        {
            if (item == null || string.IsNullOrEmpty(item.classReq)) return true;
            if (classIds == null) return false;

            foreach (string held in classIds)
                if (!string.IsNullOrEmpty(held) &&
                    string.Equals(held, item.classReq, System.StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>
        /// Why it was refused, for a tooltip or a toast. Null when it was not.
        ///
        /// Names the class rather than saying "wrong class", because a player looking
        /// at a sword they cannot use wants to know who can.
        /// </summary>
        public static string Refusal(ItemData item, IEnumerable<string> classIds, string className)
        {
            if (Allows(item, classIds)) return null;

            string who = string.IsNullOrEmpty(className) ? item.classReq : className;

            return $"Only a {who} can wield the {item.DisplayName}.";
        }
    }
}
