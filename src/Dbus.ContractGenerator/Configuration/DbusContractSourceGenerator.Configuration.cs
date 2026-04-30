
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Dbus.ContractGenerator;

public sealed partial class DbusContractSourceGenerator
{
    private static GeneratorConfiguration BuildGeneratorConfiguration(
        SourceProductionContext context,
        ImmutableArray<DbusGeneratorConfigurationFile> configurationFiles)
    {
        var defaultConfiguration = new GeneratorConfiguration(
            DefaultConfigurationFileName,
            strictConfiguration: true,
            schemaVersion: SupportedConfigurationSchemaVersion,
            DefaultGeneratedNamespace,
            mergePolicy: InterfaceMergePolicy.MergeUnion,
            strictAbiCompatibility: false,
            typeNamingStrategy: new TypeNamingStrategy(
                interfacePrefix: string.Empty,
                interfaceSuffix: string.Empty,
                collisionPolicy: TypeNameCollisionPolicy.Hash,
                hashLength: 8),
            preferQtTypeHints: true,
            qtTypeHintPolicy: new QtTypeHintPolicy(
                unknownBehavior: QtTypeHintUnknownBehavior.Allow,
                allowedClrNamespaces: null,
                allowedGenericTypeDefinitions: null),
            qtTypeHintMappings: DefaultQtTypeHintMappings,
            DefaultIgnoredInterfaces,
            includedInterfaces: null,
            DefaultInterfaceTypeNameOverrides,
            ImmutableDictionary<string, InterfaceMemberFilter>.Empty.WithComparers(StringComparer.Ordinal),
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal));

        if (configurationFiles.IsDefaultOrEmpty)
        {
            return defaultConfiguration;
        }

