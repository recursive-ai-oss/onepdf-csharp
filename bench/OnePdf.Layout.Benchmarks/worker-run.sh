#!/usr/bin/env bash
# One measured run of the layout benchmarks on a shared benchmark host, from the repository root:
#
#   ONEFONT_PROJECT=/path/to/OneFont.csproj bench/OnePdf.Layout.Benchmarks/worker-run.sh <out-dir>
#
# It waits for the host's benchmark lock (~/WORKER-BENCH.lock), builds Release, publishes the NativeAOT OnePDF program, then records
# machine and load, checks every engine's output, runs BenchmarkDotNet and the one-process-per-cell peak-RSS pass. The lock is taken
# inside this script and released when it exits. Needs .NET 10 under ~/dotnet10 (override with DOTNET_DIR).
set -uo pipefail
out="$(realpath -m "${1:?usage: worker-run.sh <out-dir>}")"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
dn="${DOTNET_DIR:-$HOME/dotnet10}"
export PATH="$dn:$PATH" DOTNET_ROOT="$dn" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
# MSBuild reads this as a property, in the benchmark build and in the projects BenchmarkDotNet generates.
[ -n "${ONEFONT_PROJECT:-}" ] && export OneFontProject="$ONEFONT_PROJECT"
mkdir -p "$out"
export ONEPDF_ARTIFACTS="$out/artifacts" TMPDIR="$out/tmp"
mkdir -p "$TMPDIR"

lock="$HOME/WORKER-BENCH.lock"
until ( set -o noclobber; echo "onepdf layout benchmarks $(date -Is) expected_end=about 75 min after start" >"$lock" ) 2>/dev/null; do
  echo "$(date -Is) lock held: $(cat "$lock" 2>/dev/null)" >>"$out/lock-wait.log"
  sleep 60
done
trace=0
cleanup() { [ "$trace" -ne 0 ] && kill "$trace" 2>/dev/null; rm -f "$lock"; }
trap cleanup EXIT

{
  echo "start: $(date -Is)"
  echo "host: $(hostname)  cores: $(nproc)"
  lscpu | grep -E 'Model name|^CPU\(s\)|Thread|MHz' | sed 's/  */ /g'
  free -g | head -2
  echo "load before: $(cat /proc/loadavg)"
  dotnet --list-sdks
  dotnet --list-runtimes | grep -E 'NETCore.App'
} >"$out/machine.txt" 2>&1

( while true; do echo "$(date -Is) load=$(cut -d' ' -f1-3 /proc/loadavg) top: $(ps -eo pcpu,comm --sort=-pcpu | sed -n 2,4p | tr -s ' ' | tr '\n' ';')"; sleep 15; done ) >"$out/load-trace.txt" 2>&1 &
trace=$!

cd "$root" || exit 1
dotnet build -c Release bench/OnePdf.Layout.Benchmarks -v quiet -clp:NoSummary >"$out/build.log" 2>&1 || { echo "build failed"; tail -20 "$out/build.log"; exit 1; }
dotnet publish -c Release -r linux-x64 bench/OnePdf.Layout.Benchmarks.Aot -o "$out/aot" -v quiet -clp:NoSummary >"$out/aot-build.log" 2>&1 || { echo "aot publish failed"; tail -20 "$out/aot-build.log"; exit 1; }
dll="$ONEPDF_ARTIFACTS/bin/OnePdf.Layout.Benchmarks/release/OnePdf.Layout.Benchmarks.dll"

dotnet "$dll" check "$out/outputs" >"$out/check.csv" 2>"$out/check.err"

( cd bench/OnePdf.Layout.Benchmarks && dotnet "$dll" --filter '*' --artifacts "$out/bdn" >"$out/bdn.log" 2>&1 )

BENCH_DLL="$dll" AOT_EXE="$out/aot/OnePdf.Layout.Benchmarks.Aot" bench/OnePdf.Layout.Benchmarks/rss.sh "$out/rss.csv" >"$out/rss.log" 2>&1

echo "end: $(date -Is)  load after: $(cat /proc/loadavg)" >>"$out/machine.txt"
echo done
