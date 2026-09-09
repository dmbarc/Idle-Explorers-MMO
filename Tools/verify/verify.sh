#!/usr/bin/env bash
# Compiles Assembly-CSharp and Assembly-CSharp-Editor with Unity's own Roslyn.
#
# Flags and assembly references come from the response files Unity generated for its
# last build. Two things are deliberately NOT taken from them:
#
#   * The SOURCE LIST is rebuilt from disk every run. Unity's rsp names its sources
#     explicitly, so reusing it verbatim silently skips any file created since Unity
#     last compiled -- a brand new script would appear to "compile clean" because it
#     was never compiled at all.
#
#   * The editor assembly's reference to Assembly-CSharp is re-pointed at the runtime
#     assembly THIS script just built. Unity's copy is whatever it last compiled, so
#     an editor script calling a method added to a runtime script in the same session
#     fails to resolve it -- an error that reads like a typo and is a stale reference.
#
# == WHY THIS SCRIPT ONCE REPORTED CLEAN ON CODE THAT DID NOT COMPILE =============
#
# It wrote `-out:` as a POSIX path. Git Bash maps /tmp outside the Windows filesystem,
# so the path handed to csc was one a Windows compiler reads as C:\tmp\... -- a
# directory that does not exist. csc stopped with
#
#     error CS2012: Cannot open '...' for writing
#
# before compiling anything, and the editor assembly then failed with CS0006 because
# the runtime dll it references was never produced. Neither error mentions a path
# under Assets/, and the script only ever looked for errors that did, so both were
# filtered out and it printed "compile clean" -- for an entire session.
#
# Three changes make that particular lie impossible:
#
#   1. Output paths go through cygpath -w, so csc is given a path it can write.
#   2. EVERY "error CS" is reported, wherever it occurs. Only WARNINGS are filtered
#      down to our own directories, because those are the ones we do not own.
#   3. The run fails unless the dll actually appears. A compiler that did nothing at
#      all is the one outcome a log-scraping check cannot distinguish from success,
#      so success is defined as an artefact existing rather than as silence.
set -u

# Repo-relative, so this survives being checked out anywhere. UNITY_EDITOR_DIR
# overrides the version for a different install or for CI.
HERE="$(cd "$(dirname "$0")" && pwd)"
PROJ="$(cd "$HERE/../.." && pwd)"

UNITY_EDITOR_DIR="${UNITY_EDITOR_DIR:-/c/Program Files/Unity/Hub/Editor/6000.4.6f1/Editor}"
DOTNET="$UNITY_EDITOR_DIR/Data/NetCoreRuntime/dotnet.exe"
CSCDLL="$UNITY_EDITOR_DIR/Data/DotNetSdkRoslyn/csc.dll"

# Unity names the artifacts directory after a hash of the build graph, so find it
# rather than hard-coding it -- it changes when Unity's version or settings change.
DAG="$(find "$PROJ/Library/Bee/artifacts" -maxdepth 1 -name '*.dag' -type d 2>/dev/null | head -1)"

OUT="$HERE/build"
mkdir -p "$OUT" || exit 2

for required in "$DOTNET" "$CSCDLL"; do
  [ -f "$required" ] || { echo "MISSING $required -- set UNITY_EDITOR_DIR."; exit 2; }
done
[ -n "$DAG" ] || { echo "No .dag under Library/Bee/artifacts -- let Unity compile once first."; exit 2; }

# The one line this whole script turned on. csc is a Windows program; it needs a
# Windows path, and every path below is built from this rather than from $OUT.
OUTWIN="$(cygpath -w "$OUT")"

cd "$PROJ" || exit 2

fail=0
for ASM in Assembly-CSharp Assembly-CSharp-Editor; do
  RSP="$DAG/$ASM.rsp"
  [ -f "$RSP" ] || { echo "MISSING $RSP -- let Unity compile once first."; exit 2; }

  MYRSP="$OUT/$ASM.args.rsp"

  grep -viE '(\.cs"?$|^-out:|^-refout:|^-r:.*Assembly-CSharp\.ref\.dll)' "$RSP" > "$MYRSP"
  echo "-out:\"$OUTWIN/$ASM.dll\"" >> "$MYRSP"
  if [ "$ASM" = "Assembly-CSharp-Editor" ]; then
    echo "-r:\"$OUTWIN/Assembly-CSharp.dll\"" >> "$MYRSP"
  fi

  # Third-party sources Unity compiles into this assembly, kept verbatim.
  grep -iE '\.cs"?$' "$RSP" | grep -viE '"Assets/(Scripts|Editor|ItemDrops)/' >> "$MYRSP"

  # Our sources, from disk.
  if [ "$ASM" = "Assembly-CSharp" ]; then dirs="Assets/Scripts Assets/ItemDrops"
  else                                    dirs="Assets/Editor"; fi
  find $dirs -name '*.cs' 2>/dev/null | sed 's#^#"#; s#$#"#' >> "$MYRSP"

  mine=$(find $dirs -name '*.cs' 2>/dev/null | wc -l)
  echo "=== $ASM ($mine of our source file(s)) ==="

  # Removed first, so a compiler that does nothing cannot pass on a stale artefact.
  rm -f "$OUT/$ASM.dll"

  "$DOTNET" "$(cygpath -w "$CSCDLL")" "@$(cygpath -w "$MYRSP")" -nologo > "$OUT/$ASM.raw" 2>&1
  status=$?

  # Every error, wherever it comes from. A compiler error outside Assets/ is still a
  # compiler error, and filtering by path is exactly how this script used to lie.
  grep -E "error CS" "$OUT/$ASM.raw" | sort -u > "$OUT/$ASM.errors"

  # Warnings, only from code we own. The imported packages carry warnings we cannot fix.
  grep -E "Assets[\/](Scripts|Editor|ItemDrops)[\/].*warning CS" "$OUT/$ASM.raw" \
    | sort -u > "$OUT/$ASM.warnings"

  cat "$OUT/$ASM.errors"
  cat "$OUT/$ASM.warnings"

  if [ -s "$OUT/$ASM.errors" ]; then fail=1; fi

  if [ ! -f "$OUT/$ASM.dll" ]; then
    echo "  NO OUTPUT -- $ASM.dll was not produced (csc exit $status). See $OUT/$ASM.raw"
    fail=1
  fi
done

if [ "$fail" -ne 0 ]; then
  echo ""
  echo "FAILED -- see the errors above."
  exit 1
fi

echo ""
echo "OK -- both assemblies compiled, and both dlls exist."
