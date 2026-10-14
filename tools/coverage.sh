#!/usr/bin/env bash
# Runs the gdUnit4 test suite with code coverage and writes a Cobertura report of the framework (addons/ePlugin).
#
# gdUnit4 runs the tests inside a Godot process, and Godot loads the project assembly from a memory stream. Profiler
# based collectors (`dotnet test --collect "Code Coverage"`, coverlet) therefore never see its code. Instead the built
# assembly is instrumented on disk with dotnet-coverage, a dotnet-coverage server session collects what the Godot
# process reports, and the original assembly is restored afterwards.
#
# Usage: tools/coverage.sh <report.cobertura.xml> [dotnet test arguments...]
# Needs a Debug build of the solution, GODOT_BIN (like dotnet test) and dotnet-coverage on PATH
# (`dotnet tool install --global dotnet-coverage`). Exits with the exit code of dotnet test.
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <report.cobertura.xml> [dotnet test arguments...]" >&2
  exit 2
fi
report="$(realpath -m "$1")"
shift

project="$(cd "$(dirname "${BASH_SOURCE[0]}")/../src/eplugin-framework" && pwd)"
assembly="$project/.godot/mono/temp/bin/Debug/EPluginFramework.dll"
settings="$project/coverage.settings.xml"
session="eplugin-coverage-$$"
export DOTNET_COVERAGE_NOLOGO=1 DOTNET_COVERAGE_TELEMETRY_OPTOUT=1

if [[ ! -f "$assembly" ]]; then
  echo "error: $assembly not found, build the solution first" >&2
  exit 2
fi
mkdir -p "$(dirname "$report")"

collecting=false
# shellcheck disable=SC2317 # called by the EXIT trap
restore() {
  # Shutting down a session that is not running waits forever.
  if [[ "$collecting" == true ]]; then dotnet-coverage shutdown "$session" > /dev/null 2>&1 || true; fi
  # instrument keeps the originals; an instrumented assembly would otherwise survive an incremental build.
  for file in "$assembly" "${assembly%.dll}.pdb"; do
    if [[ -f "$file.orig" ]]; then mv -f "$file.orig" "$file"; fi
  done
}
trap restore EXIT

dotnet-coverage instrument "$assembly" --session-id "$session" --settings "$settings"
# Redirect the output: the background server would otherwise keep a pipe of the caller open.
dotnet-coverage collect --session-id "$session" --server-mode --background \
  --settings "$settings" --output-format cobertura --output "$report" > /dev/null 2>&1
collecting=true

status=0
(cd "$project" && dotnet test "EPluginFramework.sln" --configuration Debug --no-build --settings .runsettings "$@") \
  || status=$?

collecting=false
dotnet-coverage shutdown "$session"
if [[ ! -s "$report" ]]; then
  echo "error: no coverage report was written to $report" >&2
  exit 1
fi
exit "$status"
