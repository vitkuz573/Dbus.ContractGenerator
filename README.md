# Dbus.ContractGenerator

Roslyn incremental source generator that converts D-Bus introspection XML into C# contracts.

Generated contracts target the `Dbus.Contracts` runtime surface (`DbusInterfaceAttribute`, `IDbusObject`, `DbusObjectPath`, `DbusPropertyChanges`, `DbusDictionaryAttribute`).

## Build

```bash
dotnet build Dbus.ContractGenerator.slnx -c Release
```
