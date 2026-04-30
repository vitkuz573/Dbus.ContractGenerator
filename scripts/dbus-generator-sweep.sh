#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GENERATOR_PROJECT="$ROOT_DIR/src/Dbus.ContractGenerator/Dbus.ContractGenerator.csproj"

WORK_DIR="${DBUS_SWEEP_WORK_DIR:-/tmp/dbus-contract-generator-sweep}"
PROJECT_DIR="$WORK_DIR/DbusSweep"
XML_LIMIT_PER_BUS="${DBUS_SWEEP_LIMIT_PER_BUS:-500}"
TREE_PATH_LIMIT_PER_SERVICE="${DBUS_SWEEP_TREE_PATH_LIMIT_PER_SERVICE:-120}"
INTROSPECT_TIMEOUT_SECONDS="${DBUS_SWEEP_INTROSPECT_TIMEOUT_SECONDS:-3}"
INCLUDE_STATIC_XML="${DBUS_SWEEP_INCLUDE_STATIC_XML:-true}"
STATIC_XML_LIMIT="${DBUS_SWEEP_STATIC_XML_LIMIT:-2500}"
STATIC_XML_ROOTS="${DBUS_SWEEP_STATIC_XML_ROOTS:-/usr/share /usr/local/share}"
SYNTHETIC_RARE_ENABLED="${DBUS_SWEEP_SYNTHETIC_RARE:-true}"
SYNTHETIC_RARE_COUNT="${DBUS_SWEEP_SYNTHETIC_RARE_COUNT:-3000}"
STRICT_CONFIGURATION="${DBUS_SWEEP_STRICT:-true}"

if ! command -v busctl >/dev/null 2>&1; then
  echo "busctl is required but not found in PATH." >&2
  exit 1
fi

if [ ! -f "$GENERATOR_PROJECT" ]; then
  echo "Generator project not found: $GENERATOR_PROJECT" >&2
  exit 1
fi

require_integer_at_least() {
  local name="$1"
  local value="$2"
  local minimum="$3"

  if ! [[ "$value" =~ ^[0-9]+$ ]]; then
    echo "Sweep configuration '$name' must be an integer (actual: '$value')." >&2
    exit 1
  fi

  if [ "$value" -lt "$minimum" ]; then
    echo "Sweep configuration '$name' must be >= $minimum (actual: '$value')." >&2
    exit 1
  fi
}

is_enabled() {
  local value="$1"
  case "${value,,}" in
    1|true|yes|on)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

is_parseable_xml_file() {
  local xml_path="$1"

  if command -v xmllint >/dev/null 2>&1; then
    local lint_output
    set +e
    lint_output="$(xmllint --noout "$xml_path" 2>&1 >/dev/null)"
    local lint_status="$?"
    set -e
    if [ "$lint_status" -ne 0 ] || [ -n "$lint_output" ]; then
      return 1
    fi

    return 0
  fi

  if command -v perl >/dev/null 2>&1; then
    perl -ne 'while (/&([A-Za-z_][A-Za-z0-9_.:-]*);/g) { exit 1 if $1 !~ /^(amp|lt|gt|quot|apos)$/ }' "$xml_path" >/dev/null 2>&1
    return
  fi

  return 0
}

require_integer_at_least "DBUS_SWEEP_LIMIT_PER_BUS" "$XML_LIMIT_PER_BUS" 0
require_integer_at_least "DBUS_SWEEP_TREE_PATH_LIMIT_PER_SERVICE" "$TREE_PATH_LIMIT_PER_SERVICE" 1
require_integer_at_least "DBUS_SWEEP_INTROSPECT_TIMEOUT_SECONDS" "$INTROSPECT_TIMEOUT_SECONDS" 1
require_integer_at_least "DBUS_SWEEP_STATIC_XML_LIMIT" "$STATIC_XML_LIMIT" 0
require_integer_at_least "DBUS_SWEEP_SYNTHETIC_RARE_COUNT" "$SYNTHETIC_RARE_COUNT" 0

if [ -d "$WORK_DIR" ]; then
  find "$WORK_DIR" -mindepth 1 -delete
