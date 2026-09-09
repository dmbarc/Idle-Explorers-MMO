using IdleExplorers.Api.Infrastructure;
using Npgsql;
using Xunit;

namespace IdleExplorers.Database.Tests
{
    /// <summary>
    /// The connection string an operator actually pastes.
    ///
    /// ══ WHY THIS EXISTS ═══════════════════════════════════════════════════════════
    ///
    /// The first deploy of this server died at startup on
    ///
    ///     Format of the initialization string does not conform to specification
    ///     starting at index 0
    ///
    /// because the secret held a postgres:// URI -- the format the Supabase dashboard
    /// hands out, the format every other Postgres tool accepts, and the one format
    /// Npgsql refuses. The message mentions neither URIs nor Postgres, and it arrives
    /// at startup rather than on a request, so the container simply never comes up.
    ///
    /// These run with no database. They are about PARSING, and a test that needed a
    /// live server to check a string parser would be skipped on exactly the machines
    /// where somebody is debugging their connection string.
    /// </summary>
    public class ConnectionStringTests
    {
        [Fact]
        public void AUriBecomesSomethingNpgsqlUnderstands()
        {
            string normalised = Db.Normalise(
                "postgresql://postgres:hunter2@db.abcdef.supabase.co:5432/postgres");

            // The real assertion: Npgsql accepts it. Comparing strings would pin the
            // formatting rather than the behaviour.
            var builder = new NpgsqlConnectionStringBuilder(normalised);

            Assert.Equal("db.abcdef.supabase.co", builder.Host);
            Assert.Equal(5432, builder.Port);
            Assert.Equal("postgres", builder.Database);
            Assert.Equal("postgres", builder.Username);
            Assert.Equal("hunter2", builder.Password);
        }

        [Fact]
        public void TheShortSchemeWorksToo()
        {
            var builder = new NpgsqlConnectionStringBuilder(
                Db.Normalise("postgres://user:pw@example.com:6543/mydb"));

            Assert.Equal("example.com", builder.Host);
            Assert.Equal(6543, builder.Port);
            Assert.Equal("mydb", builder.Database);
        }

        /// <summary>
        /// ══ THE ONE THAT SENDS PEOPLE ROTATING GOOD CREDENTIALS ═══════════════
        ///
        /// A URI percent-encodes anything special, so a password with @ or / in it
        /// arrives as %40 or %2F. Passed through verbatim it authenticates with the
        /// literal characters and fails with "password authentication failed" -- which
        /// reads as a wrong password, not as an encoding bug.
        /// </summary>
        [Fact]
        public void AnEncodedPasswordIsDecoded()
        {
            var builder = new NpgsqlConnectionStringBuilder(
                Db.Normalise("postgresql://postgres:p%40ss%2Fw%23rd@host.example:5432/postgres"));

            Assert.Equal("p@ss/w#rd", builder.Password);
        }

        [Fact]
        public void QueryParametersSurvive()
        {
            var builder = new NpgsqlConnectionStringBuilder(
                Db.Normalise("postgresql://u:p@host.example:5432/db?sslmode=require"));

            // Dropping sslmode against a host that demands TLS is a refused connection
            // with nothing in the message to explain it.
            Assert.Equal(SslMode.Require, builder.SslMode);
        }

        [Fact]
        public void AnUnknownParameterIsIgnoredRatherThanFatal()
        {
            // libpq accepts several parameters Npgsql does not. Refusing to start over
            // one would be worse than ignoring it.
            var builder = new NpgsqlConnectionStringBuilder(
                Db.Normalise("postgresql://u:p@host.example:5432/db?target_session_attrs=read-write"));

            Assert.Equal("host.example", builder.Host);
        }

        /// <summary>
        /// Exactly how far the percent hazard goes, which is less far than assumed.
        ///
        /// This test was written expecting any raw % in a password to be corrupted.
        /// It is not: .NET leaves an INVALID escape alone, so "ab%cdef" survives --
        /// %CD is not valid UTF-8 on its own and UnescapeDataString declines to guess.
        ///
        /// The real hazard is narrower and worth stating precisely rather than
        /// overstating: a password containing something that IS a valid escape, like
        /// %41, silently becomes the character it encodes. Overstating it would have
        /// sent somebody rewriting a parser that mostly works.
        /// </summary>
        [Fact]
        public void OnlyValidEscapesAreDecoded()
        {
            var tolerated = new NpgsqlConnectionStringBuilder(
                Db.Normalise("postgresql://postgres:ab%cdef@host.example:5432/postgres"));

            Assert.Equal("ab%cdef", tolerated.Password);

            // But this one really does change under the parser.
            var decoded = new NpgsqlConnectionStringBuilder(
                Db.Normalise("postgresql://postgres:ab%41cd@host.example:5432/postgres"));

            Assert.Equal("abAcd", decoded.Password);

            // The keyword form carries either through untouched, which is why it is the
            // form the documentation should recommend.
            var safe = new NpgsqlConnectionStringBuilder(
                Db.Normalise("Host=host.example;Username=postgres;Password=ab%41cd"));

            Assert.Equal("ab%41cd", safe.Password);
        }

        /// <summary>
        /// A password that breaks the URI is reported as a PASSWORD problem.
        ///
        /// A slash, hash or question mark ends the authority section, and .NET's own
        /// message for that is "Invalid port specified" -- so an operator whose
        /// password contains a slash is told to look at the port, at startup, on a
        /// container that then exits. This test was written expecting truncation and
        /// found a misleading crash instead.
        ///
        /// Naming the real cause is the difference between a five-minute fix and an
        /// afternoon, and it is the same class of failure that already cost one deploy
        /// cycle on this project.
        /// </summary>
        [Fact]
        public void APasswordThatBreaksTheUriSaysSo()
        {
            var thrown = Assert.Throws<System.ArgumentException>(() =>
                Db.Normalise("postgresql://postgres:pa/ss@db.example:5432/postgres"));

            Assert.Contains("password", thrown.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("keyword form", thrown.Message);

            // And it must not echo the credential it is complaining about.
            Assert.DoesNotContain("pa/ss", thrown.Message);

            // The keyword form takes it without complaint, which is the recommendation.
            var safe = new NpgsqlConnectionStringBuilder(
                Db.Normalise("Host=db.example;Username=postgres;Password=pa/ss"));

            Assert.Equal("pa/ss", safe.Password);
        }

        [Fact]
        public void TheKeywordFormIsLeftAlone()
        {
            const string keyword = "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres";

            // Unchanged, so the local default and every existing deployment keep
            // working exactly as they did.
            Assert.Equal(keyword, Db.Normalise(keyword));
        }

        [Fact]
        public void WhitespaceAroundAPastedValueDoesNotBreakIt()
        {
            // A value copied out of a dashboard very often carries a newline.
            var builder = new NpgsqlConnectionStringBuilder(
                Db.Normalise("  postgresql://u:p@host.example:5432/db\n"));

            Assert.Equal("host.example", builder.Host);
        }

        [Fact]
        public void AUriWithNoDatabaseFallsBackRatherThanBeingEmpty()
        {
            var builder = new NpgsqlConnectionStringBuilder(
                Db.Normalise("postgresql://u:p@host.example:5432"));

            Assert.Equal("postgres", builder.Database);
        }
    }
}
