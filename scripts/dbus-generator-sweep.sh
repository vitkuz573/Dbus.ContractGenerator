#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GENERATOR_PROJECT="$ROOT_DIR/src/Dbus.ContractGenerator/Dbus.ContractGenerator.csproj"

WORK_DIR="${DBUS_SWEEP_WORK_DIR:-/tmp/dbus-contract-generator-sweep}"
PROJECT_DIR="$WORK_DIR/DbusSweep"
XML_LIMIT_PER_BUS="${DBUS_SWEEP_LIMIT_PER_BUS:-140}"
STRICT_CONFIGURATION="${DBUS_SWEEP_STRICT:-true}"

if ! command -v busctl >/dev/null 2>&1; then
  echo "busctl is required but not found in PATH." >&2
  exit 1
fi

if [ ! -f "$GENERATOR_PROJECT" ]; then
  echo "Generator project not found: $GENERATOR_PROJECT" >&2
  exit 1
fi

if [ -d "$WORK_DIR" ]; then
  find "$WORK_DIR" -mindepth 1 -delete
else
  mkdir -p "$WORK_DIR"
fi
dotnet new classlib -n DbusSweep -o "$PROJECT_DIR" >/dev/null
mkdir -p "$PROJECT_DIR/Dbus/system" "$PROJECT_DIR/Dbus/user"

cat > "$PROJECT_DIR/DbusSweep.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>obj/Generated</CompilerGeneratedFilesOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="$GENERATOR_PROJECT"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

  <ItemGroup>
    <AdditionalFiles Include="Dbus/**/*.xml" />
    <AdditionalFiles Include="Dbus/dbus-contract-generator.json" />
  </ItemGroup>
</Project>
EOF

cat > "$PROJECT_DIR/Dbus.Contracts.Stubs.cs" <<EOF
using System;

namespace Dbus.Contracts;

[AttributeUsage(AttributeTargets.Interface)]
public sealed class DbusInterfaceAttribute : Attribute
{
    public DbusInterfaceAttribute(string interfaceName)
    {
    }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class DbusDictionaryAttribute : Attribute
{
}

public interface IDbusObject
{
}

public readonly struct DbusObjectPath
{
}

public sealed class CloseSafeHandle
{
}

public sealed class DbusPropertyChanges
{
}
EOF

cat > "$PROJECT_DIR/Dbus/dbus-contract-generator.json" <<EOF
{
  "schemaVersion": 1,
  "strictConfiguration": $STRICT_CONFIGURATION,
  "mergePolicy": "warn",
  "generatedNamespace": "DbusSweep.Generated"
}
EOF

collect_bus() {
  local bus_name="$1"
  local out_dir="$2"
  local total=0
  local services

  services="$(busctl --"$bus_name" list --no-pager --no-legend 2>/dev/null | awk '{print $1}' | grep -v '^:' | sort -u || true)"
  for service in $services; do
    local paths
    paths="$(busctl --"$bus_name" tree --no-pager --list "$service" 2>/dev/null | head -n 20 || true)"
    if [ -z "$paths" ]; then
      paths="/"
    fi

    while IFS= read -r object_path; do
      [ -z "$object_path" ] && continue
      local xml
      xml="$(busctl --"$bus_name" introspect --xml-interface --timeout=3 "$service" "$object_path" 2>/dev/null || true)"
      [ -z "$xml" ] && continue
      if ! grep -q "<interface " <<<"$xml"; then
        continue
      fi

      local safe_name
      safe_name="$(printf '%s__%s' "$service" "$object_path" | tr '/:. -' '_' | tr -s '_')"
      printf '%s\n' "$xml" > "$out_dir/${safe_name}.xml"

      total=$((total + 1))
      if [ "$total" -ge "$XML_LIMIT_PER_BUS" ]; then
        echo "$total"
        return
      fi
    done <<<"$paths"
  done

  echo "$total"
}

SYSTEM_COUNT="$(collect_bus system "$PROJECT_DIR/Dbus/system")"
USER_COUNT="$(collect_bus user "$PROJECT_DIR/Dbus/user")"
TOTAL_COUNT="$(find "$PROJECT_DIR/Dbus" -type f -name '*.xml' | wc -l | tr -d '[:space:]')"

echo "Collected D-Bus XML files:"
echo "  system: $SYSTEM_COUNT"
echo "  user:   $USER_COUNT"
echo "  total:  $TOTAL_COUNT"

dotnet build "$PROJECT_DIR/DbusSweep.csproj" -c Release

GENERATED_FILE_COUNT="$(find "$PROJECT_DIR/obj/Generated" -type f -name '*.cs' 2>/dev/null | wc -l | tr -d '[:space:]')"
REPORT_PATH="${DBUS_SWEEP_REPORT:-}"
if [ -n "$REPORT_PATH" ]; then
  mkdir -p "$(dirname "$REPORT_PATH")"
  cat > "$REPORT_PATH" <<EOF
{
  "workDir": "$WORK_DIR",
  "projectDir": "$PROJECT_DIR",
  "strictConfiguration": $STRICT_CONFIGURATION,
  "xmlLimitPerBus": $XML_LIMIT_PER_BUS,
  "systemCount": $SYSTEM_COUNT,
  "userCount": $USER_COUNT,
  "totalXmlCount": $TOTAL_COUNT,
  "generatedSourceCount": $GENERATED_FILE_COUNT
}
EOF
fi

echo
echo "Sweep completed successfully."
echo "Project: $PROJECT_DIR"
echo "Generated sources: $PROJECT_DIR/obj/Generated"
echo "Generated source files: $GENERATED_FILE_COUNT"
if [ -n "$REPORT_PATH" ]; then
  echo "Sweep report: $REPORT_PATH"
fi
