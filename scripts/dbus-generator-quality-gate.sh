#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GENERATOR_PROJECT="$ROOT_DIR/src/Dbus.ContractGenerator/Dbus.ContractGenerator.csproj"
TEST_PROJECT="$ROOT_DIR/tests/Dbus.ContractGenerator.Tests/Dbus.ContractGenerator.Tests.csproj"

REPORT_DIR="${DBUS_QUALITY_GATE_REPORT_DIR:-/tmp/dbus-contract-generator-quality-gate}"
SWEEP_REPORT="$REPORT_DIR/sweep-report.json"
SUMMARY_REPORT="$REPORT_DIR/quality-gate-report.json"
PROFILE="${DBUS_QUALITY_GATE_PROFILE:-ci}"
BENCHMARK_HOOK="${DBUS_QUALITY_GATE_BENCHMARK_HOOK:-}"

case "$PROFILE" in
  nightly)
    DEFAULT_FUZZ_ITERATIONS=2500
    ;;
  *)
    DEFAULT_FUZZ_ITERATIONS=60
    ;;
esac

FUZZ_ITERATIONS="${DBUS_QUALITY_GATE_FUZZ_ITERATIONS:-$DEFAULT_FUZZ_ITERATIONS}"
MAX_BUILD_SECONDS="${DBUS_QUALITY_GATE_MAX_BUILD_SECONDS:-600}"
MAX_TEST_SECONDS="${DBUS_QUALITY_GATE_MAX_TEST_SECONDS:-1800}"
MAX_SWEEP_SECONDS="${DBUS_QUALITY_GATE_MAX_SWEEP_SECONDS:-1200}"
MAX_TOTAL_SECONDS="${DBUS_QUALITY_GATE_MAX_TOTAL_SECONDS:-3000}"
MAX_PERF_DURATION_MS="${DBUS_QUALITY_GATE_MAX_PERF_DURATION_MS:-12000}"
MAX_PERF_ALLOC_BYTES="${DBUS_QUALITY_GATE_MAX_PERF_ALLOC_BYTES:-1073741824}"
PERF_REPORT="$REPORT_DIR/perf-report.json"

export DBUS_GENERATOR_FUZZ_ITERATIONS="$FUZZ_ITERATIONS"
export DBUS_GENERATOR_FUZZ_PROFILE="$PROFILE"
export DBUS_GENERATOR_FUZZ_FAILURE_DIR="${DBUS_QUALITY_GATE_FUZZ_FAILURE_DIR:-$REPORT_DIR/fuzz-failures}"
export DBUS_GENERATOR_PERF_MAX_MS="$MAX_PERF_DURATION_MS"
export DBUS_GENERATOR_PERF_MAX_ALLOC_BYTES="$MAX_PERF_ALLOC_BYTES"
export DBUS_GENERATOR_PERF_REPORT_PATH="$PERF_REPORT"

mkdir -p "$REPORT_DIR"

require_integer_at_least() {
  local name="$1"
  local value="$2"
  local minimum="$3"

  if ! [[ "$value" =~ ^[0-9]+$ ]]; then
    echo "DBus quality gate failed: '$name' must be an integer (actual: '$value')." >&2
    exit 1
  fi

  if [ "$value" -lt "$minimum" ]; then
    echo "DBus quality gate failed: '$name' must be >= $minimum (actual: '$value')." >&2
    exit 1
  fi
}

assert_duration_within_limit() {
  local phase="$1"
  local duration="$2"
  local limit="$3"

  if [ "$duration" -le "$limit" ]; then
    return 0
  fi

  echo "DBus quality gate failed: phase '$phase' exceeded time limit (${duration}s > ${limit}s)." >&2
  exit 1
}

require_integer_at_least "DBUS_QUALITY_GATE_FUZZ_ITERATIONS" "$FUZZ_ITERATIONS" 1
require_integer_at_least "DBUS_QUALITY_GATE_MAX_BUILD_SECONDS" "$MAX_BUILD_SECONDS" 1
require_integer_at_least "DBUS_QUALITY_GATE_MAX_TEST_SECONDS" "$MAX_TEST_SECONDS" 1
require_integer_at_least "DBUS_QUALITY_GATE_MAX_SWEEP_SECONDS" "$MAX_SWEEP_SECONDS" 1
require_integer_at_least "DBUS_QUALITY_GATE_MAX_TOTAL_SECONDS" "$MAX_TOTAL_SECONDS" 1
require_integer_at_least "DBUS_QUALITY_GATE_MAX_PERF_DURATION_MS" "$MAX_PERF_DURATION_MS" 1
require_integer_at_least "DBUS_QUALITY_GATE_MAX_PERF_ALLOC_BYTES" "$MAX_PERF_ALLOC_BYTES" 1

