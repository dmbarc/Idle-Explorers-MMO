/// <summary>
/// What a line of chat is, which is also what colour it is.
///
/// Two groups in one enum, deliberately. The first four are WHO said it — the channels
/// an MMO has, each with the colour players already expect from every other game in the
/// genre. The last four are the game itself talking, coloured by whether the news is
/// good, bad, or merely news.
///
/// They share an enum because they share a window. A player reading the chat box is not
/// separating "messages from people" from "messages from the game"; they are scanning
/// one column of coloured text for the line that matters, and one vocabulary is what
/// makes that scan work.
///
/// The colours live in UITheme so they are themeable alongside everything else — see
/// UITheme.ChatColor.
/// </summary>
public enum ChatTone
{
    // ── Who said it ───────────────────────────────────────────────────────────

    /// <summary>Players standing near you. White, and the default for typed chat.</summary>
    Local,

    /// <summary>Your party. Blue.</summary>
    Party,

    /// <summary>Your guild. Green.</summary>
    Guild,

    /// <summary>Everyone on the server. Purple.</summary>
    World,

    // ── The game talking ──────────────────────────────────────────────────────

    /// <summary>Something went your way: a level, a rare drop, a repair. Light green.</summary>
    Good,

    /// <summary>Something did not: you cannot afford it, it is on cooldown, you died. Red.</summary>
    Bad,

    /// <summary>Something is about to go wrong if ignored: armour breaking, bag full. Orange.</summary>
    Warning,

    /// <summary>Neither: a state change, a confirmation, a number. Yellow, and the default.</summary>
    Info,
}
