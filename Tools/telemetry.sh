#!/usr/bin/env bash
#
# Get the playtest data out.
#
# ══ WHY THIS SCRIPT EXISTS ════════════════════════════════════════════════════
#
# The exporter is a .NET console app and the documented way to run it —
# `dotnet run --project ... -- export` — does not forward arguments on this
# machine's SDK. It silently prints the usage text instead, which looks exactly like
# having typed the command wrong.
#
# So this builds it once and invokes the dll directly, which does work. One command,
# no arguments to remember, and it prints where it put the file.
#
# ══ WHICH DATABASE ════════════════════════════════════════════════════════════
#
# Whatever IDLE_EXPLORERS_DB points at, and the LOCAL stack when it is unset.
#
# For live playtest data you want the production connection string, which lives in
# the Supabase dashboard under Project Settings -> Database -> Connection string
# (use the SESSION pooler one, port 5432). It contains the database password, so it
# belongs in your shell for the length of a command and nowhere else:
#
#     IDLE_EXPLORERS_DB="Host=...;Port=5432;Database=postgres;Username=...;Password=..." \
#       bash Tools/telemetry.sh export
#
# It is deliberately NOT stored in this file or anywhere else in the repository.
#
# ══ USAGE ═════════════════════════════════════════════════════════════════════
#
#   bash Tools/telemetry.sh export                 everything, as NDJSON
#   bash Tools/telemetry.sh export --since 2026-08-29T00:00:00Z
#   bash Tools/telemetry.sh errors                 just what broke, newest first
#   bash Tools/telemetry.sh inspect Bricta         one character's sheet and ledger
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
PROJECT="$ROOT/server/src/IdleExplorers.Tools"
DLL="$PROJECT/bin/Debug/net10.0/IdleExplorers.Tools.dll"

command="${1:-export}"
shift || true

# Built quietly every time. It takes about two seconds and removes the entire class
# of "I edited the exporter and got yesterday's answer".
dotnet build "$PROJECT" -v q --nologo > /dev/null

case "$command" in
  export)
    OUT="${OUT:-$ROOT/telemetry}"
    mkdir -p "$OUT"

    echo "── Exporting to $OUT ─────────────────────────────────────────"
    dotnet "$DLL" export --out "$OUT" "$@"
    ;;

  errors)
    # ══ WHY THIS IS A SEPARATE VERB ═══════════════════════════════════════════
    #
    # Because during a playtest it is the only one anybody wants. An export is a
    # file to open in something; this is the answer to "did anything break", on
    # screen, in one command.
    #
    # It exports to a temporary directory and filters, rather than querying the
    # database directly, so there is exactly one piece of SQL in this project that
    # reads telemetry and it lives in the exporter.
    TMP="$(mktemp -d)"
    trap 'rm -rf "$TMP"' EXIT

    dotnet "$DLL" export --out "$TMP" > /dev/null

    FILE="$(find "$TMP" -name '*.ndjson' | head -1)"

    if [ -z "$FILE" ]; then
      echo "No telemetry file was written. Is the database reachable?"
      exit 1
    fi

    echo "── Client errors, newest last ────────────────────────────────"
    echo ""

    # grep rather than jq, which is not installed here. The lines are one object
    # each, which is the property NDJSON was chosen for.
    if ! grep -F '"client_error"' "$FILE" | tail -40; then
      echo "  (none — nobody's client has thrown anything)"
    fi

    echo ""
    echo "── How many, by message ──────────────────────────────────────"

    # The MESSAGE field specifically, not every "v" in the payload. Matching bare
    # "v" counted the map, the stack, the type and the occurrence number as though
    # each were a distinct error -- five rows of noise per genuine one.
    #
    # The pair is adjacent in the JSON, which is what makes this greppable without
    # jq: the payload is an array of {"k":...,"v":...} objects.
    grep -F '"client_error"' "$FILE" \
      | grep -oE '"k":"message","v":"[^"]{0,120}"' \
      | sed 's/"k":"message","v":"//; s/"$//' \
      | sort | uniq -c | sort -rn | head -20 || echo "  (none)"
    ;;

  inspect)
    who="${1:-}"

    if [ -z "$who" ]; then
      echo "usage: bash Tools/telemetry.sh inspect <character-name-or-id>"
      exit 2
    fi

    dotnet "$DLL" inspect --character "$who"
    ;;

  *)
    echo "usage: bash Tools/telemetry.sh [export|errors|inspect] ..."
    exit 2
    ;;
esac
