using UnityEngine;

/// <summary>
/// Points the shared rules at Unity's console.
///
/// Everything under Assets/Scripts/Rules is compiled by both Unity and the game
/// server, so it cannot reference either one's logger. It warns through a delegate
/// instead, and somebody has to set it -- otherwise a content bug that the rules
/// detect goes nowhere, which is worse than the bug.
///
/// SubsystemRegistration runs before any scene loads and before the first Awake, and
/// re-runs after a domain reload, so the hook survives entering and leaving Play Mode
/// with Fast Enter Play Mode on.
/// </summary>
public static class RulesHost
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Install()
    {
        IdleExplorers.Rules.RulesLog.OnWarn = message => Debug.LogWarning(message);
    }

#if UNITY_EDITOR
    /// <summary>
    /// The editor runs validation and setup tools without ever entering Play Mode, so
    /// the runtime hook above has not fired. Without this, "Setup Everything" would
    /// silently swallow every content warning the rules raise.
    /// </summary>
    [UnityEditor.InitializeOnLoadMethod]
    private static void InstallInEditor() => Install();
#endif
}
