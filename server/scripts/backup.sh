#!/usr/bin/env bash
# A nightly dump, independent of whoever is hosting the database.
#
# == WHY THIS EXISTS WHEN SUPABASE PRO ALREADY BACKS UP =========================
#
# Because a backup held by the same provider as the database is one account problem
# away from being gone with it, and because a restore you have never performed is a
# belief rather than a backup.
#
# Somebody will put forty hours into a character. Losing that is not a bug report, it
# is a person who never comes back.
#
# == WHAT IT DOES ===============================================================
#
#   1. pg_dump the whole database, custom format, compressed
#   2. verify the dump can be READ back before trusting it
#   3. keep the last N days and delete the rest
#
# Step 2 is the one people skip. A truncated dump is a valid file of the right size
# with nothing useful in it, and the only way to know is to ask pg_restore to list its
# contents -- which is cheap and catches a half-written file every time.
#
# == HOW TO RUN IT ==============================================================
#
#   IDLE_EXPLORERS_DB="postgresql://..." ./scripts/backup.sh /var/backups/idle
#
# From cron, nightly:
#
#   17 3 * * *  IDLE_EXPLORERS_DB="postgresql://..." /path/to/backup.sh /var/backups/idle
#
# The connection string is the DIRECT one, not the pooler: pg_dump needs a session it
# can hold open, and a transaction pooler will cut it off partway through.

set -euo pipefail

DEST="${1:-./backups}"
KEEP_DAYS="${KEEP_DAYS:-14}"

if [ -z "${IDLE_EXPLORERS_DB:-}" ]; then
  echo "IDLE_EXPLORERS_DB is not set. It needs the DIRECT connection string," >&2
  echo "not the pooled one -- pg_dump holds a session open." >&2
  exit 2
fi

command -v pg_dump    >/dev/null || { echo "pg_dump is not installed." >&2; exit 2; }
command -v pg_restore >/dev/null || { echo "pg_restore is not installed." >&2; exit 2; }

mkdir -p "$DEST"

STAMP="$(date -u +%Y%m%d-%H%M%S)"
FILE="$DEST/idle-explorers-$STAMP.dump"

echo "── Dumping to $FILE"

# Custom format (-Fc): compressed, and restorable table by table, which matters when
# the thing that needs restoring is one player's characters rather than the world.
#
# --no-owner and --no-privileges, because the roles on a restore target are almost
# never the roles on the source, and a dump that refuses to restore over a role
# mismatch is a dump that fails at the worst possible moment.
pg_dump "$IDLE_EXPLORERS_DB" \
  --format=custom \
  --compress=9 \
  --no-owner \
  --no-privileges \
  --file="$FILE"

echo "── Verifying it can be read back"

# THE STEP NOBODY DOES. A truncated dump is a plausible-looking file, and this is the
# cheapest possible proof that it is not one.
if ! pg_restore --list "$FILE" > /dev/null 2>&1; then
  echo "The dump is unreadable. NOT keeping it." >&2
  rm -f "$FILE"
  exit 1
fi

TABLES="$(pg_restore --list "$FILE" | grep -c 'TABLE DATA' || true)"

# A dump of an empty database is readable and worthless. If the schema is there, the
# tables are there, and a count of zero means something pointed at the wrong database.
if [ "$TABLES" -lt 10 ]; then
  echo "Only $TABLES tables in the dump — that is not this database. NOT keeping it." >&2
  rm -f "$FILE"
  exit 1
fi

SIZE="$(du -h "$FILE" | cut -f1)"
echo "── OK: $SIZE, $TABLES tables"

echo "── Removing dumps older than $KEEP_DAYS days"
find "$DEST" -name 'idle-explorers-*.dump' -type f -mtime "+$KEEP_DAYS" -print -delete

echo ""
echo "Restore drill, to be run against a SCRATCH database before the first player:"
echo ""
echo "  createdb idle_restore_test"
echo "  pg_restore --dbname=idle_restore_test --no-owner --no-privileges \"$FILE\""
echo "  psql idle_restore_test -c 'select count(*) from character;'"
echo ""
echo "EXPECTED, and not a failure:"
echo "  pg_restore: error: ... permission denied for table secrets"
echo "  pg_restore: warning: errors ignored on restore: 1"
echo ""
echo "That is the Supabase vault, which the postgres role cannot read and which a"
echo "fresh project provisions for itself. Every GAME table restores. Anything else in"
echo "that output is worth stopping for."
echo ""
echo "An untested backup is a belief. Do this once, write down the date, and do it"
echo "again whenever the schema changes shape."
