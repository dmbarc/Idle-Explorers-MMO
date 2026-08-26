#!/usr/bin/env bash
# Everything that can be checked without opening Unity.
#
#   1. Both Unity assemblies compile, and both dlls actually exist on disk.
#   2. The standalone suite over content, maps, stats and equipment slots.
#
# Run this before every commit that touches Assets/ or Tools/.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"

fail=0

echo "── Unity assemblies ─────────────────────────────────────────────"
"$HERE/verify/verify.sh" || fail=1

echo ""
echo "── Standalone checks ────────────────────────────────────────────"
dotnet run --project "$HERE/tests" -v q --nologo || fail=1

echo ""
if [ "$fail" -ne 0 ]; then echo "CHECKS FAILED"; exit 1; fi
echo "ALL CHECKS PASSED"