extract_json_number() {
  local key="$1"
  local path="$2"
  local line
  line="$(grep -E "\"${key}\"[[:space:]]*:[[:space:]]*[0-9]+" "$path" | head -n 1 || true)"
  if [ -z "$line" ]; then
    echo ""
    return
  fi

  echo "$line" | sed -E 's/[^0-9]*([0-9]+).*/\1/'
}

BUILD_START="$(date +%s)"
dotnet build "$GENERATOR_PROJECT" -c Release
BUILD_END="$(date +%s)"

TEST_START="$(date +%s)"
dotnet test "$TEST_PROJECT" -c Release --filter "FullyQualifiedName~DbusContractSourceGenerator"
TEST_END="$(date +%s)"

if [ ! -f "$PERF_REPORT" ]; then
  echo "DBus quality gate failed: performance report was not produced at '$PERF_REPORT'." >&2
  exit 1
fi

PERF_DURATION_MS="$(extract_json_number "durationMs" "$PERF_REPORT")"
PERF_ALLOC_BYTES="$(extract_json_number "allocatedBytes" "$PERF_REPORT")"

if [ -z "$PERF_DURATION_MS" ] || [ -z "$PERF_ALLOC_BYTES" ]; then
  echo "DBus quality gate failed: performance report '$PERF_REPORT' is missing numeric metrics." >&2
  exit 1
fi

if [ "$PERF_DURATION_MS" -gt "$MAX_PERF_DURATION_MS" ]; then
  echo "DBus quality gate failed: perf duration exceeded (${PERF_DURATION_MS}ms > ${MAX_PERF_DURATION_MS}ms)." >&2
  exit 1
fi

if [ "$PERF_ALLOC_BYTES" -gt "$MAX_PERF_ALLOC_BYTES" ]; then
  echo "DBus quality gate failed: perf allocations exceeded (${PERF_ALLOC_BYTES} > ${MAX_PERF_ALLOC_BYTES} bytes)." >&2
  exit 1
fi

SWEEP_START="$(date +%s)"
DBUS_SWEEP_REPORT="$SWEEP_REPORT" "$ROOT_DIR/scripts/dbus-generator-sweep.sh"
SWEEP_END="$(date +%s)"

TOTAL_DURATION="$((SWEEP_END - BUILD_START))"
BUILD_DURATION="$((BUILD_END - BUILD_START))"
TEST_DURATION="$((TEST_END - TEST_START))"
SWEEP_DURATION="$((SWEEP_END - SWEEP_START))"

assert_duration_within_limit "build" "$BUILD_DURATION" "$MAX_BUILD_SECONDS"
assert_duration_within_limit "tests" "$TEST_DURATION" "$MAX_TEST_SECONDS"
assert_duration_within_limit "sweep" "$SWEEP_DURATION" "$MAX_SWEEP_SECONDS"
assert_duration_within_limit "total" "$TOTAL_DURATION" "$MAX_TOTAL_SECONDS"

TIMESTAMP_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

cat > "$SUMMARY_REPORT" <<EOF
{
  "timestampUtc": "$TIMESTAMP_UTC",
  "profile": "$PROFILE",
  "status": "passed",
  "fuzzIterations": $FUZZ_ITERATIONS,
  "perfDurationMs": $PERF_DURATION_MS,
  "perfAllocationBytes": $PERF_ALLOC_BYTES,
  "maxPerfDurationMs": $MAX_PERF_DURATION_MS,
  "maxPerfAllocationBytes": $MAX_PERF_ALLOC_BYTES,
  "buildDurationSeconds": $BUILD_DURATION,
  "testDurationSeconds": $TEST_DURATION,
  "sweepDurationSeconds": $SWEEP_DURATION,
  "totalDurationSeconds": $TOTAL_DURATION,
  "maxBuildSeconds": $MAX_BUILD_SECONDS,
  "maxTestSeconds": $MAX_TEST_SECONDS,
  "maxSweepSeconds": $MAX_SWEEP_SECONDS,
  "maxTotalSeconds": $MAX_TOTAL_SECONDS,
  "generatorProject": "$GENERATOR_PROJECT",
  "testProject": "$TEST_PROJECT",
  "sweepReport": "$SWEEP_REPORT",
  "perfReport": "$PERF_REPORT"
}
EOF

if [ -n "$BENCHMARK_HOOK" ]; then
  if [ ! -f "$BENCHMARK_HOOK" ]; then
    echo "DBus quality gate benchmark hook script was not found: $BENCHMARK_HOOK" >&2
    exit 1
  fi

  bash "$BENCHMARK_HOOK" "$SUMMARY_REPORT"
fi

echo "DBus generator quality gate passed."
echo "Summary report: $SUMMARY_REPORT"
