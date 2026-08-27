#!/usr/bin/env bash
# Everything that can be checked without opening Unity.
#
#   1. Both Unity assemblies compile, and both dlls actually exist on disk.
#   2. The standalone suite over content, maps, stats, slots and rules purity.
#   3. The game server's own suite: rules, content import, and the schema.
#
# The schema tests need the local Supabase stack. They SKIP themselves when it is
# not running rather than failing, so everything else stays runnable without
# Docker — but a skip that goes unnoticed is a check that silently stopped
# existing, so this prints a loud reminder at the end when that happens.
#
# Run this before every commit that touches Assets/, Tools/ or server/.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

fail=0

echo "── Unity assemblies ─────────────────────────────────────────────"
"$HERE/verify/verify.sh" || fail=1

echo ""
echo "── Standalone checks ────────────────────────────────────────────"
dotnet run --project "$HERE/tests" -v q --nologo || fail=1

echo ""
echo "── Game server ──────────────────────────────────────────────────"
server_log="$(mktemp)"
if ! dotnet test "$ROOT/server/IdleExplorers.slnx" -v q --nologo 2>&1 | tee "$server_log"; then
    fail=1
fi

# grep -c counts lines, and a skipped run prints "Skipped:  N" per assembly.
skipped="$(grep -oE 'Skipped: *[0-9]+' "$server_log" | grep -oE '[0-9]+' | awk '{ s += $1 } END { print s+0 }')"
rm -f "$server_log"

echo ""
if [ "$fail" -ne 0 ]; then echo "CHECKS FAILED"; exit 1; fi

if [ "${skipped:-0}" -gt 0 ]; then
    echo "ALL CHECKS PASSED — but $skipped were SKIPPED."
    echo "  The schema and row-level-security tests need the local stack:"
    echo "      cd server && supabase start"
    exit 0
fi

echo "ALL CHECKS PASSED"
