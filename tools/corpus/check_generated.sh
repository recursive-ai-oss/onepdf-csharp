#!/usr/bin/env bash
# Sanity check of the generated set with pdftotext (a black-box validity check, never truth):
# every PDF must convert, using the password from its truth sidecar; every PDF has a sidecar
# and every sidecar a PDF. Files marked "unrecoverable" in their sidecar may fail.
set -u
cd "$(dirname "$0")/../../testdata/generated" || exit 2
fail=0; n=0
for f in pdf/*.pdf; do
  b=$(basename "$f" .pdf); n=$((n+1))
  if [ ! -f "truth/$b.json" ]; then echo "no sidecar: $b"; fail=1; continue; fi
  pw=$(python3 -c "import json,sys;d=json.load(open('truth/$b.json'));print(d.get('passwords',{}).get('user',''))")
  unrec=$(python3 -c "import json;d=json.load(open('truth/$b.json'));print('unrecoverable' if d.get('expect')=='unrecoverable' or d.get('blackBoxCheck')=='may-fail' else '')")
  if ! pdftotext -upw "$pw" "$f" /dev/null 2>/dev/null; then
    if [ "$unrec" = "unrecoverable" ]; then echo "expected failure: $b"; else echo "FAIL: $b"; fail=1; fi
  fi
done
for t in truth/*.json; do [ -f "pdf/$(basename "$t" .json).pdf" ] || { echo "no pdf: $t"; fail=1; }; done
echo "$n pdfs checked, failures: $fail"
exit $fail
