using System;

namespace IdleExplorers.Rules
{
    /// <summary>Where an attack lands.</summary>
    public enum AreaKind
    {
        /// <summary>One target. The only shape the game had before this.</summary>
        Single,

        /// <summary>A filled circle around a point.</summary>
        Circle,

        /// <summary>A band between two radii — an expanding shockwave.</summary>
        Ring,

        /// <summary>A rectangle from the caster along a direction. A charge, a beam.</summary>
        Line,

        /// <summary>An arc in front of the caster. A cleave.</summary>
        Cone,
    }

    /// <summary>
    /// A point on the ground, in the two dimensions that matter.
    ///
    /// Not UnityEngine.Vector2, because this tree cannot reference an engine and the
    /// server has to resolve the same shapes to decide the same hits. Height is
    /// deliberately absent: every map in this game is flat enough that including it
    /// would only let a character dodge a shockwave by standing on a rock.
    /// </summary>
    public struct Ground
    {
        public float X;
        public float Z;

        public Ground(float x, float z) { X = x; Z = z; }

        public static Ground Zero => new Ground(0f, 0f);

        public float SquaredDistanceTo(Ground other)
        {
            float dx = X - other.X;
            float dz = Z - other.Z;

            return dx * dx + dz * dz;
        }

        public float DistanceTo(Ground other) => (float)Math.Sqrt(SquaredDistanceTo(other));
    }

    /// <summary>
    /// One telegraphed attack: what shape, where, how big.
    ///
    /// ══ WHY SHAPES ARE DATA ═══════════════════════════════════════════════════════
    ///
    /// Before this the game had exactly two: hit one target, or hit everything within
    /// a radius of yourself. Both were written inline in ApplyAbilityEffect, so a cone
    /// would have meant a third branch, a line a fourth, and the boss needs both.
    ///
    /// Describing the shape as data instead means the client can DRAW it -- the
    /// telegraph decal reads the same struct the hit test does, so what is painted on
    /// the ground and what actually lands cannot disagree. That is the entire promise
    /// of a telegraphed attack, and the usual way it breaks is two implementations.
    /// </summary>
    public struct AreaShape
    {
        public AreaKind Kind;

        /// <summary>Where the shape starts: the caster for a cone or line, the centre otherwise.</summary>
        public Ground Origin;

        /// <summary>Unit direction. Ignored by Circle and Ring.</summary>
        public Ground Facing;

        /// <summary>Reach for a line or cone, outer radius for a circle or ring.</summary>
        public float Range;

        /// <summary>Half-width of a line. Ignored by everything else.</summary>
        public float HalfWidth;

        /// <summary>Total arc of a cone, in degrees. Ignored by everything else.</summary>
        public float ArcDegrees;

        /// <summary>Inner radius of a ring. The hole in the middle.</summary>
        public float InnerRadius;

        public static AreaShape Single(Ground at) =>
            new AreaShape { Kind = AreaKind.Single, Origin = at };

        public static AreaShape Circle(Ground centre, float radius) =>
            new AreaShape { Kind = AreaKind.Circle, Origin = centre, Range = radius };

        public static AreaShape Ring(Ground centre, float inner, float outer) =>
            new AreaShape { Kind = AreaKind.Ring, Origin = centre, InnerRadius = inner, Range = outer };

        public static AreaShape Line(Ground from, Ground facing, float length, float halfWidth) =>
            new AreaShape
            {
                Kind = AreaKind.Line, Origin = from, Facing = Normalise(facing),
                Range = length, HalfWidth = halfWidth,
            };

        public static AreaShape Cone(Ground from, Ground facing, float length, float arcDegrees) =>
            new AreaShape
            {
                Kind = AreaKind.Cone, Origin = from, Facing = Normalise(facing),
                Range = length, ArcDegrees = arcDegrees,
            };

        /// <summary>
        /// Whether a point is inside.
        ///
        /// ══ THE BOUNDARY IS INCLUSIVE, DELIBERATELY ═══════════════════════════
        ///
        /// A player standing exactly on the edge is hit. The alternative -- exclusive
        /// -- means a float comparison decides whether somebody took damage, and the
        /// client and server would answer differently at the fifth decimal place.
        /// Inclusive is not more correct in the abstract; it is one answer instead of
        /// two, which is what matters when both sides have to agree.
        /// </summary>
        public bool Contains(Ground point)
        {
            switch (Kind)
            {
                case AreaKind.Single:
                    return point.SquaredDistanceTo(Origin) <= SinglePointTolerance * SinglePointTolerance;

                case AreaKind.Circle:
                    return point.SquaredDistanceTo(Origin) <= Range * Range;

                case AreaKind.Ring:
                {
                    float squared = point.SquaredDistanceTo(Origin);

                    return squared <= Range * Range &&
                           squared >= InnerRadius * InnerRadius;
                }

                case AreaKind.Line:
                {
                    // Projected onto the facing to get distance ALONG the line, and
                    // onto its perpendicular to get distance ACROSS it. Both have to
                    // be in range, which is what makes this a rectangle rather than a
                    // very long thin cone.
                    float dx = point.X - Origin.X;
                    float dz = point.Z - Origin.Z;

                    float along  = dx * Facing.X + dz * Facing.Z;
                    if (along < 0f || along > Range) return false;

                    float across = Math.Abs(dx * -Facing.Z + dz * Facing.X);

                    return across <= HalfWidth;
                }

                case AreaKind.Cone:
                {
                    float squared = point.SquaredDistanceTo(Origin);
                    if (squared > Range * Range) return false;

                    // The caster's own square is always inside its cone. Without this
                    // the direction of a zero-length vector is undefined and a boss
                    // could be stood on to avoid its own cleave.
                    if (squared <= SinglePointTolerance * SinglePointTolerance) return true;

                    float distance = (float)Math.Sqrt(squared);

                    float dx = (point.X - Origin.X) / distance;
                    float dz = (point.Z - Origin.Z) / distance;

                    // Dot product of two unit vectors is the cosine of the angle
                    // between them, so comparing cosines avoids an inverse trig call
                    // per target per frame -- and the comparison flips, because cosine
                    // decreases as the angle grows.
                    float cosine = dx * Facing.X + dz * Facing.Z;
                    float halfArc = RulesMath.Clamp(ArcDegrees, 0f, 360f) * 0.5f;

                    return cosine >= (float)Math.Cos(halfArc * DegreesToRadians);
                }

                default:
                    return false;
            }
        }

        /// <summary>
        /// How close counts as "the same point".
        ///
        /// A tenth of a unit, which is well under the width of a character. Exact
        /// float equality would mean a single-target hit depended on two machines
        /// computing the same position bit for bit.
        /// </summary>
        public const float SinglePointTolerance = 0.1f;

        private const double DegreesToRadians = Math.PI / 180d;

        private static Ground Normalise(Ground direction)
        {
            float length = (float)Math.Sqrt(direction.X * direction.X + direction.Z * direction.Z);

            // A zero direction would make every hit test NaN, and NaN comparisons are
            // all false -- so an attack with no facing would silently hit nobody.
            if (length < 0.0001f) return new Ground(0f, 1f);

            return new Ground(direction.X / length, direction.Z / length);
        }
    }
}
