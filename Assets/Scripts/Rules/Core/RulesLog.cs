using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// Where the rules complain, without knowing who is listening.
    ///
    /// The rules run in two hosts. Unity points this at Debug.LogWarning; the game
    /// server points it at ILogger. Neither is referenced here, because a reference
    /// to either would stop these files compiling in the other place.
    ///
    /// Warnings from the rules are almost always CONTENT problems -- an item granting
    /// a stat nothing applies, a set bonus naming an action nothing implements. They
    /// need to be loud in both hosts, because a bonus that quietly does nothing is
    /// the failure this project keeps rediscovering.
    /// </summary>
    public static class RulesLog
    {
        /// <summary>Set once at start-up by whichever host is running the rules.</summary>
        public static Action<string> OnWarn;

        public static void Warn(string message)
        {
            // No fallback to Console. A server writing to stdout from deep inside a
            // damage calculation is worse than silence, and the host always sets this.
            OnWarn?.Invoke(message);
        }
    }
}
