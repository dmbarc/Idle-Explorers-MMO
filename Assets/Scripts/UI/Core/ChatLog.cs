using System;
using System.Collections.Generic;

/// <summary>
/// Everything that has been said, and the one place anything says it.
///
/// ══ WHY THIS REPLACED THE TOASTS ══════════════════════════════════════════════
///
/// A toast is a good way to say one thing and a terrible way to say six. They stack,
/// they expire, and by design they are gone before you can read the one you missed —
/// so "you levelled Mining", "your helmet broke" and "inventory full" all competed for
/// the same corner and the loser was simply never seen. A chat window keeps them, in
/// order, coloured by what they are, and costs the player nothing to ignore.
///
/// The buffer lives HERE rather than on the panel because the HUD is rebuilt whenever
/// the character changes and the panel goes with it. History that vanished when the
/// window was redrawn would be the toast problem again with extra steps.
///
/// ToastLayer still exists and still pops for anything raised outside the game — the
/// login and character-select screens have no chat window, and a silent failure there
/// would be worse than a popup.
/// </summary>
public static class ChatLog
{
    /// <summary>One thing somebody said.</summary>
    public readonly struct Line
    {
        public readonly string   Text;
        public readonly ChatTone Tone;

        public Line(string text, ChatTone tone)
        {
            Text = text;
            Tone = tone;
        }
    }

    /// <summary>
    /// How many lines are kept. Deep enough to scroll back through a fight, shallow
    /// enough that an AFK session does not accumulate an hour of gathering ticks.
    /// </summary>
    public const int Capacity = 120;

    private static readonly List<Line> _lines = new();

    /// <summary>Raised for each new line. The panel listens; nothing else needs to.</summary>
    public static Action<Line> OnLine;

    /// <summary>
    /// True while a chat window is up to receive lines.
    ///
    /// ToastLayer asks this before popping. In game the chat window is the display and
    /// a popup on top of it would be the same sentence twice; on the menus there is no
    /// window, so the popup is the only display there is.
    /// </summary>
    public static bool HasWindow { get; set; }

    /// <summary>Every line still held, oldest first. For a panel being rebuilt.</summary>
    public static IReadOnlyList<Line> Lines => _lines;

    /// <summary>Adds a line. Empty messages are dropped rather than shown as a gap.</summary>
    public static void Say(string message, ChatTone tone = ChatTone.Info)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var line = new Line(message, tone);

        _lines.Add(line);

        // Trimmed from the front, so the oldest line is the one that goes.
        if (_lines.Count > Capacity) _lines.RemoveRange(0, _lines.Count - Capacity);

        OnLine?.Invoke(line);
    }

    /// <summary>
    /// Forgets everything. Called when the game returns to the menu, so the next
    /// character does not open their chat window onto the last one's death.
    /// </summary>
    public static void Clear()
    {
        _lines.Clear();
        OnLine?.Invoke(new Line(null, ChatTone.Info));
    }

    // ── Typed input ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a leading channel prefix off a typed message.
    ///
    /// /p, /g and /w exist now so the four channels can be seen and tested before
    /// there is a server to carry them — without them, Party, Guild and World are
    /// three colours nothing can ever produce. Anything else is Local, which is what
    /// an MMO defaults to and what the player standing next to you would hear.
    /// </summary>
    public static ChatTone ParseChannel(ref string message)
    {
        if (string.IsNullOrEmpty(message) || message[0] != '/') return ChatTone.Local;

        int space = message.IndexOf(' ');
        string prefix = (space < 0 ? message.Substring(1) : message.Substring(1, space - 1)).ToLowerInvariant();
        string rest   = space < 0 ? "" : message.Substring(space + 1);

        switch (prefix)
        {
            case "p":
            case "party": message = rest; return ChatTone.Party;

            case "g":
            case "guild": message = rest; return ChatTone.Guild;

            case "w":
            case "world":
            case "y":
            case "yell":  message = rest; return ChatTone.World;
        }

        // An unrecognised slash command is left alone and said out loud, rather than
        // swallowed. A player who mistypes /guild should see their message, not
        // silence they have to work out the cause of.
        return ChatTone.Local;
    }
}
