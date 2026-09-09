using System.Collections.Generic;
using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// The tally behind the boss portal.
    ///
    /// The gate is a thousand ACTIVE goblins, per character. Everything here is about
    /// that number being unfakeable and unlosable: it can only be credited from a
    /// settlement result, it saturates rather than wrapping, and a gate that has
    /// opened cannot close.
    /// </summary>
    public class KillLedgerTests
    {
        private const long BossGate = 1000L;

        [Fact]
        public void AFreshLedgerHasKilledNothing()
        {
            var ledger = new KillLedger();

            Assert.Equal(0L, ledger.ActiveKills("goblin"));
            Assert.Equal(0L, ledger.TotalKills("goblin"));
            Assert.False(ledger.GateOpen("goblin", BossGate));
            Assert.Equal(BossGate, ledger.Remaining("goblin", BossGate));
        }

        [Fact]
        public void CreditingSplitsWatchedFromAway()
        {
            var ledger = new KillLedger();
            ledger.Credit("goblin", kills: 768L, supervisedKills: 480L);

            Assert.Equal(480L, ledger.ActiveKills("goblin"));
            Assert.Equal(288L, ledger.AfkKills("goblin"));
            Assert.Equal(768L, ledger.TotalKills("goblin"));
        }

        [Fact]
        public void MonstersAreCountedSeparately()
        {
            var ledger = new KillLedger();
            ledger.Credit("goblin", 100L, 100L);
            ledger.Credit("bramblekin", 50L, 50L);

            Assert.Equal(100L, ledger.ActiveKills("goblin"));
            Assert.Equal(50L,  ledger.ActiveKills("bramblekin"));
            Assert.Equal(0L,   ledger.ActiveKills("goblin_king"));
        }

        // ── The gate ──────────────────────────────────────────────────────────

        /// <summary>
        /// The boundary, stated exactly once, because the portal UI, the travel check
        /// and the engage endpoint all read it and must agree.
        /// </summary>
        [Fact]
        public void TheGateIsClosedAt999AndOpenAt1000()
        {
            var ledger = new KillLedger();

            ledger.Credit("goblin", 999L, 999L);
            Assert.False(ledger.GateOpen("goblin", BossGate));
            Assert.Equal(1L, ledger.Remaining("goblin", BossGate));

            ledger.Credit("goblin", 1L, 1L);
            Assert.True(ledger.GateOpen("goblin", BossGate));
            Assert.Equal(0L, ledger.Remaining("goblin", BossGate));
        }

        /// <summary>
        /// A gate an idle character walks through on its own is not a gate. Nine
        /// thousand goblins killed while logged out open nothing.
        /// </summary>
        [Fact]
        public void SleepingThroughNineThousandGoblinsOpensNothing()
        {
            var ledger = new KillLedger();
            ledger.Credit("goblin", kills: 9000L, supervisedKills: 0L);

            Assert.Equal(9000L, ledger.TotalKills("goblin"));
            Assert.False(ledger.GateOpen("goblin", BossGate));
            Assert.Equal(BossGate, ledger.Remaining("goblin", BossGate));
        }

        [Fact]
        public void TheGateStaysOpenOnceItIsOpen()
        {
            var ledger = new KillLedger();
            ledger.Credit("goblin", 1000L, 1000L);

            // Reloading from storage is the case that matters: the unlock has to be
            // persisted state, not something the session happened to be holding.
            var reloaded = new KillLedger();
            reloaded.Load(ledger.Rows);

            Assert.True(reloaded.GateOpen("goblin", BossGate));
        }

        // ── Round-tripping ────────────────────────────────────────────────────

        [Fact]
        public void RowsSurviveASaveAndLoad()
        {
            var ledger = new KillLedger();
            ledger.Credit("goblin", 768L, 480L);
            ledger.Credit("bramblekin", 30L, 10L);

            var reloaded = new KillLedger();
            reloaded.Load(ledger.Rows);

            Assert.Equal(480L, reloaded.ActiveKills("goblin"));
            Assert.Equal(288L, reloaded.AfkKills("goblin"));
            Assert.Equal(10L,  reloaded.ActiveKills("bramblekin"));
            Assert.Equal(2,    reloaded.Rows.Count);
        }

        /// <summary>
        /// Two rows for one monster would each hold half a tally, and which one the
        /// gate read would depend on iteration order.
        /// </summary>
        [Fact]
        public void DuplicateRowsAreFoldedRatherThanShadowed()
        {
            var reloaded = new KillLedger();
            reloaded.Load(new List<KillLedger.Tally>
            {
                new KillLedger.Tally { monsterId = "goblin", activeKills = 600L, afkKills = 10L },
                new KillLedger.Tally { monsterId = "goblin", activeKills = 400L, afkKills = 20L },
            });

            Assert.Single(reloaded.Rows);
            Assert.Equal(1000L, reloaded.ActiveKills("goblin"));
            Assert.Equal(30L,   reloaded.AfkKills("goblin"));
            Assert.True(reloaded.GateOpen("goblin", BossGate));
        }

        [Fact]
        public void LoadingReplacesRatherThanAccumulates()
        {
            var ledger = new KillLedger();
            ledger.Credit("goblin", 500L, 500L);

            ledger.Load(new List<KillLedger.Tally>
            {
                new KillLedger.Tally { monsterId = "goblin", activeKills = 7L },
            });

            Assert.Equal(7L, ledger.ActiveKills("goblin"));
        }

        [Fact]
        public void LoadingNothingIsNotAnException()
        {
            var ledger = new KillLedger();
            ledger.Credit("goblin", 5L, 5L);
            ledger.Load(null);

            Assert.Empty(ledger.Rows);
            Assert.Equal(0L, ledger.ActiveKills("goblin"));
        }

        // ── Credited from settlement, and nowhere else ────────────────────────

        /// <summary>
        /// The kill count and the loot come out of one integral. Crediting from the
        /// result directly is what stops a second code path deciding how many goblins
        /// died -- and a second code path is the one a cheater would aim at.
        /// </summary>
        [Fact]
        public void ASettlementResultCreditsItselfConsistently()
        {
            var result = new SettlementResult { Actions = 768L, SupervisedActions = 480L };

            var ledger = new KillLedger();
            ledger.Credit("goblin", result);

            Assert.Equal(480L, ledger.ActiveKills("goblin"));
            Assert.Equal(768L, ledger.TotalKills("goblin"));
        }

        // ── Degenerate input ──────────────────────────────────────────────────

        [Fact]
        public void SupervisedCannotExceedTheTotal()
        {
            var ledger = new KillLedger();
            ledger.Credit("goblin", kills: 10L, supervisedKills: 999_999L);

            Assert.Equal(10L, ledger.ActiveKills("goblin"));
            Assert.Equal(0L,  ledger.AfkKills("goblin"));
        }

        [Fact]
        public void NegativeAndEmptyCreditsChangeNothing()
        {
            var ledger = new KillLedger();
            ledger.Credit("goblin", kills: -50L, supervisedKills: -50L);
            ledger.Credit("goblin", kills: 0L, supervisedKills: 0L);
            ledger.Credit("", 100L, 100L);
            ledger.Credit(null, 100L, 100L);
            ledger.Credit("goblin", (SettlementResult)null);

            Assert.Empty(ledger.Rows);
        }

        /// <summary>
        /// A count that wrapped to negative would close a gate the player had already
        /// opened, and no amount of further play would reopen it.
        /// </summary>
        [Fact]
        public void AnAbsurdCreditSaturatesRatherThanWrapping()
        {
            var ledger = new KillLedger();
            ledger.Credit("goblin", long.MaxValue, long.MaxValue);
            ledger.Credit("goblin", long.MaxValue, long.MaxValue);

            Assert.Equal(long.MaxValue, ledger.ActiveKills("goblin"));
            Assert.True(ledger.ActiveKills("goblin") > 0L);
            Assert.True(ledger.GateOpen("goblin", BossGate));
        }
    }
}
