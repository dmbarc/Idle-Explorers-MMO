using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// The handful of Mathf helpers the rules need, without UnityEngine.
    ///
    /// Everything under Assets/Scripts/Rules is compiled TWICE: once by Unity, and
    /// once by the ASP.NET Core game server, which links these same files. That is the
    /// point -- the server decides what a swing does for, and the client draws the
    /// number, and they cannot disagree because there is only one implementation.
    ///
    /// The price is that nothing in here may touch the engine. UnityEngine.Mathf is
    /// the only piece of it the maths ever wanted, and it is a thin wrapper over
    /// System.Math, so this replaces it rather than stubbing it. A test asserts the
    /// boundary holds; see RulesPurity in Tools/tests.
    /// </summary>
    public static class RulesMath
    {
        /// <summary>
        /// Unity's Mathf.Approximately tolerance, reproduced.
        ///
        /// Not float.Epsilon, which is the smallest denormal and is useless as a
        /// comparison threshold. Unity scales the tolerance with the magnitude of the
        /// inputs; the fixed value here is what the test stubs already assumed and
        /// what every caller in this codebase is comparing against zero with.
        /// </summary>
        public const float Tolerance = 1e-5f;

        public static bool Approximately(float a, float b) => Math.Abs(a - b) < Tolerance;

        public static bool IsZero(float value) => Math.Abs(value) < Tolerance;

        public static float Clamp01(float value) =>
            value < 0f ? 0f : (value > 1f ? 1f : value);

        public static float Clamp(float value, float min, float max) =>
            value < min ? min : (value > max ? max : value);

        public static long Clamp(long value, long min, long max) =>
            value < min ? min : (value > max ? max : value);

        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    }
}