else
  mkdir -p "$WORK_DIR"
fi
dotnet new classlib -n DbusSweep -o "$PROJECT_DIR" >/dev/null
mkdir -p "$PROJECT_DIR/Dbus/system" "$PROJECT_DIR/Dbus/user" "$PROJECT_DIR/Dbus/static" "$PROJECT_DIR/Dbus/synthetic"

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

  if [ "$XML_LIMIT_PER_BUS" -eq 0 ]; then
    echo "0"
    return
  fi

  services="$(busctl --"$bus_name" list --no-pager --no-legend 2>/dev/null | awk '{print $1}' | grep -v '^:' | sort -u || true)"
  for service in $services; do
    local paths
    paths="$(busctl --"$bus_name" tree --no-pager --list "$service" 2>/dev/null | head -n "$TREE_PATH_LIMIT_PER_SERVICE" || true)"
    if [ -z "$paths" ]; then
      paths="/"
    fi

    while IFS= read -r object_path; do
      [ -z "$object_path" ] && continue
      local xml
      xml="$(busctl --"$bus_name" introspect --xml-interface --timeout="$INTROSPECT_TIMEOUT_SECONDS" "$service" "$object_path" 2>/dev/null || true)"
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

collect_static_xml() {
  local out_dir="$1"
  local total=0

  if ! is_enabled "$INCLUDE_STATIC_XML" || [ "$STATIC_XML_LIMIT" -eq 0 ]; then
    echo "0"
    return
  fi

  for static_root in $STATIC_XML_ROOTS; do
    if [ ! -d "$static_root" ]; then
      continue
    fi

    while IFS= read -r xml_path; do
      [ -z "$xml_path" ] && continue
      if ! grep -Eq "<node([[:space:]>]|$)" "$xml_path" 2>/dev/null ||
         ! grep -Eq "<interface([[:space:]>]|$)" "$xml_path" 2>/dev/null; then
        continue
      fi

      if ! is_parseable_xml_file "$xml_path"; then
        continue
      fi

      total=$((total + 1))
      cp "$xml_path" "$out_dir/static_${total}.xml"
      if [ "$total" -ge "$STATIC_XML_LIMIT" ]; then
        echo "$total"
        return
      fi
    done < <(find "$static_root" -type f -name '*.xml' 2>/dev/null | sort)
  done

  echo "$total"
}

