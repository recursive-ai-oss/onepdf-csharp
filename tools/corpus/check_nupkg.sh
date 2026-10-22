#!/usr/bin/env bash
# Fails when a package contains test data or any PDF.
# Usage: check_nupkg.sh DIR   (DIR holds the .nupkg / .snupkg files from `dotnet pack`)
set -eu
dir=${1:?usage: check_nupkg.sh DIR}
n=0; bad=0
for p in "$dir"/*.nupkg "$dir"/*.snupkg; do
  [ -e "$p" ] || continue
  n=$((n+1))
  # unzip -Z1 lists entry names only
  hits=$(unzip -Z1 "$p" | grep -Ei '(^|/)testdata(/|$)|\.pdf$|verapdf|pdf20examples|govdocs' || true)
  if [ -n "$hits" ]; then echo "test data inside $p:"; echo "$hits"; bad=1; fi
done
[ "$n" -gt 0 ] || { echo "no packages found in $dir"; exit 2; }
[ "$bad" -eq 0 ] && echo "$n package(s) checked: no test data inside"
exit "$bad"
