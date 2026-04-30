#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "Usage: $0 <quality-gate-summary-report.json>" >&2
  exit 1
fi

SUMMARY_REPORT="$1"
if [ ! -f "$SUMMARY_REPORT" ]; then
  echo "Summary report file was not found: $SUMMARY_REPORT" >&2
  exit 1
fi

BENCHMARK_DIR="${DBUS_QUALITY_GATE_BENCHMARK_DIR:-$(dirname "$SUMMARY_REPORT")/benchmarks}"
TIMESTAMP_UTC="$(date -u +%Y%m%dT%H%M%SZ)"
TARGET_PATH="$BENCHMARK_DIR/$TIMESTAMP_UTC-quality-gate.json"

mkdir -p "$BENCHMARK_DIR"
cp "$SUMMARY_REPORT" "$TARGET_PATH"

echo "Benchmark snapshot saved: $TARGET_PATH"
