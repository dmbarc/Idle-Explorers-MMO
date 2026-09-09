using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// Armour that has left the character but is expected back.
    ///
    /// The bug being fixed: the tin set throws a worn piece at the enemy on its
    /// 4-piece bonus, which drops the worn count from six to five — switching off
    /// every 6-piece bonus, including the one whose whole job is picking the thrown
    /// piece back up. The set permanently disarms itself on its own proc.
    /// </summary>
    public class InFlightPiecesTests
    {
        [Fact]
        public void APieceIsNotInFlightUntilItIsThrown()
        {
            var ledger = new InFlightPieces();

            Assert.False(ledger.IsInFlight("tin_helmet", 0d));
        }

        [Fact]
        public void AThrownPieceStillCounts()
        {
            var ledger = new InFlightPieces();
            ledger.Record("tin_helmet", "helmet", 100d);

            Assert.True(ledger.IsInFlight("tin_helmet", 100d));
            Assert.True(ledger.IsInFlight("tin_helmet", 140d));
            Assert.Equal("helmet", ledger.SlotOf("tin_helmet"));
        }

        /// <summary>
        /// Without an expiry this is a free set bonus: throw a piece, walk away, keep
        /// the six-piece for the rest of your life.
        /// </summary>
        [Fact]
        public void AnAbandonedPieceStopsCountingAfterTheGraceWindow()
        {
            var ledger = new InFlightPieces();
            ledger.Record("tin_helmet", "helmet", 100d);

            Assert.True(ledger.IsInFlight("tin_helmet", 100d + InFlightPieces.GraceSeconds));
            Assert.False(ledger.IsInFlight("tin_helmet", 100d + InFlightPieces.GraceSeconds + 0.01d));
        }

        [Fact]
        public void ScavengingItClearsTheRecord()
        {
            var ledger = new InFlightPieces();
            ledger.Record("tin_helmet", "helmet", 100d);

            Assert.True(ledger.Clear("tin_helmet"));
            Assert.False(ledger.IsInFlight("tin_helmet", 100d));
            Assert.False(ledger.Clear("tin_helmet"));   // and clearing twice is harmless
        }

        [Fact]
        public void ThrowingTheSamePieceAgainRestartsItsWindow()
        {
            var ledger = new InFlightPieces();
            ledger.Record("tin_helmet", "helmet", 0d);
            ledger.Record("tin_helmet", "helmet", 100d);

            Assert.True(ledger.IsInFlight("tin_helmet", 140d));
            Assert.Equal(1, ledger.Count);
        }

        [Fact]
        public void SeveralPiecesCanBeOutAtOnce()
        {
            var ledger = new InFlightPieces();
            ledger.Record("tin_helmet", "helmet", 0d);
            ledger.Record("tin_boots",  "boots",  10d);

            Assert.True(ledger.IsInFlight("tin_helmet", 20d));
            Assert.True(ledger.IsInFlight("tin_boots",  20d));
            Assert.Equal(2, ledger.Count);
        }

        [Fact]
        public void PruningDropsOnlyTheLapsedOnes()
        {
            var ledger = new InFlightPieces();
            ledger.Record("tin_helmet", "helmet", 0d);
            ledger.Record("tin_boots",  "boots",  100d);

            ledger.Prune(110d);

            Assert.Equal(1, ledger.Count);
            Assert.True(ledger.IsInFlight("tin_boots", 110d));
            Assert.False(ledger.IsInFlight("tin_helmet", 110d));
        }

        [Fact]
        public void ClearingEverythingEmptiesIt()
        {
            var ledger = new InFlightPieces();
            ledger.Record("tin_helmet", "helmet", 0d);
            ledger.Record("tin_boots",  "boots",  0d);

            ledger.Clear();

            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void AnEmptyItemIdIsIgnoredRatherThanRecorded()
        {
            var ledger = new InFlightPieces();
            ledger.Record("", "helmet", 0d);
            ledger.Record(null, "helmet", 0d);

            Assert.Equal(0, ledger.Count);
            Assert.False(ledger.IsInFlight(null, 0d));
            Assert.Null(ledger.SlotOf("tin_helmet"));
        }
    }
}
