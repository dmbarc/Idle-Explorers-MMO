using System;
using System.Collections.Generic;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// Whether a point may go into a node.
    ///
    /// ══ WHY THIS MOVED OUT OF TalentManager ═══════════════════════════════════════
    ///
    /// Because talent points are in the Critical tier -- they change a character's
    /// stats, which decide how fast they farm and whether they beat the boss enrage --
    /// and they were entirely client-authoritative. TalentManager.Spend mutated a
    /// CharacterData and the server had never heard of any of it.
    ///
    /// The server therefore has to make the same decision, and the one thing that must
    /// not happen is two implementations of it: a client that thinks tier three is open
    /// and a server that does not produces a button that fails with no explanation.
    ///
    /// So the DECISION lives here and both hosts ask it. TalentManager keeps the
    /// presentation -- placing a new ability on the bar, the toasts, the tooltips --
    /// which is exactly the split the rest of this migration uses.
    ///
    /// ══ WHY IT TAKES ARGUMENTS RATHER THAN READING A CHARACTER ════════════════════
    ///
    /// TalentManager reached into CharacterManager.Current and ContentManager on every
    /// call. That is convenient in a game with one character on screen and unusable in
    /// a server handling three hundred. Everything here is a function of what it is
    /// handed, which also makes it testable without a Unity scene.
    /// </summary>
    public static class Talents
    {
        /// <summary>
        /// Points that must already be spent before a tier opens.
        ///
        /// Three per tier, so the first row has to be worked through before the second
        /// is reachable. This is the shape of the tree rather than a balance number --
        /// changing it changes what a tree IS.
        /// </summary>
        public const int PointsPerTier = 3;

        /// <summary>
        /// One point per character level after the first.
        ///
        /// Level 1 characters have none, so the first level-up is when the tree becomes
        /// interesting rather than something to be ignored during the tutorial.
        /// </summary>
        public static int TotalPoints(int characterLevel) => Math.Max(0, characterLevel - 1);

        public static int TierRequirement(int tier) => Math.Max(0, tier) * PointsPerTier;

        /// <summary>
        /// What rank a node currently sits at. Zero when untouched.
        /// </summary>
        public static int RankOf(IReadOnlyList<TalentRank> ranks, string nodeId)
        {
            if (ranks == null || string.IsNullOrEmpty(nodeId)) return 0;

            foreach (var rank in ranks)
                if (rank != null && rank.nodeId == nodeId) return Math.Max(0, rank.rank);

            return 0;
        }

        /// <summary>
        /// Points spent across EVERY tree the character has specced into.
        ///
        /// One shared pool rather than a pool per class: with up to three trees,
        /// separate pools would make a third class free and remove the decision
        /// entirely. Sharing them is what makes spreading yourself thin a real cost.
        ///
        /// Nodes the character no longer has access to still COUNT, deliberately.
        /// Dropping a class must not silently hand back its points as free ones -- the
        /// refund is an explicit respec, not a side effect.
        /// </summary>
        public static int SpentPoints(IReadOnlyList<TalentRank> ranks,
                                      IReadOnlyList<TalentNode> nodes)
        {
            if (ranks == null) return 0;

            int spent = 0;

            foreach (var rank in ranks)
            {
                if (rank == null || rank.rank <= 0) continue;

                TalentNode node = Find(nodes, rank.nodeId);

                // An unknown node is worth one point each, not zero. Content can be
                // retired, and pricing a retired node at zero would hand its owner free
                // points -- which is the direction that inflates a character.
                int cost = node?.PointCost ?? 1;

                spent += rank.rank * Math.Max(1, cost);
            }

            return spent;
        }

        public static int AvailablePoints(int characterLevel,
                                          IReadOnlyList<TalentRank> ranks,
                                          IReadOnlyList<TalentNode> nodes) =>
            Math.Max(0, TotalPoints(characterLevel) - SpentPoints(ranks, nodes));

        /// <summary>
        /// Whether one more point can go into this node, and why not if it cannot.
        ///
        /// `nodes` is every node the character can reach across ALL their trees, not
        /// just this node's own tree -- the point pool and the tier requirement are
        /// both counted across everything, and passing one tree would silently make a
        /// cross-specced character's points cheaper.
        /// </summary>
        public static bool CanSpend(TalentNode node, int characterLevel,
                                    IReadOnlyList<TalentRank> ranks,
                                    IReadOnlyList<TalentNode> nodes,
                                    out string reason)
        {
            if (node == null)
            {
                reason = "No such talent.";
                return false;
            }

            if (RankOf(ranks, node.id) >= node.RankCap)
            {
                reason = "Already at maximum rank.";
                return false;
            }

            int spent     = SpentPoints(ranks, nodes);
            int available = Math.Max(0, TotalPoints(characterLevel) - spent);

            if (available < node.PointCost)
            {
                reason = node.PointCost > 1
                    ? $"Needs {node.PointCost} points."
                    : "No talent points to spend.";

                return false;
            }

            int required = TierRequirement(node.tier);

            if (spent < required)
            {
                reason = $"Spend {required - spent} more point(s) first.";
                return false;
            }

            if (node.requiresNodeIds != null)
            {
                foreach (string prerequisite in node.requiresNodeIds)
                {
                    if (string.IsNullOrEmpty(prerequisite)) continue;
                    if (RankOf(ranks, prerequisite) > 0)    continue;

                    string name = Find(nodes, prerequisite)?.name ?? prerequisite;

                    reason = $"Requires {name}.";
                    return false;
                }
            }

            reason = "";
            return true;
        }

        /// <summary>
        /// Every node in a set of classes, flattened.
        ///
        /// Built here rather than by each caller because "all the nodes this character
        /// can reach" is the input to almost everything above, and a caller that passed
        /// one tree instead of three would silently make a cross-specced character's
        /// points cheaper.
        /// </summary>
        public static List<TalentNode> NodesOf(IEnumerable<ClassData> classes)
        {
            var nodes = new List<TalentNode>();

            if (classes == null) return nodes;

            foreach (var entry in classes)
            {
                if (entry?.talentTree == null) continue;

                foreach (var node in entry.talentTree)
                    if (node != null && !string.IsNullOrEmpty(node.id)) nodes.Add(node);
            }

            return nodes;
        }

        // ── What the points are actually worth ────────────────────────────

        /// <summary>
        /// Effect types whose value REDUCES something rather than adding to it.
        ///
        /// They need their own list because the sign is decided by the effect, not the
        /// call site -- and because they need a cap. A reduction of 1.0 divides the
        /// thing it reduces to nothing: an infinite attack rate, or a zero-second
        /// craft. Both are printing presses, and both are one generous talent away.
        /// </summary>
        public static readonly string[] Reductions =
        {
            "attackSpeedPercent", "cooldownPercent", "craftSpeedPercent", "gatherRatePercent",
        };

        /// <summary>Hardest any reduction may bite, however many points go in.</summary>
        public const float MaxReduction = 0.75f;

        /// <summary>
        /// Summed value of one effect across every talent taken.
        ///
        /// ══ WHY ABILITY-SCOPED NODES ARE SKIPPED ══════════════════════════════
        ///
        /// "Fireball costs 20% less mana" is not "every ability costs 20% less". A node
        /// naming an ability contributes to that ability and nothing else, and counting
        /// it here would quietly make every other ability cheaper too -- the kind of
        /// bug that reads as generous tuning rather than as a mistake.
        /// </summary>
        public static float Bonus(IReadOnlyList<TalentRank> ranks,
                                  IReadOnlyList<TalentNode> nodes,
                                  string effectType)
        {
            if (nodes == null || string.IsNullOrEmpty(effectType)) return 0f;

            float total = 0f;

            foreach (var node in nodes)
            {
                if (node == null || node.effectType != effectType) continue;
                if (!string.IsNullOrEmpty(node.abilityId))         continue;

                int rank = RankOf(ranks, node.id);
                if (rank <= 0) continue;

                total += node.effectValue * rank;
            }

            if (IsReduction(effectType)) total = RulesMath.Clamp(total, 0f, MaxReduction);

            return total;
        }

        public static bool IsReduction(string effectType)
        {
            foreach (string reduction in Reductions)
                if (reduction == effectType) return true;

            return false;
        }

        /// <summary>1 + Bonus, for the callers that want a straight multiplier.</summary>
        public static float Multiplier(IReadOnlyList<TalentRank> ranks,
                                       IReadOnlyList<TalentNode> nodes,
                                       string effectType) =>
            1f + Bonus(ranks, nodes, effectType);

        /// <summary>
        /// 1 - Bonus, for the reductions.
        ///
        /// Separate from Multiplier so the SIGN is decided once here rather than at
        /// each of a dozen call sites, which is where a minus goes missing.
        /// </summary>
        public static float ReductionMultiplier(IReadOnlyList<TalentRank> ranks,
                                                IReadOnlyList<TalentNode> nodes,
                                                string effectType) =>
            Math.Max(1f - MaxReduction, 1f - Bonus(ranks, nodes, effectType));

        private static TalentNode Find(IReadOnlyList<TalentNode> nodes, string nodeId)
        {
            if (nodes == null || string.IsNullOrEmpty(nodeId)) return null;

            foreach (var node in nodes)
                if (node != null && node.id == nodeId) return node;

            return null;
        }
    }
}