generate_synthetic_rare_corpus() {
  local out_dir="$1"
  local total=0

  if ! is_enabled "$SYNTHETIC_RARE_ENABLED" || [ "$SYNTHETIC_RARE_COUNT" -eq 0 ]; then
    echo "0"
    return
  fi

  local signatures=(
    "y" "b" "n" "q" "i" "u" "x" "t" "d" "s" "o" "g" "h" "v"
    "ay" "ab" "an" "aq" "ai" "au" "ax" "at" "ad" "as" "ao" "ag" "ah" "av"
    "aay" "aas" "a{ss}" "a{sv}" "a{sa{sv}}" "a{oa{sa{sv}}}" "a(su)" "a(ib)"
    "(su)" "(suv)" "(a{sv}as)" "()" "(s)" "((su)a{sv})"
  )
  local hints=(
    "QString"
    "const QString &amp;"
    "QStringView"
    "QLatin1StringView"
    "QUtf8StringView"
    "QStringList"
    "QByteArray"
    "QByteArrayList"
    "QVariant"
    "QVariantList"
    "QVariantMap"
    "QVariantHash"
    "QDBusVariant"
    "QDBusObjectPath"
    "QDBusSignature"
    "QDBusUnixFileDescriptor"
    "bool"
    "uchar"
    "qint8"
    "quint8"
    "short"
    "unsigned short"
    "int"
    "unsigned int"
    "long long"
    "unsigned long long"
    "qreal"
    "QList&lt;const QString &amp;&gt;"
    "QVector&lt;quint32&gt;"
    "QLinkedList&lt;QDBusObjectPath&gt;"
    "QQueue&lt;QString&gt;"
    "QStack&lt;QByteArray&gt;"
    "QSet&lt;QString&gt;"
    "QMap&lt;QString,QVariant&gt;"
    "QHash&lt;QString,QByteArray&gt;"
    "QPair&lt;quint32,QString&gt;"
    "std::vector&lt;QString&gt;"
    "std::list&lt;quint64&gt;"
    "std::set&lt;QString&gt;"
    "std::map&lt;QString,const QByteArray &amp;&gt;"
    "std::pair&lt;quint32,QString&gt;"
  )

  while [ "$total" -lt "$SYNTHETIC_RARE_COUNT" ]; do
    local index="$total"
    local in_signature="${signatures[$((index % ${#signatures[@]}))]}"
    local out_signature="${signatures[$(((index * 7 + 3) % ${#signatures[@]}))]}"
    local property_signature="${signatures[$(((index * 11 + 5) % ${#signatures[@]}))]}"
    local signal_signature="${signatures[$(((index * 13 + 9) % ${#signatures[@]}))]}"
    local in_hint="${hints[$((index % ${#hints[@]}))]}"
    local out_hint="${hints[$(((index * 5 + 1) % ${#hints[@]}))]}"
    local property_hint="${hints[$(((index * 7 + 2) % ${#hints[@]}))]}"
    local signal_hint="${hints[$(((index * 11 + 4) % ${#hints[@]}))]}"
    local case_number
    case_number="$(printf '%05d' "$index")"

    cat > "$out_dir/rare_${case_number}.xml" <<EOF
<node name="/org/example/RareSweep/${case_number}">
  <interface name="org.example.RareSweep.Case${case_number}">
    <method name="Execute${case_number}">
      <annotation name="org.qtproject.QtDBus.QtTypeName.In0" value="$in_hint" />
      <annotation name="org.qtproject.QtDBus.QtTypeName.Out0" value="$out_hint" />
      <arg direction="in" name="input" type="$in_signature" />
      <arg direction="out" name="result" type="$out_signature" />
    </method>
    <property name="State${case_number}" type="$property_signature" access="readwrite">
      <annotation name="org.qtproject.QtDBus.QtTypeName" value="$property_hint" />
    </property>
    <signal name="Changed${case_number}">
      <annotation name="org.qtproject.QtDBus.QtTypeName.Out0" value="$signal_hint" />
      <arg name="payload" type="$signal_signature" />
    </signal>
  </interface>
</node>
EOF

    total=$((total + 1))
  done

  echo "$total"
}

SYSTEM_COUNT="$(collect_bus system "$PROJECT_DIR/Dbus/system")"
USER_COUNT="$(collect_bus user "$PROJECT_DIR/Dbus/user")"
STATIC_COUNT="$(collect_static_xml "$PROJECT_DIR/Dbus/static")"
SYNTHETIC_COUNT="$(generate_synthetic_rare_corpus "$PROJECT_DIR/Dbus/synthetic")"
TOTAL_COUNT="$(find "$PROJECT_DIR/Dbus" -type f -name '*.xml' | wc -l | tr -d '[:space:]')"

echo "Collected D-Bus XML files:"
echo "  system:    $SYSTEM_COUNT"
echo "  user:      $USER_COUNT"
echo "  static:    $STATIC_COUNT"
echo "  synthetic: $SYNTHETIC_COUNT"
echo "  total:     $TOTAL_COUNT"

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
  "treePathLimitPerService": $TREE_PATH_LIMIT_PER_SERVICE,
  "introspectTimeoutSeconds": $INTROSPECT_TIMEOUT_SECONDS,
  "includeStaticXml": "$INCLUDE_STATIC_XML",
  "staticXmlLimit": $STATIC_XML_LIMIT,
  "staticXmlRoots": "$STATIC_XML_ROOTS",
  "syntheticRareEnabled": "$SYNTHETIC_RARE_ENABLED",
  "syntheticRareCountLimit": $SYNTHETIC_RARE_COUNT,
  "systemCount": $SYSTEM_COUNT,
  "userCount": $USER_COUNT,
  "staticCount": $STATIC_COUNT,
  "syntheticCount": $SYNTHETIC_COUNT,
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
