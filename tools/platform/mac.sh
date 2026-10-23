#!/bin/bash
# Build, test, AOT-smoke and score extraction on macOS. Run on the Mac with the source tree in ~/onepdf/tree
# (see README.md). Uses a user-local SDK in ~/onepdf/dotnet and writes logs to ~/onepdf/logs.
set -u
R=$HOME/onepdf
export DOTNET_ROOT=$R/dotnet PATH=$R/dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 \
  DOTNET_CLI_HOME=$R/home NUGET_PACKAGES=$R/nuget TMPDIR=$R/tmp ONEPDF_ARTIFACTS=$R/artifacts
export ONEPDF_FONT_CACHE=$R/data/fonts ONEPDF_UPSTREAM=$R/data/upstream ONEPDF_UNICODE=$R/data/unicode
L=$R/logs
mkdir -p "$L" "$TMPDIR" "$ONEPDF_ARTIFACTS"
cd "$R/tree" || exit 1

dotnet build tests/OnePdf.Tests -v quiet -clp:NoSummary > "$L/build.log" 2>&1; tail -4 "$L/build.log"
dotnet test tests/OnePdf.Tests > "$L/test.log" 2>&1; tail -6 "$L/test.log"
grep -A1 '^skipped' "$L/test.log" | grep -v '^--'

dotnet publish tests/OnePdf.AotSmoke -warnaserror -o "$ONEPDF_ARTIFACTS/aot" -v quiet -clp:NoSummary > "$L/aot.log" 2>&1
echo "aot publish exit code: $?"
"$ONEPDF_ARTIFACTS/aot/OnePdf.AotSmoke"

dotnet run -c Release --project tools/OnePdf.Quality -- all --set generated > "$L/quality.log" 2>&1
grep '^| ALL' "$L/quality.log"   # OnePDF content / order columns must equal the Linux run
