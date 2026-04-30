# Dbus.ContractGenerator

`Dbus.ContractGenerator` is a Roslyn incremental generator that converts D-Bus introspection XML into deterministic C# contract code.

The generated code targets `Dbus.Contracts` runtime primitives:
- `DbusInterfaceAttribute`
- `IDbusObject`
- `DbusObjectPath`
- `DbusPropertyChanges`
- `DbusDictionaryAttribute`

## Goals

- Deterministic generation from the same XML/config inputs.
- Strict, diagnosable behavior with explicit analyzer IDs (`DBCG001`-`DBCG013`).
- Breaking-by-design evolution (no legacy compatibility mode in the current generator).
- Production-oriented validation and quality gates (fuzz, ABI baseline, perf budget, system D-Bus sweep).

## Requirements

- `.NET SDK 10.x` (for solution/test tooling).
- Linux with `busctl` for the sweep script (`scripts/dbus-generator-sweep.sh`).

## Quick Start

1. Reference the generator as an analyzer in your project:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/Dbus.ContractGenerator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>

<ItemGroup>
  <AdditionalFiles Include="Dbus/**/*.xml" />
  <AdditionalFiles Include="Dbus/dbus-contract-generator.json" />
</ItemGroup>
```

2. Place D-Bus XML under a path containing `/Dbus/` (case-insensitive).
3. Add optional configuration file `Dbus/dbus-contract-generator.json`.
4. Build the consumer project; generated files are added by Roslyn.

## Input Contract

- XML input rule: file extension must be `.xml`.
- XML input rule: file path must contain `/Dbus/` (case-insensitive).
- Configuration input rule: file name must be `dbus-contract-generator.json`.
- Configuration input rule: path must end with `/Dbus/dbus-contract-generator.json` (case-insensitive).
- Multiple configuration files rule: the first file by ordinal path sort is used.
- Multiple configuration files rule: a diagnostic is reported.

## Output Shape

For each D-Bus interface, the generator emits:
- One C# interface with method/signal members.
- One dictionary-style properties class.
- One extension class with property helper methods.
- `DefaultObjectPath` and `KnownObjectPaths` extracted from recursive XML nodes.

The generator applies:
- Name sanitization and keyword escaping.
- Deterministic ordering.
- Merge semantics when an interface is defined in multiple files.

## Configuration Schema

`schemaVersion` currently supports only `1`.

Top-level properties:

| Property | Type | Default | Notes |
|---|---|---|---|
| `schemaVersion` | `int` | `1` | Must be exactly `1`. |
| `strictConfiguration` | `bool` | `true` | Unknown properties emit warning always; with `true` they also emit error. |
| `strictAbiCompatibility` | `bool` | `false` | When `true`, conflicting interface signatures fail instead of merge. |
| `generatedNamespace` | `string` | `Dbus.Contracts.Generated` | Must be valid C# namespace. |
| `mergePolicy` | `string` | `merge-union` | `fail`, `warn`, `merge-prefer-first`, `merge-union`. |
| `naming` | `object` | see below | Naming strategy configuration. |
| `preferQtTypeHints` | `bool` | `true` | Enables Qt hint-driven type mapping preference. |
| `qtTypeHintPolicy` | `object` | see below | Unknown-hint behavior and optional allow-lists. |
| `qtTypeHintMappings` | `object<string,string>` | built-in map | Custom Qt hint -> CLR type mapping. |
| `ignoredInterfaces` | `string[]` | `org.freedesktop.DBus.Peer`, `org.freedesktop.DBus.Introspectable`, `org.freedesktop.DBus.Properties` | Excluded interfaces. |
| `includedInterfaces` | `string[]` | `null` | Allow-list mode. Mutually exclusive with `ignoredInterfaces`. |
| `interfaceTypeNameOverrides` | `object<string,string>` | `{}` | D-Bus interface name -> C# type name. |
| `interfaceMembers` | `object` | `{}` | Per-interface member filtering (`methods`/`properties`/`signals`). |

`naming` properties:

| Property | Type | Default | Notes |
|---|---|---|---|
| `interfacePrefix` | `string` | `""` | Added before resolved interface type name. |
| `interfaceSuffix` | `string` | `""` | Added after resolved interface type name. |
| `collisionPolicy` | `string` | `hash` | `suffix`, `prefix`, `hash`. |
| `hashLength` | `int` | `8` | Valid range: `4..32`. |

`qtTypeHintPolicy` properties:

| Property | Type | Default | Notes |
|---|---|---|---|
| `unknownHintBehavior` | `string` | `allow` | `allow`, `warn`, `error`. |
| `allowedClrNamespaces` | `string[]` | `null` | Optional namespace allow-list for mapped CLR types. |
| `allowedGenericTypeDefinitions` | `string[]` | `null` | Optional allow-list for generic root type definitions. |

`interfaceMembers` shape:

```json
{
  "interfaceMembers": {
    "org.example.InterfaceName": {
      "methods": ["Ping", "Pong"],
      "properties": ["Status"],
      "signals": ["Updated"]
    }
  }
}
```

Unknown member names report `DBCG006`.

Intrinsic generic Qt hint resolution (no explicit mapping required):
- `const`, references, and pointers are stripped before resolving hints.
- `QList<T>`, `QVector<T>`, `QLinkedList<T>`, `QQueue<T>`, `QStack<T>`, `std::vector<T>`, `std::list<T>` -> `T[]`
- `QSet<T>`, `std::set<T>` -> `System.Collections.Generic.ISet<T>`
- `QMap<K,V>`, `QHash<K,V>`, `std::map<K,V>` -> `System.Collections.Generic.IDictionary<K, V>`
- `QPair<A,B>`, `std::pair<A,B>` -> `(A, B)`

## Default Qt Type Hint Mappings

Built-in defaults:
- `QVariant` -> `object`
- `QString` -> `string`
- `QStringView`, `QLatin1StringView`, `QUtf8StringView`, `QAnyStringView` -> `string`
- `QStringList` -> `string[]`
- `QByteArray` -> `byte[]`
- `QByteArrayList` -> `byte[][]`
- `QVariantList` -> `object[]`
- `QVariantMap` -> `System.Collections.Generic.IDictionary<string, object>`
- `QVariantHash` -> `System.Collections.Generic.IDictionary<string, object>`
- `QDBusVariant` -> `object`
- `QDBusObjectPath` -> `DbusObjectPath`
- `QDBusSignature` -> `string`
- `QDBusUnixFileDescriptor` -> `CloseSafeHandle`
- `bool` -> `bool`
- `uchar`, `quint8`, `uint8_t` -> `byte`
- `qint8`, `int8_t` -> `sbyte`
- `double` -> `double`
- `qreal` -> `double`
- `qint16` -> `short`
- `quint16`, `unsigned short`, `uint16_t` -> `ushort`
- `qint32` -> `int`
- `quint32`, `uint`, `unsigned int`, `uint32_t` -> `uint`
- `qint64`, `qlonglong`, `long long`, `int64_t` -> `long`
- `quint64`, `qulonglong`, `unsigned long long`, `uint64_t` -> `ulong`

## Merge and Compatibility Behavior

When the same D-Bus interface is discovered in multiple XML files:
- `strictAbiCompatibility=true`: conflicts report `DBCG003` and conflicting definitions are not merged.
- `strictAbiCompatibility=false`: merge is controlled by `mergePolicy`.
- `mergePolicy=warn`: reports `DBCG009`.
- `mergePolicy=fail`: reports `DBCG003`.
- `mergePolicy=merge-prefer-first` and `mergePolicy=merge-union`: merge deterministically.
- `mergePolicy=merge-union` with `strictConfiguration=true`: incompatible unions can escalate to `DBCG003`.

## Compatibility Matrix

### D-Bus Signature Support

| Category | Signature(s) | Support | Generated CLR Type | Notes |
|---|---|---|---|---|
| Primitive | `y` | Full | `byte` |  |
| Primitive | `b` | Full | `bool` |  |
| Primitive | `n` | Full | `short` |  |
| Primitive | `q` | Full | `ushort` |  |
| Primitive | `i` | Full | `int` |  |
| Primitive | `u` | Full | `uint` |  |
| Primitive | `x` | Full | `long` |  |
| Primitive | `t` | Full | `ulong` |  |
| Primitive | `d` | Full | `double` |  |
| Primitive | `s` | Full | `string` |  |
| Primitive | `o` | Full | `DbusObjectPath` |  |
| Primitive | `g` | Full | `string` |  |
| Primitive | `h` | Full | `CloseSafeHandle` |  |
| Primitive | `v` | Full | `object` |  |
| Array | `aT` | Full | `T[]` | `T` must be supported type. |
| Dictionary | `a{KV}` | Full | `IDictionary<K,V>` | `K`, `V` must be supported types. |
| Struct | `(T1...Tn)` (`n>=2`) | Full | `(T1, ..., Tn)` | Named tuple elements generated deterministically. |
| Struct | `(T)` (`n=1`) | Full | `System.ValueTuple<T>` | Explicit single-element tuple handling. |
| Struct | `()` (`n=0`) | Full | `System.ValueTuple` |  |
| Unsupported token | any other token | Rejected | N/A | Reports `DBCG002`. |

### Qt Type Hint Compatibility

| Hint Pattern | Support | Result | Notes |
|---|---|---|---|
| Exact mapped hint (`QString`, `QVariantMap`, etc.) | Full | Uses `qtTypeHintMappings` CLR type | Includes built-in defaults and custom mappings. |
| C++ decorated hints (`const T&`, `T*`) | Full | Normalized `T` mapping | Applies recursively inside generic hints. |
| Qt/C++ sequence hints (`QList<T>`, `QVector<T>`, `std::vector<T>`, etc.) | Full | `T[]` | `T` resolved recursively through mapping/parser. |
| Qt/C++ set hints (`QSet<T>`, `std::set<T>`, etc.) | Full | `System.Collections.Generic.ISet<T>` | `T` resolved recursively. |
| Qt/C++ map hints (`QMap<K,V>`, `QHash<K,V>`, `std::map<K,V>`, etc.) | Full | `System.Collections.Generic.IDictionary<K,V>` | `K`, `V` resolved recursively. |
| Qt/C++ pair hints (`QPair<A,B>`, `std::pair<A,B>`) | Full | `(A, B)` | `A`, `B` resolved recursively. |
| Unknown hint + `unknownHintBehavior=allow` | Allowed | Falls back to signature-driven type | No diagnostic. |
| Unknown hint + `unknownHintBehavior=warn` | Conditional | Falls back to signature-driven type | Reports `DBCG012`. |
| Unknown hint + `unknownHintBehavior=error` | Conditional | Falls back to signature-driven type | Reports `DBCG013`. |

### Merge/Conflict Compatibility

| Mode | Conflict Handling | Diagnostic Behavior | Build Impact |
|---|---|---|---|
| `strictAbiCompatibility=true` | No merge | `DBCG003` | Error |
| `strictAbiCompatibility=false`, `mergePolicy=fail` | No merge | `DBCG003` | Error |
| `strictAbiCompatibility=false`, `mergePolicy=warn` | Deterministic merge | `DBCG009` | Warning |
| `strictAbiCompatibility=false`, `mergePolicy=merge-prefer-first` | Deterministic merge | None by default | Compiles |
| `strictAbiCompatibility=false`, `mergePolicy=merge-union` | Deterministic merge | Optional `DBCG003` in strict config edge cases | Usually compiles |

## Diagnostics

| ID | Severity | Meaning |
|---|---|---|
| `DBCG001` | Error | Invalid XML document. |
| `DBCG002` | Error | Invalid/unsupported D-Bus signature. |
| `DBCG003` | Error | Conflicting interface definitions. |
| `DBCG004` | Error | Missing interface metadata. |
| `DBCG005` | Error | Invalid generator configuration. |
| `DBCG006` | Error | Invalid interface member filter. |
| `DBCG007` | Error | Unknown configured interface name. |
| `DBCG008` | Warning | Unknown configuration property. |
| `DBCG009` | Warning | Conflict merged according to policy. |
| `DBCG010` | Error | Invalid contract semantics. |
| `DBCG011` | Error | Invalid annotation usage. |
| `DBCG012` | Warning | Qt hint policy warning. |
| `DBCG013` | Error | Qt hint policy error. |

## Build and Test

```bash
dotnet restore Dbus.ContractGenerator.slnx
dotnet build Dbus.ContractGenerator.slnx -c Release
dotnet test Dbus.ContractGenerator.slnx -c Release
```

Targeted test project:

```bash
dotnet test tests/Dbus.ContractGenerator.Tests/Dbus.ContractGenerator.Tests.csproj -c Release
```

## Quality Gate and Ops Scripts

Full quality gate:

```bash
./scripts/dbus-generator-quality-gate.sh
```

What it runs:
- Build generator.
- Run test suite (including fuzz/perf checks).
- Validate performance budget report.
- Run real-system D-Bus sweep compile check.
- Emit JSON summary report.

Sweep only:

```bash
./scripts/dbus-generator-sweep.sh
```

Sweep behavior notes:
- The sweep combines live `busctl` introspection, installed introspection XML, and a synthetic rare-case corpus.
- The generated temporary sweep configuration sets `mergePolicy` to `warn` to keep broad multi-service Linux sweeps compilable while still surfacing merge conflicts as diagnostics.
- `DBUS_SWEEP_STRICT` controls `strictConfiguration` in the temporary sweep config.

Useful quality-gate environment variables:
- `DBUS_QUALITY_GATE_PROFILE` (`ci` or `nightly`).
- `DBUS_QUALITY_GATE_FUZZ_ITERATIONS`.
- `DBUS_QUALITY_GATE_MAX_BUILD_SECONDS`.
- `DBUS_QUALITY_GATE_MAX_TEST_SECONDS`.
- `DBUS_QUALITY_GATE_MAX_SWEEP_SECONDS`.
- `DBUS_QUALITY_GATE_MAX_TOTAL_SECONDS`.
- `DBUS_QUALITY_GATE_MAX_PERF_DURATION_MS`.
- `DBUS_QUALITY_GATE_MAX_PERF_ALLOC_BYTES`.
- `DBUS_QUALITY_GATE_REPORT_DIR`.
- `DBUS_QUALITY_GATE_FUZZ_FAILURE_DIR`.
- `DBUS_QUALITY_GATE_BENCHMARK_HOOK`.
- `DBUS_QUALITY_GATE_BENCHMARK_DIR` (used by benchmark hook script).

Useful sweep environment variables:
- `DBUS_SWEEP_WORK_DIR`.
- `DBUS_SWEEP_LIMIT_PER_BUS`.
- `DBUS_SWEEP_TREE_PATH_LIMIT_PER_SERVICE`.
- `DBUS_SWEEP_INTROSPECT_TIMEOUT_SECONDS`.
- `DBUS_SWEEP_INCLUDE_STATIC_XML` (`true`/`false`).
- `DBUS_SWEEP_STATIC_XML_LIMIT`.
- `DBUS_SWEEP_STATIC_XML_ROOTS` (space-separated roots).
- `DBUS_SWEEP_SYNTHETIC_RARE` (`true`/`false`).
- `DBUS_SWEEP_SYNTHETIC_RARE_COUNT`.
- `DBUS_SWEEP_STRICT`.
- `DBUS_SWEEP_REPORT`.

Useful test-only environment variables:
- `DBUS_GENERATOR_UPDATE_SNAPSHOTS` (`1`/`true`) updates snapshot files.
- `DBUS_GENERATOR_UPDATE_ABI_MANIFEST` (`1`/`true`) updates ABI manifest.
- `DBUS_GENERATOR_FUZZ_SEED`.
- `DBUS_GENERATOR_FUZZ_ITERATIONS`.
- `DBUS_GENERATOR_FUZZ_PROFILE`.
- `DBUS_GENERATOR_FUZZ_FAILURE_DIR`.
- `DBUS_GENERATOR_PERF_ITERATIONS`.
- `DBUS_GENERATOR_PERF_MAX_MS`.
- `DBUS_GENERATOR_PERF_MAX_ALLOC_BYTES`.
- `DBUS_GENERATOR_PERF_REPORT_PATH`.

## Repository Layout

| Path | Purpose |
|---|---|
| `src/Dbus.ContractGenerator` | Generator implementation. |
| `tests/Dbus.ContractGenerator.Tests` | Unit, corpus, fuzz, ABI, perf tests. |
| `scripts/dbus-generator-quality-gate.sh` | Full gate runner and report emitter. |
| `scripts/dbus-generator-sweep.sh` | Linux busctl-based D-Bus corpus sweep. |
| `scripts/dbus-generator-benchmark-hook.sh` | Optional benchmark artifact snapshot hook. |

## Enterprise Usage Notes

- Keep `strictConfiguration=true` in CI for fail-fast config drift detection.
- Pin and review `qtTypeHintMappings` centrally for deterministic contracts.
- Treat `DBCG003`, `DBCG005`, `DBCG010`, `DBCG011`, `DBCG013` as release blockers.
- Run full quality gate before merging generator changes.
