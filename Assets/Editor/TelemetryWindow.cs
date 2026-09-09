using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IdleExplorers.Rules;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reads a telemetry export and answers the questions a playtest was run to answer.
///
/// ══ WHY THIS IS IN UNITY AT ALL ═══════════════════════════════════════════════
///
/// Because the person who needs these numbers is already in Unity, and the alternative
/// is a DuckDB install and remembering SQL for a question asked once a week. The export
/// is NDJSON precisely so that jq and pandas stay available for the questions this
/// window does not answer -- this is the fast path, not the only path.
///
/// ══ WHY THE ARITHMETIC IS NOT HERE ════════════════════════════════════════════
///
/// Every number on this screen comes from IdleExplorers.Rules.TelemetrySummary, which
/// is a pure function over a list of records and is unit-tested. Counting inside an
/// EditorWindow would mean the only way to check a funnel is to open Unity, load an
/// export and squint -- and a stage that reads zero because its event name has a typo
/// looks exactly like a stage nobody reached.
///
/// This file does layout, filtering and file IO. Nothing else.
///
/// ══ WHY IT LOADS A FILE RATHER THAN QUERYING ══════════════════════════════════
///
/// A Unity editor holding a database connection string is a database connection string
/// on a machine that also runs untrusted asset code, and it would have to be the
/// service role to read another account's rows. The export is produced by an operator
/// command that already holds that credential; this reads its output.
/// </summary>
public class TelemetryWindow : EditorWindow
{
    /// <summary>
    /// Rows drawn at once.
    ///
    /// IMGUI lays out every row it is asked for whether or not it is on screen, so a
    /// 200,000-row export drawn in full freezes the editor. The summaries above the
    /// table read the WHOLE file regardless -- it is only the table that pages.
    /// </summary>
    private const int PageSize = 200;

    /// <summary>Longest a payload cell gets before it is cut. Wider than this wraps and eats the screen.</summary>
    private const int PayloadWidth = 90;

    private readonly List<TelemetryRecord> _all      = new();
    private readonly List<TelemetryRecord> _filtered = new();

    private string _loadedFrom = "";
    private string _loadError  = "";
    private int    _malformed;

    // ── Filters ───────────────────────────────────────────────────────────────

    private string[] _eventNames  = { "(all)" };
    private string[] _characters  = { "(all)" };
    private int      _eventChoice;
    private int      _characterChoice;
    private string   _search      = "";
    private string   _fromDate    = "";
    private string   _toDate      = "";
    private bool     _newestFirst = true;

    private int      _page;
    private Vector2  _scroll;
    private bool     _showSummaries = true;

    [MenuItem("Idle Explorers/Telemetry Explorer")]
    public static void Open()
    {
        var window = GetWindow<TelemetryWindow>("Telemetry");
        window.minSize = new Vector2(820f, 480f);
    }

    private void OnGUI()
    {
        DrawToolbar();

        if (_all.Count == 0)
        {
            DrawEmpty();
            return;
        }

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        if (_showSummaries) DrawSummaries();

        DrawFilters();
        DrawTable();

        EditorGUILayout.EndScrollView();
    }

