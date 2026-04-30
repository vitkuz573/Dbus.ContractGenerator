# Repository Guidelines

## Project Structure & Module Organization
This submodule is a Roslyn incremental source generator for D-Bus contracts.

- `src/Dbus.ContractGenerator/`: generator implementation, split by concern (`Parsing/`, `Emission/`, `Validation/`, `Utilities/`, `Configuration/`, `Models/`).
- `tests/Dbus.ContractGenerator.Tests/`: xUnit test suite plus corpus fixtures under `TestData/DbusGeneratorCorpus/`.
- `scripts/`: operational checks (`dbus-generator-quality-gate.sh`, `dbus-generator-sweep.sh`, `dbus-generator-benchmark-hook.sh`).
- `Dbus.ContractGenerator.slnx`: solution entry for restore/build/test.

## Build, Test, and Development Commands
Run from `submodules/Dbus.ContractGenerator`:

- `dotnet restore Dbus.ContractGenerator.slnx`: restore dependencies.
- `dotnet build Dbus.ContractGenerator.slnx -c Release`: build generator and tests.
- `dotnet test Dbus.ContractGenerator.slnx -c Release`: run full test suite.
- `dotnet test tests/Dbus.ContractGenerator.Tests/Dbus.ContractGenerator.Tests.csproj -c Release --filter "FullyQualifiedName~DbusContractSourceGenerator"`: targeted generator tests.
- `./scripts/dbus-generator-quality-gate.sh`: full gate (build, tests, perf budget, Linux sweep).
- `./scripts/dbus-generator-sweep.sh`: standalone real-system `busctl` sweep.

## Coding Style & Naming Conventions
- C# uses 4-space indentation, file-scoped namespaces, `nullable` enabled, and `ImplicitUsings` enabled.
- Prefer small partial files grouped by responsibility (for example `DbusContractSourceGenerator.*.cs`).
- Use `PascalCase` for types/methods/properties, `camelCase` for locals/parameters.
- Keep diagnostics and constants centralized in `DbusGeneratorConstants.cs`; use stable IDs like `DBCG001`.

## Testing Guidelines
- Framework: xUnit with `Microsoft.NET.Test.Sdk` and `coverlet.collector`.
- Test naming pattern: `Action_WithCondition_ExpectedOutcome` (example: `Generate_WithMergePolicyFail_ReportsConflictError`).
- Keep corpus-driven fixtures deterministic; update snapshots/manifests only intentionally:
  - `DBUS_GENERATOR_UPDATE_SNAPSHOTS=1`
  - `DBUS_GENERATOR_UPDATE_ABI_MANIFEST=1`

## Commit & Pull Request Guidelines
- Follow Conventional Commit style seen in history: `feat:`, `fix:`, `refactor:`, `docs:`, `chore:`.
- Keep commits scoped and reviewable; include tests with behavioral changes.
- PRs should include: concise summary, why the change is needed, test evidence (command + result), and any diagnostic/config impacts.
- If generator behavior or quality-gate semantics change, update `README.md` in the same PR.
