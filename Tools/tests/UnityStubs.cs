// Minimal stand-ins for the UnityEngine surface the tested files touch, so real
// project source can be compiled and run outside Unity. Only what is actually
// called is implemented -- a stub that guesses at behaviour would make a passing
// test mean nothing.
using System;

namespace UnityEngine
{
    public static class Mathf
    {
        public const float Epsilon = 1.4e-45f;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int   Max(int a, int b)     => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int   Min(int a, int b)     => a < b ? a : b;
        public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        public static int   Clamp(int v, int lo, int hi)       => v < lo ? lo : (v > hi ? hi : v);
        public static float Abs(float v) => Math.Abs(v);
        public static float Sqrt(float v) => (float)Math.Sqrt(v);
        public static float Log10(float v) => (float)Math.Log10(v);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static int   FloorToInt(float v) => (int)Math.Floor(v);
        public static int   RoundToInt(float v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        public static bool  Approximately(float a, float b) => Math.Abs(a - b) < 1e-5f;
        public static float Sin(float v) => (float)Math.Sin(v);
        public static float Cos(float v) => (float)Math.Cos(v);
        public const float PI = (float)Math.PI;
    }

    public static class Debug
    {
        public static void Log(object m) { }
        public static void LogWarning(object m) => TestLog.Warnings.Add(m?.ToString() ?? "");
        public static void LogError(object m)   => TestLog.Errors.Add(m?.ToString() ?? "");
    }

    public static class TestLog
    {
        public static System.Collections.Generic.List<string> Warnings = new();
        public static System.Collections.Generic.List<string> Errors   = new();
        public static void Clear() { Warnings.Clear(); Errors.Clear(); }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float s)   => new Vector3(a.x * s, a.y * s, a.z * s);
        public override string ToString() => $"({x}, {y}, {z})";
    }

    public class SerializeField : Attribute { }
}

namespace UnityEngine
{
    /// <summary>
    /// Deterministic stand-in for UnityEngine.Random. StatBlock.Roll uses it, and a
    /// test that cannot fix the seed cannot make a claim about a damage band.
    /// </summary>
    public static class Random
    {
        private static System.Random _rng = new System.Random(20260824);

        public static void Seed(int seed) => _rng = new System.Random(seed);

        public static float value => (float)_rng.NextDouble();

        public static float Range(float min, float max) => min + (max - min) * value;
        public static int   Range(int min, int max)     => _rng.Next(min, max);
    }
}