        var orderedConfigurationFiles = configurationFiles
            .Where(static file => !string.IsNullOrWhiteSpace(file.Content))
            .OrderBy(static file => file.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        if (orderedConfigurationFiles.IsDefaultOrEmpty)
        {
            return defaultConfiguration;
        }

        var configurationFile = orderedConfigurationFiles[0];
        if (orderedConfigurationFiles.Length > 1)
        {
            var duplicateFiles = string.Join(", ", orderedConfigurationFiles.Select(static file => file.Path));
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    DefaultConfigurationFileName,
                    $"Multiple configuration files found: {duplicateFiles}. The first file '{configurationFile.Path}' will be used."));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(configurationFile.Content);
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationFile.Path,
                    $"Failed to parse JSON: {ex.Message}"));
            return defaultConfiguration;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationFile.Path,
                        "Root JSON node must be an object."));
                return defaultConfiguration;
            }

            var schemaVersion = ReadSchemaVersion(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.SchemaVersion);
            var strictConfiguration = ReadStrictConfiguration(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.StrictConfiguration);
            ValidateUnknownConfigurationProperties(
                context,
                configurationFile.Path,
                root,
                strictConfiguration);

            var generatedNamespace = ReadGeneratedNamespace(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.GeneratedNamespace);
            var mergePolicy = ReadMergePolicy(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.MergePolicy);
            var strictAbiCompatibility = ReadStrictAbiCompatibility(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.StrictAbiCompatibility);
            var namingStrategy = ReadNamingStrategy(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.TypeNamingStrategy,
                strictConfiguration);
            var preferQtTypeHints = ReadPreferQtTypeHints(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.PreferQtTypeHints);
            var qtTypeHintPolicy = ReadQtTypeHintPolicy(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.QtTypeHintPolicy,
                strictConfiguration);
            var qtTypeHintMappings = ReadQtTypeHintMappings(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.QtTypeHintMappings,
                qtTypeHintPolicy);
            var ignoredInterfaces = ReadIgnoredInterfaces(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.IgnoredInterfaces,
                out var configuredIgnoredInterfaces);
            var includedInterfaces = ReadIncludedInterfaces(
                context,
                configurationFile.Path,
                root,
                out var configuredIncludedInterfaces);
            var interfaceTypeNameOverrides = ReadInterfaceTypeNameOverrides(
                context,
                configurationFile.Path,
                root,
                defaultConfiguration.InterfaceTypeNameOverrides,
                out var configuredOverrideInterfaces);
            var interfaceMemberFilters = ReadInterfaceMemberFilters(
                context,
                configurationFile.Path,
                root,
                strictConfiguration,
                out var configuredMemberFilterInterfaces);

            if (includedInterfaces is not null && configuredIgnoredInterfaces.Count > 0)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationFile.Path,
                        "'includedInterfaces' and 'ignoredInterfaces' are mutually exclusive. Use only one of these properties."));
            }

            if (includedInterfaces is not null)
            {
                var overlaps = includedInterfaces
                    .Intersect(ignoredInterfaces, StringComparer.Ordinal)
                    .OrderBy(static item => item, StringComparer.Ordinal)
                    .ToImmutableArray();
                if (!overlaps.IsDefaultOrEmpty)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            InvalidGeneratorConfiguration,
                            Location.None,
                            configurationFile.Path,
                            $"Interfaces cannot be present in both 'includedInterfaces' and 'ignoredInterfaces': {string.Join(", ", overlaps)}."));
                }
            }

            return new GeneratorConfiguration(
                configurationFile.Path,
                strictConfiguration,
                schemaVersion,
                generatedNamespace,
                mergePolicy,
                strictAbiCompatibility,
                namingStrategy,
                preferQtTypeHints,
                qtTypeHintPolicy,
                qtTypeHintMappings,
                ignoredInterfaces,
                includedInterfaces,
                interfaceTypeNameOverrides,
                interfaceMemberFilters,
                configuredIgnoredInterfaces,
                configuredIncludedInterfaces,
                configuredOverrideInterfaces,
                configuredMemberFilterInterfaces);
        }
    }

    private static int ReadSchemaVersion(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        int fallbackVersion)
    {
        if (!root.TryGetProperty("schemaVersion", out var schemaVersionProperty))
        {
            return fallbackVersion;
        }

        if (schemaVersionProperty.ValueKind != JsonValueKind.Number ||
            !schemaVersionProperty.TryGetInt32(out var schemaVersion))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'schemaVersion' must be an integer."));
            return fallbackVersion;
        }

        if (schemaVersion <= 0)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'schemaVersion' must be greater than zero."));
            return fallbackVersion;
        }

        if (schemaVersion != SupportedConfigurationSchemaVersion)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    $"'schemaVersion' value '{schemaVersion}' is unsupported. Supported value: {SupportedConfigurationSchemaVersion}."));
            return fallbackVersion;
        }

        return schemaVersion;
    }

    private static bool ReadStrictConfiguration(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        bool fallbackStrictConfiguration)
    {
        if (!root.TryGetProperty("strictConfiguration", out var strictProperty))
        {
            return fallbackStrictConfiguration;
        }

        if (strictProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'strictConfiguration' must be a boolean."));
            return fallbackStrictConfiguration;
        }

        return strictProperty.GetBoolean();
    }

    private static bool ReadStrictAbiCompatibility(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        bool fallbackStrictAbiCompatibility)
    {
        if (!root.TryGetProperty("strictAbiCompatibility", out var strictAbiProperty))
        {
            return fallbackStrictAbiCompatibility;
        }

        if (strictAbiProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'strictAbiCompatibility' must be a boolean."));
            return fallbackStrictAbiCompatibility;
        }

        return strictAbiProperty.GetBoolean();
    }

    private static void ValidateUnknownConfigurationProperties(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        bool strictConfiguration)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (KnownConfigurationProperties.Contains(property.Name))
            {
                continue;
            }

            ReportUnknownConfigurationProperty(
                context,
                configurationPath,
                property.Name,
                $"$.{property.Name}",
                strictConfiguration);
        }
    }

    private static string ReadGeneratedNamespace(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        string fallbackNamespace)
    {
        if (!root.TryGetProperty("generatedNamespace", out var namespaceProperty))
        {
            return fallbackNamespace;
        }

        if (namespaceProperty.ValueKind != JsonValueKind.String)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'generatedNamespace' must be a string."));
            return fallbackNamespace;
        }

        var generatedNamespace = namespaceProperty.GetString();
        if (string.IsNullOrWhiteSpace(generatedNamespace))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'generatedNamespace' must not be empty."));
            return fallbackNamespace;
        }

        var normalizedNamespace = generatedNamespace!.Trim();
        if (!IsValidNamespace(normalizedNamespace))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    $"'generatedNamespace' value '{normalizedNamespace}' is not a valid C# namespace."));
            return fallbackNamespace;
        }

        return normalizedNamespace;
    }

    private static InterfaceMergePolicy ReadMergePolicy(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        InterfaceMergePolicy fallbackPolicy)
    {
        if (!root.TryGetProperty("mergePolicy", out var mergePolicyProperty))
        {
            return fallbackPolicy;
        }

        if (mergePolicyProperty.ValueKind != JsonValueKind.String)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'mergePolicy' must be a string."));
            return fallbackPolicy;
        }

        var mergePolicy = mergePolicyProperty.GetString();
        if (string.IsNullOrWhiteSpace(mergePolicy))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'mergePolicy' must not be empty."));
            return fallbackPolicy;
        }

        switch (mergePolicy!.Trim())
        {
            case "fail":
                return InterfaceMergePolicy.Fail;
            case "warn":
                return InterfaceMergePolicy.Warn;
            case "merge-prefer-first":
                return InterfaceMergePolicy.MergePreferFirst;
            case "merge-union":
                return InterfaceMergePolicy.MergeUnion;
            default:
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"Unsupported merge policy '{mergePolicy}'. Supported values: fail, warn, merge-prefer-first, merge-union."));
                return fallbackPolicy;
        }
    }

    private static bool ReadPreferQtTypeHints(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        bool fallbackValue)
    {
        if (!root.TryGetProperty("preferQtTypeHints", out var preferQtTypeHintsProperty))
        {
            return fallbackValue;
        }

        if (preferQtTypeHintsProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'preferQtTypeHints' must be a boolean."));
            return fallbackValue;
        }

        return preferQtTypeHintsProperty.GetBoolean();
    }

    private static QtTypeHintPolicy ReadQtTypeHintPolicy(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        QtTypeHintPolicy fallbackPolicy,
        bool strictConfiguration)
    {
        if (!root.TryGetProperty("qtTypeHintPolicy", out var policyProperty))
        {
            return fallbackPolicy;
        }

        if (policyProperty.ValueKind != JsonValueKind.Object)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'qtTypeHintPolicy' must be a JSON object."));
            return fallbackPolicy;
        }

        ValidateUnknownQtTypeHintPolicyProperties(
            context,
            configurationPath,
            policyProperty,
            strictConfiguration);

        var unknownBehavior = fallbackPolicy.UnknownBehavior;
        if (policyProperty.TryGetProperty("unknownHintBehavior", out var behaviorProperty))
        {
            if (behaviorProperty.ValueKind != JsonValueKind.String)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        "qtTypeHintPolicy.unknownHintBehavior must be a string."));
            }
            else
            {
                unknownBehavior = behaviorProperty.GetString()?.Trim() switch
                {
                    "allow" => QtTypeHintUnknownBehavior.Allow,
                    "warn" => QtTypeHintUnknownBehavior.Warn,
                    "error" => QtTypeHintUnknownBehavior.Error,
                    _ => ReportInvalidQtTypeHintUnknownBehavior(context, configurationPath, behaviorProperty.GetString(), fallbackPolicy.UnknownBehavior)
                };
            }
        }

        var allowedClrNamespaces = ReadStringArrayAsSet(
            context,
            configurationPath,
            policyProperty,
            "qtTypeHintPolicy",
            "allowedClrNamespaces");
        var allowedGenericTypeDefinitions = ReadStringArrayAsSet(
            context,
            configurationPath,
            policyProperty,
            "qtTypeHintPolicy",
            "allowedGenericTypeDefinitions");

        return new QtTypeHintPolicy(unknownBehavior, allowedClrNamespaces, allowedGenericTypeDefinitions);
    }

    private static ImmutableDictionary<string, string> ReadQtTypeHintMappings(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        ImmutableDictionary<string, string> fallbackMappings,
        QtTypeHintPolicy policy)
    {
        if (!root.TryGetProperty("qtTypeHintMappings", out var mappingsProperty))
        {
            return fallbackMappings;
        }

        if (mappingsProperty.ValueKind != JsonValueKind.Object)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'qtTypeHintMappings' must be a JSON object where keys are hint names and values are CLR type names."));
            return fallbackMappings;
        }

        var builder = fallbackMappings.ToBuilder();
        foreach (var mappingProperty in mappingsProperty.EnumerateObject())
        {
            var hintName = mappingProperty.Name.Trim();
            if (string.IsNullOrWhiteSpace(hintName))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        "'qtTypeHintMappings' contains an empty hint name."));
                continue;
            }

            if (mappingProperty.Value.ValueKind != JsonValueKind.String)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"qtTypeHintMappings['{mappingProperty.Name}'] must be a string CLR type name."));
                continue;
            }

            var clrType = mappingProperty.Value.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(clrType))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"qtTypeHintMappings['{mappingProperty.Name}'] must not be empty."));
                continue;
            }

            if (!IsLikelyClrTypeReference(clrType!))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"qtTypeHintMappings['{mappingProperty.Name}'] contains unsupported CLR type syntax '{clrType}'."));
                continue;
            }

            if (!ValidateMappedClrTypeAgainstPolicy(
                    context,
                    configurationPath,
                    mappingProperty.Name,
                    clrType!,
                    policy))
            {
                continue;
            }

            builder[hintName] = clrType!;
        }

        return builder.ToImmutable();
    }

    private static TypeNamingStrategy ReadNamingStrategy(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        TypeNamingStrategy fallbackStrategy,
        bool strictConfiguration)
    {
        if (!root.TryGetProperty("naming", out var namingProperty))
        {
            return fallbackStrategy;
        }

        if (namingProperty.ValueKind != JsonValueKind.Object)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'naming' must be a JSON object."));
            return fallbackStrategy;
        }

        ValidateUnknownNamingConfigurationProperties(
            context,
            configurationPath,
            namingProperty,
            strictConfiguration);

        var interfacePrefix = fallbackStrategy.InterfacePrefix;
        if (namingProperty.TryGetProperty("interfacePrefix", out var interfacePrefixProperty))
        {
            if (interfacePrefixProperty.ValueKind != JsonValueKind.String)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        "naming.interfacePrefix must be a string."));
            }
            else
            {
                interfacePrefix = interfacePrefixProperty.GetString()?.Trim() ?? string.Empty;
            }
        }

        var interfaceSuffix = fallbackStrategy.InterfaceSuffix;
        if (namingProperty.TryGetProperty("interfaceSuffix", out var interfaceSuffixProperty))
        {
            if (interfaceSuffixProperty.ValueKind != JsonValueKind.String)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        "naming.interfaceSuffix must be a string."));
            }
            else
            {
                interfaceSuffix = interfaceSuffixProperty.GetString()?.Trim() ?? string.Empty;
            }
        }

        var collisionPolicy = fallbackStrategy.CollisionPolicy;
        if (namingProperty.TryGetProperty("collisionPolicy", out var collisionPolicyProperty))
        {
            if (collisionPolicyProperty.ValueKind != JsonValueKind.String)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        "naming.collisionPolicy must be a string."));
            }
            else
            {
                collisionPolicy = collisionPolicyProperty.GetString() switch
                {
                    "suffix" => TypeNameCollisionPolicy.Suffix,
                    "prefix" => TypeNameCollisionPolicy.Prefix,
                    "hash" => TypeNameCollisionPolicy.Hash,
                    _ => ReportInvalidNamingCollisionPolicy(
                        context,
                        configurationPath,
                        collisionPolicyProperty.GetString(),
                        fallbackStrategy.CollisionPolicy)
                };
            }
        }

        var hashLength = fallbackStrategy.HashLength;
        if (namingProperty.TryGetProperty("hashLength", out var hashLengthProperty))
        {
            if (hashLengthProperty.ValueKind != JsonValueKind.Number || !hashLengthProperty.TryGetInt32(out var configuredHashLength))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        "naming.hashLength must be an integer between 4 and 32."));
            }
            else if (configuredHashLength < 4 || configuredHashLength > 32)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        "naming.hashLength must be between 4 and 32."));
            }
            else
            {
                hashLength = configuredHashLength;
            }
        }

        return new TypeNamingStrategy(interfacePrefix, interfaceSuffix, collisionPolicy, hashLength);
    }

    private static TypeNameCollisionPolicy ReportInvalidNamingCollisionPolicy(
        SourceProductionContext context,
        string configurationPath,
        string? configuredPolicy,
        TypeNameCollisionPolicy fallbackPolicy)
    {
        context.ReportDiagnostic(
            Diagnostic.Create(
                InvalidGeneratorConfiguration,
                Location.None,
                configurationPath,
                $"Unsupported naming.collisionPolicy value '{configuredPolicy}'. Supported values: suffix, prefix, hash."));
        return fallbackPolicy;
    }

    private static void ValidateUnknownNamingConfigurationProperties(
        SourceProductionContext context,
        string configurationPath,
        JsonElement namingConfiguration,
        bool strictConfiguration)
    {
        foreach (var property in namingConfiguration.EnumerateObject())
        {
            if (KnownNamingConfigurationProperties.Contains(property.Name))
            {
                continue;
            }

            ReportUnknownConfigurationProperty(
                context,
                configurationPath,
                property.Name,
                "$.naming." + property.Name,
                strictConfiguration);
        }
    }

    private static ImmutableHashSet<string> ReadIgnoredInterfaces(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        ImmutableHashSet<string> fallbackIgnoredInterfaces,
        out ImmutableHashSet<string> configuredIgnoredInterfaces)
    {
        if (!root.TryGetProperty("ignoredInterfaces", out var ignoredInterfacesProperty))
        {
            configuredIgnoredInterfaces = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            return fallbackIgnoredInterfaces;
        }

        if (ignoredInterfacesProperty.ValueKind != JsonValueKind.Array)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'ignoredInterfaces' must be an array of strings."));
            configuredIgnoredInterfaces = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            return fallbackIgnoredInterfaces;
        }

        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var item in ignoredInterfacesProperty.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        "'ignoredInterfaces' must contain only string values."));
                continue;
            }

            var interfaceName = item.GetString();
            if (!string.IsNullOrWhiteSpace(interfaceName))
            {
                builder.Add(interfaceName!.Trim());
            }
        }

        configuredIgnoredInterfaces = builder.ToImmutable();
        return configuredIgnoredInterfaces;
    }

    private static ImmutableHashSet<string>? ReadIncludedInterfaces(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        out ImmutableHashSet<string> configuredIncludedInterfaces)
    {
        if (!root.TryGetProperty("includedInterfaces", out var includedInterfacesProperty))
        {
            configuredIncludedInterfaces = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            return null;
        }

        if (includedInterfacesProperty.ValueKind != JsonValueKind.Array)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'includedInterfaces' must be an array of strings."));
            configuredIncludedInterfaces = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            return null;
        }

        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var item in includedInterfacesProperty.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        "'includedInterfaces' must contain only string values."));
                continue;
            }

            var interfaceName = item.GetString();
            if (!string.IsNullOrWhiteSpace(interfaceName))
            {
                builder.Add(interfaceName!.Trim());
            }
        }

        configuredIncludedInterfaces = builder.ToImmutable();
        return configuredIncludedInterfaces;
    }

    private static ImmutableDictionary<string, string> ReadInterfaceTypeNameOverrides(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        ImmutableDictionary<string, string> fallbackOverrides,
        out ImmutableHashSet<string> configuredOverrideInterfaces)
    {
        if (!root.TryGetProperty("interfaceTypeNameOverrides", out var overridesProperty))
        {
            configuredOverrideInterfaces = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            return fallbackOverrides;
        }

        if (overridesProperty.ValueKind != JsonValueKind.Object)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'interfaceTypeNameOverrides' must be a JSON object where keys are D-Bus interface names and values are C# type names."));
            configuredOverrideInterfaces = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            return fallbackOverrides;
        }

        var builder = fallbackOverrides.ToBuilder();
        var configuredInterfaceBuilder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var property in overridesProperty.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"Override for '{property.Name}' must be a string."));
                continue;
            }

            var typeName = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(typeName))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"Override for '{property.Name}' must not be empty."));
                continue;
            }

            var normalizedTypeName = typeName!.Trim();
            if (!IsValidIdentifier(normalizedTypeName))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"Override for '{property.Name}' must be a valid C# identifier."));
                continue;
            }

            configuredInterfaceBuilder.Add(property.Name);
            builder[property.Name] = normalizedTypeName;
        }

        configuredOverrideInterfaces = configuredInterfaceBuilder.ToImmutable();
        return builder.ToImmutable();
    }

    private static void ValidateUnknownQtTypeHintPolicyProperties(
        SourceProductionContext context,
        string configurationPath,
        JsonElement policyConfiguration,
        bool strictConfiguration)
    {
        foreach (var property in policyConfiguration.EnumerateObject())
        {
            if (KnownQtTypeHintPolicyProperties.Contains(property.Name))
            {
                continue;
            }

            ReportUnknownConfigurationProperty(
                context,
                configurationPath,
                property.Name,
                "$.qtTypeHintPolicy." + property.Name,
                strictConfiguration);
        }
    }

    private static QtTypeHintUnknownBehavior ReportInvalidQtTypeHintUnknownBehavior(
        SourceProductionContext context,
        string configurationPath,
        string? configuredBehavior,
        QtTypeHintUnknownBehavior fallbackBehavior)
    {
        context.ReportDiagnostic(
            Diagnostic.Create(
                InvalidGeneratorConfiguration,
                Location.None,
                configurationPath,
                $"Unsupported qtTypeHintPolicy.unknownHintBehavior value '{configuredBehavior}'. Supported values: allow, warn, error."));
        return fallbackBehavior;
    }

    private static ImmutableHashSet<string>? ReadStringArrayAsSet(
        SourceProductionContext context,
        string configurationPath,
        JsonElement parentObject,
        string parentName,
        string propertyName)
    {
        if (!parentObject.TryGetProperty(propertyName, out var propertyElement))
        {
            return null;
        }

        if (propertyElement.ValueKind != JsonValueKind.Array)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    $"{parentName}.{propertyName} must be an array of strings."));
            return ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        }

        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var item in propertyElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"{parentName}.{propertyName} must contain only string values."));
                continue;
            }

            var value = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (string.Equals(propertyName, "allowedClrNamespaces", StringComparison.Ordinal) && !IsValidNamespace(value!))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"{parentName}.{propertyName} contains invalid namespace '{value}'."));
                continue;
            }

            if (string.Equals(propertyName, "allowedGenericTypeDefinitions", StringComparison.Ordinal) &&
                !IsLikelyClrTypeDefinitionName(value!))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"{parentName}.{propertyName} contains invalid type definition '{value}'."));
                continue;
            }

            builder.Add(value!);
        }

        return builder.ToImmutable();
    }

    private static bool ValidateMappedClrTypeAgainstPolicy(
        SourceProductionContext context,
        string configurationPath,
        string hintName,
        string clrType,
        QtTypeHintPolicy policy)
    {
        if (policy.AllowedClrNamespaces is null && policy.AllowedGenericTypeDefinitions is null)
        {
            return true;
        }

        if (policy.AllowedClrNamespaces is not null)
        {
            foreach (var typeReference in ExtractClrTypeReferences(clrType))
            {
                if (KnownClrTypeKeywords.Contains(typeReference))
                {
                    continue;
                }

                if (typeReference.IndexOf('.') < 0)
                {
                    if (KnownSimpleClrTypeNames.Contains(typeReference))
                    {
                        continue;
                    }

                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            InvalidGeneratorConfiguration,
                            Location.None,
                            configurationPath,
                            $"qtTypeHintMappings['{hintName}'] uses unqualified type '{typeReference}' but qtTypeHintPolicy.allowedClrNamespaces is set. Use a fully-qualified type or a known intrinsic alias."));
                    return false;
                }

                var namespaceSeparator = typeReference.LastIndexOf('.');
                if (namespaceSeparator <= 0)
                {
                    continue;
                }

                var typeNamespace = typeReference.Substring(0, namespaceSeparator);
                var matchesAllowedNamespace = policy.AllowedClrNamespaces.Any(
                    allowedNamespace =>
                        string.Equals(typeNamespace, allowedNamespace, StringComparison.Ordinal) ||
                        typeNamespace.StartsWith(allowedNamespace + ".", StringComparison.Ordinal));
                if (!matchesAllowedNamespace)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            InvalidGeneratorConfiguration,
                            Location.None,
                            configurationPath,
                            $"qtTypeHintMappings['{hintName}'] uses type namespace '{typeNamespace}' that is not allowed by qtTypeHintPolicy.allowedClrNamespaces."));
                    return false;
                }
            }
        }

        if (policy.AllowedGenericTypeDefinitions is not null)
        {
            var allowedDefinitions = BuildAllowedTypeDefinitionLookup(policy.AllowedGenericTypeDefinitions);
            foreach (var genericRoot in ExtractGenericTypeRoots(clrType))
            {
                if (allowedDefinitions.Contains(genericRoot))
                {
                    continue;
                }

                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"qtTypeHintMappings['{hintName}'] uses generic type '{genericRoot}' that is not allowed by qtTypeHintPolicy.allowedGenericTypeDefinitions."));
                return false;
            }
        }

        return true;
    }

    private static ImmutableHashSet<string> BuildAllowedTypeDefinitionLookup(ImmutableHashSet<string> typeDefinitions)
    {
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var typeDefinition in typeDefinitions)
        {
            builder.Add(typeDefinition);
            var separatorIndex = typeDefinition.LastIndexOf('.');
            if (separatorIndex >= 0 && separatorIndex < typeDefinition.Length - 1)
            {
                builder.Add(typeDefinition.Substring(separatorIndex + 1));
            }
        }

        return builder.ToImmutable();
    }

    private static IEnumerable<string> ExtractClrTypeReferences(string clrType)
    {
        foreach (Match match in Regex.Matches(clrType, @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*"))
        {
            var value = match.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> ExtractGenericTypeRoots(string clrType)
    {
        foreach (Match match in Regex.Matches(clrType, @"(?<root>[A-Za-z_][A-Za-z0-9_.]*)\s*<"))
        {
            var root = match.Groups["root"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return root;
            }
        }
    }

    private static bool IsLikelyClrTypeDefinitionName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.IndexOf('<') >= 0 || value.IndexOf('>') >= 0)
        {
            return false;
        }

        var segments = value.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            if (!IsValidIdentifier(segment))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly ImmutableHashSet<string> KnownClrTypeKeywords =
    [
        "string",
        "bool",
        "byte",
        "sbyte",
        "short",
        "ushort",
        "int",
        "uint",
        "long",
        "ulong",
        "float",
        "double",
        "decimal",
        "object",
        "char",
        "nint",
        "nuint",
        "void"
    ];

    private static readonly ImmutableHashSet<string> KnownSimpleClrTypeNames =
    [
        "Guid",
        "DateTime",
        "DateTimeOffset",
        "TimeSpan",
        "DbusObjectPath",
        "CloseSafeHandle",
        "DbusPropertyChanges"
    ];

    private static ImmutableDictionary<string, InterfaceMemberFilter> ReadInterfaceMemberFilters(
        SourceProductionContext context,
        string configurationPath,
        JsonElement root,
        bool strictConfiguration,
        out ImmutableHashSet<string> configuredMemberFilterInterfaces)
    {
        if (!root.TryGetProperty("interfaceMembers", out var interfaceMembersProperty))
        {
            configuredMemberFilterInterfaces = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            return ImmutableDictionary<string, InterfaceMemberFilter>.Empty.WithComparers(StringComparer.Ordinal);
        }

        if (interfaceMembersProperty.ValueKind != JsonValueKind.Object)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    "'interfaceMembers' must be a JSON object."));
            configuredMemberFilterInterfaces = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            return ImmutableDictionary<string, InterfaceMemberFilter>.Empty.WithComparers(StringComparer.Ordinal);
        }

        var builder = ImmutableDictionary.CreateBuilder<string, InterfaceMemberFilter>(StringComparer.Ordinal);
        var configuredInterfaceBuilder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var interfaceProperty in interfaceMembersProperty.EnumerateObject())
        {
            if (interfaceProperty.Value.ValueKind != JsonValueKind.Object)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"interfaceMembers['{interfaceProperty.Name}'] must be an object."));
                continue;
            }

            configuredInterfaceBuilder.Add(interfaceProperty.Name);
            var value = interfaceProperty.Value;
            ValidateUnknownInterfaceMemberFilterProperties(
                context,
                configurationPath,
                interfaceProperty.Name,
                value,
                strictConfiguration);
            var methods = ReadMemberNameSet(context, configurationPath, interfaceProperty.Name, "methods", value);
            var properties = ReadMemberNameSet(context, configurationPath, interfaceProperty.Name, "properties", value);
            var signals = ReadMemberNameSet(context, configurationPath, interfaceProperty.Name, "signals", value);

            builder[interfaceProperty.Name] = new InterfaceMemberFilter(methods, properties, signals);
        }

        configuredMemberFilterInterfaces = configuredInterfaceBuilder.ToImmutable();
        return builder.ToImmutable();
    }

    private static void ValidateUnknownInterfaceMemberFilterProperties(
        SourceProductionContext context,
        string configurationPath,
        string interfaceName,
        JsonElement interfaceConfiguration,
        bool strictConfiguration)
    {
        foreach (var property in interfaceConfiguration.EnumerateObject())
        {
            if (KnownInterfaceMemberFilterProperties.Contains(property.Name))
            {
                continue;
            }

            ReportUnknownConfigurationProperty(
                context,
                configurationPath,
                property.Name,
                $"$.interfaceMembers['{interfaceName}'].{property.Name}",
                strictConfiguration);
        }
    }

    private static void ReportUnknownConfigurationProperty(
        SourceProductionContext context,
        string configurationPath,
        string propertyName,
        string propertyPath,
        bool strictConfiguration)
    {
        context.ReportDiagnostic(
            Diagnostic.Create(
                UnknownConfigurationProperty,
                Location.None,
                configurationPath,
                propertyName,
                propertyPath));

        if (!strictConfiguration)
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                InvalidGeneratorConfiguration,
                Location.None,
                configurationPath,
                $"Unknown configuration property '{propertyName}' at '{propertyPath}'."));
    }

    private static ImmutableHashSet<string>? ReadMemberNameSet(
        SourceProductionContext context,
        string configurationPath,
        string interfaceName,
        string propertyName,
        JsonElement interfaceConfiguration)
    {
        if (!interfaceConfiguration.TryGetProperty(propertyName, out var memberProperty))
        {
            return null;
        }

        if (memberProperty.ValueKind != JsonValueKind.Array)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidGeneratorConfiguration,
                    Location.None,
                    configurationPath,
                    $"interfaceMembers['{interfaceName}'].{propertyName} must be an array of strings."));
            return ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        }

        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var memberElement in memberProperty.EnumerateArray())
        {
            if (memberElement.ValueKind != JsonValueKind.String)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidGeneratorConfiguration,
                        Location.None,
                        configurationPath,
                        $"interfaceMembers['{interfaceName}'].{propertyName} must contain only string values."));
                continue;
            }

            var memberName = memberElement.GetString();
            if (string.IsNullOrWhiteSpace(memberName))
            {
                continue;
            }

            builder.Add(memberName!.Trim());
        }

        return builder.ToImmutable();
    }

    private static bool IsLikelyClrTypeReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var depth = 0;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) ||
                char.IsLetterOrDigit(character) ||
                character is '_' or '.' or ',' or '[' or ']' or '?' or '*' or ':' or '(' or ')')
            {
                continue;
            }

            if (character == '<')
            {
                depth++;
                continue;
            }

            if (character == '>')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return depth == 0;
    }
}
