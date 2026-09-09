using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// Where an attack lands.
    ///
    /// These matter more than most geometry tests because the same struct is used
    /// twice: the client DRAWS the telegraph from it and the hit test READS it. A
    /// difference between the painted shape and the damaged one is the single way a
    /// telegraphed boss fight loses a player's trust, and it is invisible in code
    /// review.
    /// </summary>
    public class AreaShapeTests
    {
        private static Ground At(float x, float z) => new Ground(x, z);

        private static readonly Ground North = new Ground(0f, 1f);
        private static readonly Ground East  = new Ground(1f, 0f);

        // ── Circle ────────────────────────────────────────────────────────────

        [Fact]
        public void ACircleContainsItsCentreAndItsEdge()
        {
            var circle = AreaShape.Circle(At(0f, 0f), radius: 5f);

            Assert.True(circle.Contains(At(0f, 0f)));
            Assert.True(circle.Contains(At(3f, 4f)));    // exactly 5 away
            Assert.True(circle.Contains(At(-5f, 0f)));
            Assert.False(circle.Contains(At(5.01f, 0f)));
        }

        // ── Ring ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The hole is the point. An expanding shockwave you can outrun inward is a
        /// mechanic; one that fills its own centre is just a circle.
        /// </summary>
        [Fact]
        public void ARingHasAHoleInIt()
        {
            var ring = AreaShape.Ring(At(0f, 0f), inner: 3f, outer: 6f);

            Assert.False(ring.Contains(At(0f, 0f)));
            Assert.False(ring.Contains(At(2.9f, 0f)));
            Assert.True(ring.Contains(At(3f, 0f)));
            Assert.True(ring.Contains(At(4.5f, 0f)));
            Assert.True(ring.Contains(At(6f, 0f)));
            Assert.False(ring.Contains(At(6.1f, 0f)));
        }

        // ── Line ──────────────────────────────────────────────────────────────

        [Fact]
        public void ALineIsARectangleNotAVeryThinCone()
        {
            var line = AreaShape.Line(At(0f, 0f), North, length: 20f, halfWidth: 1.5f);

            // Width is constant along the whole length -- which is what distinguishes
            // a charge from a cleave.
            Assert.True(line.Contains(At(1.4f, 1f)));
            Assert.True(line.Contains(At(1.4f, 19f)));

            Assert.False(line.Contains(At(1.6f, 1f)));
            Assert.False(line.Contains(At(1.6f, 19f)));
        }

        [Fact]
        public void ALineDoesNotExtendBehindTheCaster()
        {
            var line = AreaShape.Line(At(0f, 0f), North, length: 20f, halfWidth: 1.5f);

            Assert.True(line.Contains(At(0f, 0f)));
            Assert.True(line.Contains(At(0f, 20f)));

            Assert.False(line.Contains(At(0f, -0.1f)));
            Assert.False(line.Contains(At(0f, 20.1f)));
        }

        [Fact]
        public void ALineFollowsItsFacing()
        {
            var east = AreaShape.Line(At(0f, 0f), East, length: 10f, halfWidth: 1f);

            Assert.True(east.Contains(At(9f, 0f)));
            Assert.False(east.Contains(At(0f, 9f)));
        }

        /// <summary>
        /// An unnormalised direction must behave identically to a normalised one, or
        /// a caller passing "toward the player" without normalising gets a line ten
        /// times too long and nobody notices until a boss hits across the map.
        /// </summary>
        [Fact]
        public void ALineNormalisesWhateverDirectionItIsGiven()
        {
            var scruffy = AreaShape.Line(At(0f, 0f), At(0f, 37f), length: 10f, halfWidth: 1f);

            Assert.True(scruffy.Contains(At(0f, 10f)));
            Assert.False(scruffy.Contains(At(0f, 10.5f)));
        }

        // ── Cone ──────────────────────────────────────────────────────────────

        [Fact]
        public void AConeWidensWithDistance()
        {
            var cone = AreaShape.Cone(At(0f, 0f), North, length: 10f, arcDegrees: 90f);

            // At 45 degrees either side, x == z is exactly the edge.
            Assert.True(cone.Contains(At(0.9f, 1f)));
            Assert.True(cone.Contains(At(4.9f, 5f)));

            // A point that a 90-degree cone must exclude: more sideways than forward.
            Assert.False(cone.Contains(At(2f, 1f)));
        }

        [Fact]
        public void AConeDoesNotReachBehind()
        {
            var cone = AreaShape.Cone(At(0f, 0f), North, length: 10f, arcDegrees: 90f);

            Assert.False(cone.Contains(At(0f, -3f)));
            Assert.False(cone.Contains(At(3f, -3f)));
        }

        [Fact]
        public void AConeStopsAtItsRange()
        {
            var cone = AreaShape.Cone(At(0f, 0f), North, length: 5f, arcDegrees: 90f);

            Assert.True(cone.Contains(At(0f, 5f)));
            Assert.False(cone.Contains(At(0f, 5.1f)));
        }

        /// <summary>
        /// Standing inside the boss must not be a way to avoid its cleave. At zero
        /// distance the direction to the target is undefined, and the obvious
        /// implementation divides by zero and hits nobody.
        /// </summary>
        [Fact]
        public void StandingOnTheCasterIsInsideItsCone()
        {
            var cone = AreaShape.Cone(At(10f, 10f), North, length: 5f, arcDegrees: 60f);

            Assert.True(cone.Contains(At(10f, 10f)));
        }

        [Fact]
        public void AFullCircleConeHitsEverythingInRange()
        {
            var cone = AreaShape.Cone(At(0f, 0f), North, length: 5f, arcDegrees: 360f);

            Assert.True(cone.Contains(At(0f, 4f)));
            Assert.True(cone.Contains(At(0f, -4f)));
            Assert.True(cone.Contains(At(4f, 0f)));
            Assert.False(cone.Contains(At(0f, 6f)));
        }

        [Fact]
        public void AZeroArcConeHitsAlmostNothing()
        {
            var cone = AreaShape.Cone(At(0f, 0f), North, length: 10f, arcDegrees: 0f);

            Assert.True(cone.Contains(At(0f, 5f)));      // dead ahead
            Assert.False(cone.Contains(At(1f, 5f)));
        }

        // ── Single ────────────────────────────────────────────────────────────

        [Fact]
        public void ASinglePointIsForgivingToAFraction()
        {
            var point = AreaShape.Single(At(4f, 4f));

            Assert.True(point.Contains(At(4f, 4f)));
            Assert.True(point.Contains(At(4.05f, 4f)));
            Assert.False(point.Contains(At(4.5f, 4f)));
        }

        // ── Degenerate ────────────────────────────────────────────────────────

        /// <summary>
        /// A zero direction would make every comparison NaN, and every NaN comparison
        /// is false -- so the attack would silently hit nobody rather than fail.
        /// </summary>
        [Fact]
        public void AZeroFacingStillHitsSomething()
        {
            var line = AreaShape.Line(At(0f, 0f), At(0f, 0f), length: 10f, halfWidth: 1f);
            var cone = AreaShape.Cone(At(0f, 0f), At(0f, 0f), length: 10f, arcDegrees: 90f);

            Assert.True(line.Contains(At(0f, 5f)));
            Assert.True(cone.Contains(At(0f, 5f)));
        }

        [Fact]
        public void AZeroSizedShapeHitsNothingBeyondItsOrigin()
        {
            var circle = AreaShape.Circle(At(0f, 0f), radius: 0f);

            Assert.True(circle.Contains(At(0f, 0f)));
            Assert.False(circle.Contains(At(0.5f, 0f)));
        }

        [Fact]
        public void AnAbsurdArcIsClamped()
        {
            var cone = AreaShape.Cone(At(0f, 0f), North, length: 5f, arcDegrees: 100_000f);

            // Clamped to a full circle rather than wrapping round to a sliver.
            Assert.True(cone.Contains(At(0f, -4f)));
        }
    }
}
