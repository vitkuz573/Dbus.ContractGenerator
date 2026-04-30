
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Dbus.ContractGenerator;

public sealed partial class DbusContractSourceGenerator
{
    private static void ValidateConfiguredInterfaces(
        SourceProductionContext context,
        GeneratorConfiguration configuration,
        ImmutableHashSet<string> discoveredInterfaceNames)
    {
        ValidateConfiguredInterfacesInSection(
            context,
            configuration.ConfigurationPath,
            discoveredInterfaceNames,
            configuration.ConfiguredIgnoredInterfaces,
            "ignoredInterfaces");
        ValidateConfiguredInterfacesInSection(
            context,
            configuration.ConfigurationPath,
            discoveredInterfaceNames,
            configuration.ConfiguredIncludedInterfaces,
            "includedInterfaces");
        ValidateConfiguredInterfacesInSection(
            context,
            configuration.ConfigurationPath,
            discoveredInterfaceNames,
            configuration.ConfiguredOverrideInterfaces,
            "interfaceTypeNameOverrides");
        ValidateConfiguredInterfacesInSection(
            context,
            configuration.ConfigurationPath,
            discoveredInterfaceNames,
            configuration.ConfiguredMemberFilterInterfaces,
            "interfaceMembers");
    }

    private static void ValidateConfiguredInterfacesInSection(
        SourceProductionContext context,
        string configurationPath,
        ImmutableHashSet<string> discoveredInterfaceNames,
        ImmutableHashSet<string> configuredInterfaceNames,
        string sectionName)
    {
        foreach (var interfaceName in configuredInterfaceNames.OrderBy(static item => item, StringComparer.Ordinal))
        {
            if (discoveredInterfaceNames.Contains(interfaceName))
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    UnknownConfiguredInterface,
                    Location.None,
                    configurationPath,
                    interfaceName,
                    sectionName));
        }
    }

    private static void ValidateTypeNameCollisions(
        SourceProductionContext context,
        string configurationPath,
        IEnumerable<DbusInterfaceModel> interfaceModels)
    {
        var interfaceTypeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var propertyTypeOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var extensionTypeOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var interfaceModel in interfaceModels.OrderBy(static item => item.InterfaceName, StringComparer.Ordinal))
        {
            ValidateTypeNameCollision(
                context,
                configurationPath,
                interfaceTypeOwners,
                interfaceModel.InterfaceTypeName,
                interfaceModel.InterfaceName,
                "interface");
            ValidateTypeNameCollision(
                context,
                configurationPath,
                propertyTypeOwners,
                interfaceModel.PropertyTypeName,
                interfaceModel.InterfaceName,
                "property container");
            ValidateTypeNameCollision(
                context,
                configurationPath,
                extensionTypeOwners,
                interfaceModel.ExtensionTypeName,
                interfaceModel.InterfaceName,
                "extension");
        }
    }

    private static void ValidateTypeNameCollision(
        SourceProductionContext context,
        string configurationPath,
        IDictionary<string, string> typeOwners,
        string typeName,
        string interfaceName,
        string artifactKind)
    {
        if (!typeOwners.TryGetValue(typeName, out var existingInterfaceName))
        {
            typeOwners[typeName] = interfaceName;
            return;
        }

        if (string.Equals(existingInterfaceName, interfaceName, StringComparison.Ordinal))
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                InvalidGeneratorConfiguration,
                Location.None,
                configurationPath,
                $"Generated {artifactKind} type '{typeName}' conflicts between interfaces '{existingInterfaceName}' and '{interfaceName}'."));
    }

    private static bool IsValidNamespace(string namespaceValue)
    {
        var rawSegments = namespaceValue.Split(
            ['.'],
            StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
        {
            return false;
        }

        foreach (var rawSegment in rawSegments)
        {
            var segment = rawSegment.Trim();
            if (!IsValidIdentifier(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
