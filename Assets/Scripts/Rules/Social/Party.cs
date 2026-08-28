namespace IdleExplorers.Rules
{
    /// <summary>
    /// The terms of standing near other people, and of grouping with them.
    ///
    /// ══ WHY THESE NUMBERS ARE SHARED ══════════════════════════════════════════════
    ///
    /// The same lesson the boss gate taught. A "four player maximum" written once on
    /// the server and once in the button that greys itself out is two numbers, and the
    /// day one changes the other lies to somebody: a party screen that offers a fifth
    /// slot and a join that answers 409.
    ///
    /// So the client draws four slots because the rules say four, and the server
    /// refuses a fifth for exactly the same reason.
    /// </summary>
    public static class Party
    {
        /// <summary>
        /// How many can stand in one group.
        ///
        /// Four, which is the number the boss encounter is tuned around. Raising it is
        /// a balance decision rather than a UI one, which is part of why it lives here.
        /// </summary>
        public const int MaxMembers = 4;

        /// <summary>Whether one more will fit.</summary>
        public static bool HasRoom(int members) => members < MaxMembers;
    }

    /// <summary>
    /// How the world learns where everybody is.
    ///
    /// ══ WHY POLLING, AND WHY THIS OFTEN ═══════════════════════════════════════════
    ///
    /// Unity WebGL has no usable socket. System.Net is excluded from the player, so
    /// ClientWebSocket is unavailable -- and it fails by HANGING rather than by
    /// refusing to compile, which is the worst way for a transport to be missing.
    ///
    /// Real-time would mean a JavaScript bridge and a hub with its own reconnect,
    /// backpressure and authentication story. That is worth building for a game where
    /// position decides something. Here nothing rewards where you stand: no loot, no
    /// rates, no combat advantage. So other players are polled, and the cost of being
    /// two seconds out of date is that somebody's walk is a little steppy.
    ///
    /// The seam is deliberate. The server's presence model does not know how it is
    /// being read, so replacing this with a socket later changes the transport and
    /// nothing else.
    /// </summary>
    public static class Presence
    {
        /// <summary>How often a client says where it is, and asks who else is here.</summary>
        public const double ReportSeconds = 2d;

        /// <summary>
        /// How long a report stays believable.
        ///
        /// Comfortably more than three report intervals, because a dropped request or
        /// a browser tab that throttles a background timer must not make somebody
        /// blink out of the world. Erring long is invisible; erring short is a player
        /// who keeps vanishing.
        /// </summary>
        public const double StaleAfterSeconds = 10d;

        /// <summary>
        /// The furthest a character can be from the origin of a map.
        ///
        /// Movement is presentation, not progression -- nothing about where somebody
        /// stands earns anything, so this is not a cheat check. It exists so a client
        /// with a broken float cannot store a coordinate that makes everyone else's
        /// camera or nameplate maths misbehave.
        /// </summary>
        public const float MapExtent = 500f;

        /// <summary>Brings a reported coordinate into the believable range.</summary>
        public static float Clamp(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;

            return value < -MapExtent ? -MapExtent
                 : value >  MapExtent ?  MapExtent
                 : value;
        }
    }
}