    // ── Chrome ────────────────────────────────────────────────────────────────

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button("Load export…", EditorStyles.toolbarButton, GUILayout.Width(100f)))
            Load();

        using (new EditorGUI.DisabledScope(_filtered.Count == 0))
        {
            if (GUILayout.Button("Export CSV…", EditorStyles.toolbarButton, GUILayout.Width(100f)))
                SaveCsv();
        }

        _showSummaries = GUILayout.Toggle(_showSummaries, "Summaries",
                                          EditorStyles.toolbarButton, GUILayout.Width(80f));

        GUILayout.FlexibleSpace();

        if (_loadedFrom.Length > 0)
        {
            string malformed = _malformed > 0 ? $"   ·   {_malformed} unreadable" : "";

            GUILayout.Label($"{Path.GetFileName(_loadedFrom)}   ·   {_all.Count:N0} rows{malformed}",
                            EditorStyles.miniLabel);
        }

        EditorGUILayout.EndHorizontal();
    }

    private void DrawEmpty()
    {
        EditorGUILayout.Space(24f);

        if (_loadError.Length > 0) EditorGUILayout.HelpBox(_loadError, MessageType.Error);

        EditorGUILayout.HelpBox(
            "Load a .ndjson export produced by:\n\n" +
            "    dotnet run --project server/src/IdleExplorers.Tools -- export --out .\n\n" +
            "One JSON object per line. Both the gameplay and security streams live in " +
            "the same file, told apart by the 'stream' field.",
            MessageType.Info);
    }

    // ── Summaries ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Computed over EVERY loaded row, never over the filter.
    ///
    /// Deliberate: a funnel that silently recomputed itself against "event = settled"
    /// would read one player at Created and be wrong in a way that looks like data.
    /// The summaries answer questions about the playtest; the table answers questions
    /// about rows.
    /// </summary>
    private void DrawSummaries()
    {
        EditorGUILayout.Space(6f);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Funnel — distinct characters, whole file",
                                       EditorStyles.boldLabel);

            FunnelResult[] funnel = TelemetrySummary.Funnel(_all);

            int widest = 1;
            foreach (var stage in funnel) widest = Mathf.Max(widest, stage.Characters);

            foreach (var stage in funnel)
            {
                EditorGUILayout.BeginHorizontal();

                EditorGUILayout.LabelField(stage.Label, GUILayout.Width(110f));
                EditorGUILayout.LabelField($"{stage.Characters:N0}", GUILayout.Width(60f));

                Rect bar = GUILayoutUtility.GetRect(60f, 14f, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(bar, new Color(0f, 0f, 0f, 0.15f));

                if (stage.Characters > 0)
                {
                    var filled = new Rect(bar.x, bar.y,
                                          bar.width * stage.Characters / widest, bar.height);

                    EditorGUI.DrawRect(filled, new Color(0.30f, 0.62f, 0.90f, 0.85f));
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        EditorGUILayout.BeginHorizontal();

        Tallies("Classes chosen",  TelemetrySummary.ClassDistribution(_all), plain: true);
        Tallies("Hours per skill", TelemetrySummary.SecondsPerSkill(_all),   plain: false);

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();

        Tallies("Events",   TelemetrySummary.EventCounts(_all),    plain: true);
        Tallies("Security", TelemetrySummary.SecurityCounts(_all), plain: true);

        EditorGUILayout.EndHorizontal();

        BossResult boss = TelemetrySummary.BossFights(_all);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Goblin King", EditorStyles.boldLabel);

            if (boss.Attempts == 0L)
            {
                EditorGUILayout.LabelField("    nobody has fought it yet", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    $"    {boss.Clears:N0} of {boss.Attempts:N0} attempts cleared " +
                    $"({boss.WinRate:P0})   ·   mean clear {boss.MeanClearSeconds / 60d:N1} min");
            }
        }
    }

    /// <summary>
    /// One counted column.
    /// </summary>
    /// <param name="plain">
    /// True to print the count as a number, false to read it as seconds and print hours.
    /// A flag rather than two methods because the layout is identical and the only
    /// difference is one format string.
    /// </param>
    private static void Tallies(string heading, Tally[] rows, bool plain)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(heading, EditorStyles.boldLabel);

            if (rows.Length == 0)
            {
                EditorGUILayout.LabelField("    (none)", EditorStyles.miniLabel);
                return;
            }

            foreach (var row in rows)
            {
                string value = plain ? $"{row.Count:N0}" : $"{row.Count / 3600d:N1} h";

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"    {row.Key}");
                EditorGUILayout.LabelField(value, GUILayout.Width(80f));
                EditorGUILayout.EndHorizontal();
            }
        }
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    private void DrawFilters()
    {
        EditorGUILayout.Space(8f);

        EditorGUI.BeginChangeCheck();

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField("Event", GUILayout.Width(40f));
        _eventChoice = EditorGUILayout.Popup(_eventChoice, _eventNames, GUILayout.Width(180f));

        EditorGUILayout.LabelField("Character", GUILayout.Width(64f));
        _characterChoice = EditorGUILayout.Popup(_characterChoice, _characters, GUILayout.Width(200f));

        EditorGUILayout.LabelField("Search", GUILayout.Width(46f));
        _search = EditorGUILayout.TextField(_search);

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField("From", GUILayout.Width(40f));
        _fromDate = EditorGUILayout.TextField(_fromDate, GUILayout.Width(180f));

        EditorGUILayout.LabelField("To", GUILayout.Width(24f));
        _toDate = EditorGUILayout.TextField(_toDate, GUILayout.Width(180f));

        EditorGUILayout.LabelField("(YYYY-MM-DD, blank for open)", EditorStyles.miniLabel);

        GUILayout.FlexibleSpace();

        _newestFirst = EditorGUILayout.ToggleLeft("Newest first", _newestFirst, GUILayout.Width(100f));

        EditorGUILayout.EndHorizontal();

        if (EditorGUI.EndChangeCheck()) { _page = 0; Refilter(); }
    }

    /// <summary>
    /// Rebuilds the visible list.
    ///
    /// Recomputed on change rather than evaluated per drawn row: OnGUI runs several
    /// times a frame, and a predicate over two hundred thousand records inside it makes
    /// typing in the search box feel broken.
    /// </summary>
    private void Refilter()
    {
        _filtered.Clear();

        string wanted   = _eventChoice     > 0 ? _eventNames[_eventChoice]     : "";
        string whoLabel = _characterChoice > 0 ? _characters[_characterChoice] : "";
        string needle   = _search.Trim();

        DateTime from = ParseDate(_fromDate, DateTime.MinValue);
        DateTime to   = ParseDate(_toDate,   DateTime.MaxValue);

        // Inclusive of the whole "to" day. A range typed as one date meaning an empty
        // set is the single most annoying thing a date filter can do.
        if (to != DateTime.MaxValue) to = to.AddDays(1d);

        foreach (var record in _all)
        {
            if (wanted.Length   > 0 && record.eventName != wanted)      continue;
            if (whoLabel.Length > 0 && LabelFor(record) != whoLabel)    continue;

            if (from != DateTime.MinValue || to != DateTime.MaxValue)
            {
                DateTime when = record.OccurredAtUtc;

                if (when < from || when >= to) continue;
            }

            if (needle.Length > 0 && !Mentions(record, needle)) continue;

            _filtered.Add(record);
        }

        // By id, which is the insertion order the database assigned. Sorting by
        // timestamp would reorder rows written in the same microsecond differently on
        // every refresh, and the streams interleave.
        _filtered.Sort((a, b) => _newestFirst ? b.id.CompareTo(a.id) : a.id.CompareTo(b.id));
    }

    private static bool Mentions(TelemetryRecord record, string needle)
    {
        if (Contains(record.eventName, needle))     return true;
        if (Contains(record.characterName, needle)) return true;
        if (Contains(record.characterId, needle))   return true;
        if (Contains(record.accountId, needle))     return true;

        if (record.payload == null) return false;

        foreach (var pair in record.payload)
        {
            if (pair == null) continue;
            if (Contains(pair.k, needle) || Contains(pair.v, needle)) return true;
        }

        return false;
    }

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) &&
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static DateTime ParseDate(string text, DateTime fallback) =>
        DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AdjustToUniversal |
                          System.Globalization.DateTimeStyles.AssumeUniversal,
                          out DateTime parsed)
            ? parsed : fallback;

    // ── Table ─────────────────────────────────────────────────────────────────

    private void DrawTable()
    {
        EditorGUILayout.Space(6f);

        int pages = Mathf.Max(1, Mathf.CeilToInt(_filtered.Count / (float)PageSize));
        _page = Mathf.Clamp(_page, 0, pages - 1);

        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.LabelField($"{_filtered.Count:N0} matching   ·   page {_page + 1} of {pages}",
                                   EditorStyles.boldLabel);

        GUILayout.FlexibleSpace();

        using (new EditorGUI.DisabledScope(_page == 0))
            if (GUILayout.Button("◀", GUILayout.Width(30f))) _page--;

        using (new EditorGUI.DisabledScope(_page >= pages - 1))
            if (GUILayout.Button("▶", GUILayout.Width(30f))) _page++;

        EditorGUILayout.EndHorizontal();

        Header();

        int start = _page * PageSize;
        int end   = Mathf.Min(start + PageSize, _filtered.Count);

        for (int i = start; i < end; i++) Row(_filtered[i], i);
    }

    private static void Header()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        EditorGUILayout.LabelField("when",      EditorStyles.miniBoldLabel, GUILayout.Width(140f));
        EditorGUILayout.LabelField("event",     EditorStyles.miniBoldLabel, GUILayout.Width(150f));
        EditorGUILayout.LabelField("character", EditorStyles.miniBoldLabel, GUILayout.Width(140f));
        EditorGUILayout.LabelField("payload",   EditorStyles.miniBoldLabel);

        EditorGUILayout.EndHorizontal();
    }

    private static void Row(TelemetryRecord record, int index)
    {
        var line = GUILayoutUtility.GetRect(0f, 17f, GUILayout.ExpandWidth(true));

        // Zebra striping. Not decoration -- the payload column is dense and the eye
        // loses its row halfway across it.
        if (index % 2 == 0) EditorGUI.DrawRect(line, new Color(0f, 0f, 0f, 0.06f));

        // The security stream, tinted. It is a thousandth of the volume and would
        // otherwise be impossible to spot scrolling past.
        if (record.stream == TelemetryStreams.Security)
            EditorGUI.DrawRect(line, new Color(0.85f, 0.35f, 0.25f, 0.16f));

        var cell = new Rect(line.x + 4f, line.y, 136f, line.height);
        GUI.Label(cell, record.OccurredAtUtc.ToString("MM-dd HH:mm:ss"), EditorStyles.miniLabel);

        cell.x += 140f; cell.width = 146f;
        GUI.Label(cell, record.eventName ?? "", EditorStyles.miniLabel);

        cell.x += 150f; cell.width = 136f;
        GUI.Label(cell, Who(record), EditorStyles.miniLabel);

        cell.x += 140f; cell.width = Mathf.Max(60f, line.xMax - cell.x - 4f);
        GUI.Label(cell, Payload(record), EditorStyles.miniLabel);
    }

    private static string Who(TelemetryRecord record)
    {
        if (!string.IsNullOrEmpty(record.characterName)) return record.characterName;
        if (!string.IsNullOrEmpty(record.characterId))   return TelemetrySummary.Short(record.characterId);

        return $"acct {TelemetrySummary.Short(record.accountId)}";
    }

    private static string Payload(TelemetryRecord record)
    {
        if (record.payload == null || record.payload.Length == 0) return "";

        var text = new StringBuilder();

        foreach (var pair in record.payload)
        {
            if (pair == null) continue;
            if (text.Length > 0) text.Append("  ");

            text.Append(pair.k).Append('=').Append(pair.v);

            if (text.Length > PayloadWidth) { text.Append(" …"); break; }
        }

        return text.ToString();
    }

    private static string LabelFor(TelemetryRecord record)
    {
        if (string.IsNullOrEmpty(record.characterId)) return "";

        string name = string.IsNullOrEmpty(record.characterName) ? "(unnamed)" : record.characterName;

        return $"{name} · {TelemetrySummary.Short(record.characterId)}";
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    private void Load()
    {
        string path = EditorUtility.OpenFilePanel("Telemetry export", "", "ndjson");

        if (string.IsNullOrEmpty(path)) return;

        _all.Clear();
        _filtered.Clear();
        _loadError = "";
        _malformed = 0;
        _page      = 0;

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;

                // JsonUtility throws on syntax it cannot parse, but returns an object
                // with every field defaulted on a shape it does not recognise. So a
                // record with no event name is counted as unreadable rather than
                // trusted -- otherwise a whole export of the wrong file type loads as
                // ten thousand blank rows and looks like data.
                TelemetryRecord record;

                try   { record = JsonUtility.FromJson<TelemetryRecord>(line); }
                catch { _malformed++; continue; }

                if (record == null || string.IsNullOrEmpty(record.eventName)) { _malformed++; continue; }

                _all.Add(record);
            }
        }
        catch (Exception e)
        {
            _loadError = $"Could not read {Path.GetFileName(path)}: {e.Message}";
            return;
        }

        _loadedFrom = path;

        RebuildChoices();
        Refilter();
    }

    private void RebuildChoices()
    {
        var events = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var record in _all)
            if (!string.IsNullOrEmpty(record.eventName)) events.Add(record.eventName);

        _eventNames = Prefixed("(all)", events);

        string[] characters = TelemetrySummary.Characters(_all);
        _characters = Prefixed("(all)", characters);

        _eventChoice     = 0;
        _characterChoice = 0;
    }

    private static string[] Prefixed(string first, IEnumerable<string> rest)
    {
        var list = new List<string> { first };
        list.AddRange(rest);

        return list.ToArray();
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes what is currently FILTERED, not the whole file.
    ///
    /// The whole file is already on disk as NDJSON, which every tool reads. The reason
    /// to export from here is to hand somebody the forty rows being argued about.
    /// </summary>
    private void SaveCsv()
    {
        string path = EditorUtility.SaveFilePanel("Export filtered rows", "",
                                                  "telemetry-filtered.csv", "csv");

        if (string.IsNullOrEmpty(path)) return;

        var csv = new StringBuilder();
        csv.AppendLine("stream,id,event,occurredAt,accountId,characterId,characterName,payload");

        foreach (var record in _filtered)
        {
            csv.Append(Csv(record.stream)).Append(',')
               .Append(record.id).Append(',')
               .Append(Csv(record.eventName)).Append(',')
               .Append(Csv(record.occurredAt)).Append(',')
               .Append(Csv(record.accountId)).Append(',')
               .Append(Csv(record.characterId)).Append(',')
               .Append(Csv(record.characterName)).Append(',')
               .Append(Csv(FullPayload(record)))
               .AppendLine();
        }

        try
        {
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
            EditorUtility.RevealInFinder(path);
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Could not write", e.Message, "OK");
        }
    }

    /// <summary>The whole payload, untruncated. The table cuts it; a file should not.</summary>
    private static string FullPayload(TelemetryRecord record)
    {
        if (record.payload == null) return "";

        var text = new StringBuilder();

        foreach (var pair in record.payload)
        {
            if (pair == null) continue;
            if (text.Length > 0) text.Append(' ');

            text.Append(pair.k).Append('=').Append(pair.v);
        }

        return text.ToString();
    }

    /// <summary>
    /// One CSV field.
    ///
    /// Always quoted, rather than quoted when it needs to be. A payload can contain a
    /// comma, a quote or a newline -- an item name with an apostrophe is one bad
    /// authoring decision away -- and "quote when needed" is where CSV writers acquire
    /// their bugs.
    /// </summary>
    private static string Csv(string value) =>
        $"\"{(value ?? "").Replace("\"", "\"\"")}\"";
}
