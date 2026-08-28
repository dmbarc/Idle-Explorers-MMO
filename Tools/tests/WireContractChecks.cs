using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleExplorersTests
{
    /// <summary>
    /// Whether the client and the server agree on what the fields are called.
    ///
    /// ══ THE BUG THIS EXISTS FOR ═══════════════════════════════════════════════════
    ///
    /// The server sent a character roster with the id in a field called `id`. The
    /// client read it into a field called `characterId`. JsonUtility matches on name,
    /// has no attribute for mapping one to another, and does NOT fail when a name is
    /// absent -- it leaves the field at its default and says nothing.
    ///
    /// So the roster arrived complete with names and levels and every id blank. The
    /// merge matched nothing, and character creation checked the returned id, found the
    /// empty string, and reported failure for a character the server had just written.
    ///
    /// Nothing caught it. The API tests read the response with GetProperty("id"), so
    /// they agreed with whatever the server happened to send; the client types are not
    /// referenced there at all. Both sides were internally consistent and wrong
    /// together.
    ///
    /// ══ WHY THIS COMPARES EMITTED OBJECTS AND NOT THE WHOLE FILE ══════════════════
    ///
    /// A first attempt asked whether each client field name appeared ANYWHERE in the
    /// server sources. It reported no problems -- and would have passed the bug above,
    /// because `characterId` was all over CharacterEndpoints as a local variable. The
    /// question is not whether the server knows the word. It is whether the server puts
    /// that word on the wire.
    ///
    /// So this collects the property names of the anonymous objects the endpoints
    /// actually return, and nothing else.
    ///
    /// ══ WHAT IT DOES NOT CATCH ════════════════════════════════════════
    ///
    /// The names are pooled across every endpoint, so this fails only on a field NO
    /// endpoint emits at all. That is the shape the original bug had -- nothing
    /// anywhere sent `characterId` -- but it means a PARTIAL regression slips through:
    /// revert the roster alone and this still passes, because the create endpoint is
    /// still emitting the same word.
    ///
    /// Measured, not assumed: reverting AccountEndpoints on its own leaves this green
    /// and fails four IdempotencyTests instead, which read the roster over real HTTP.
    /// That is the layer covering the per-endpoint case, and it is why this one is
    /// left coarse rather than grown a type-to-route map it would then have to keep
    /// in step with the routing table.
    /// </summary>
    internal static class WireContractChecks
    {
        internal static void Run(Action<bool, string> check, string root)
        {
            Console.WriteLine("Wire contract");

            string endpoints = Path.Combine(root, "server", "src", "IdleExplorers.Api", "Endpoints");
            string wire      = Path.Combine(root, "Assets", "Scripts", "Backend", "IGameBackend.cs");

            if (!Directory.Exists(endpoints) || !File.Exists(wire))
            {
                check(false, "the server endpoints and the client wire types are both present");
                return;
            }

            HashSet<string> emitted = EmittedFields(endpoints);

            check(emitted.Count > 20,
                  $"the server's response shapes were parsed (found {emitted.Count} field names)");

            string source  = Strip(File.ReadAllText(wire));
            var    inbound = InboundTypes(source);

            check(inbound.Count > 0, "the client declares types it deserialises from the server");

            foreach ((string type, List<string> fields) in WireTypes(source))
            {
                // Outbound-only types are not on this contract: the client SENDS them
                // and the server never returns them. BossActionReport is one.
                if (!inbound.Contains(type)) continue;

                foreach (string field in fields)
                {
                    check(emitted.Contains(field),
                          $"the server emits '{field}', which {type} reads -- a name only the " +
                          "client uses is silently left at its default, never an error");
                }
            }
        }

        /// <summary>
        /// Every property name in every anonymous object the endpoints construct.
        ///
        /// Brace-matched rather than regex-terminated, because these objects nest and a
        /// non-greedy match to the first close brace stops inside the first child.
        /// </summary>
        private static HashSet<string> EmittedFields(string endpoints)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);

            foreach (string file in Directory.EnumerateFiles(endpoints, "*.cs"))
            {
                string source = Strip(File.ReadAllText(file));

                foreach (Match start in Regex.Matches(source, NewObject))
                {
                    string body = Braced(source, start.Index + start.Length - 1);

                    foreach (string part in SplitTop(body))
                    {
                        string piece = part.Trim();

                        if (piece.Length == 0) continue;

                        // name = value
                        Match named = Regex.Match(piece, NamedProperty);
                        if (named.Success) { found.Add(named.Groups[1].Value); continue; }

                        // shorthand: a bare local, or thing.Property
                        Match bare = Regex.Match(piece, ShorthandProperty);
                        if (bare.Success) found.Add(bare.Groups[1].Value);
                    }
                }
            }

            return found;
        }

        /// <summary>The types the client receives, as declared by the interface's return types.</summary>
        private static HashSet<string> InboundTypes(string source)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match found_ in Regex.Matches(source, AwaitableOf))
                found.Add(found_.Groups[1].Value);

            return found;
        }

        private static List<(string Type, List<string> Fields)> WireTypes(string source)
        {
            var types = new List<(string, List<string>)>();

            foreach (Match declaration in Regex.Matches(source, SerializableClass))
            {
                var fields = Regex
                    .Matches(declaration.Groups[2].Value, FieldDeclaration)
                    .Select(field => field.Groups[1].Value)
                    .ToList();

                types.Add((declaration.Groups[1].Value, fields));
            }

            return types;
        }

        /// <summary>Everything between a brace and its partner.</summary>
        private static string Braced(string source, int open)
        {
            int depth = 0;

            for (int at = open; at < source.Length; at++)
            {
                if (source[at] == '{') depth++;
                else if (source[at] == '}' && --depth == 0)
                    return source.Substring(open + 1, at - open - 1);
            }

            return "";
        }

        /// <summary>Splits on commas that are not inside brackets or parentheses.</summary>
        private static IEnumerable<string> SplitTop(string body)
        {
            int depth = 0, start = 0;

            for (int at = 0; at < body.Length; at++)
            {
                char c = body[at];

                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    yield return body.Substring(start, at - start);
                    start = at + 1;
                }
            }

            yield return body.Substring(start);
        }

        private static string Strip(string source)
        {
            source = Regex.Replace(source, BlockComment, "", RegexOptions.Singleline);
            source = Regex.Replace(source, DocComment,   "", RegexOptions.Multiline);
            source = Regex.Replace(source, LineComment,  "", RegexOptions.Multiline);

            return source;
        }

        // Written as constants so the escaping is in one place and reviewable. A stray
        // backslash inside an inline pattern is the kind of thing that turns a check
        // into one that matches nothing and passes forever.
        private const string NewObject         = @"\bnew\s*\{";
        private const string NamedProperty     = @"^(\w+)\s*=";
        private const string ShorthandProperty = @"^(?:[\w.]*\.)?(\w+)$";
        private const string AwaitableOf       = @"Awaitable<(\w+)(?:\[\])?>";
        private const string SerializableClass = @"\[Serializable\][\s\S]*?public class (\w+)([\s\S]*?)\n    \}";
        private const string FieldDeclaration  = @"public\s+[\w\[\]<>,\s\?]+?\s(\w+)\s*;";
        private const string BlockComment      = @"/\*.*?\*/";
        private const string DocComment        = @"^\s*///.*$";
        private const string LineComment       = @"^\s*//.*$";
    }
}
