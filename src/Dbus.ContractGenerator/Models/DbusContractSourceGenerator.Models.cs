
using System.Collections.Immutable;

namespace Dbus.ContractGenerator;

public sealed partial class DbusContractSourceGenerator
{
    private readonly struct DbusGeneratorConfigurationFile(string path, string content)
    {
        public string Path { get; } = path;

        public string Content { get; } = content;
    }

    private sealed class GeneratorConfiguration(
        string configurationPath,
        bool strictConfiguration,
        int schemaVersion,
        string generatedNamespace,
        InterfaceMergePolicy mergePolicy,
        bool strictAbiCompatibility,
        TypeNamingStrategy typeNamingStrategy,
        bool preferQtTypeHints,
        QtTypeHintPolicy qtTypeHintPolicy,
        ImmutableDictionary<string, string> qtTypeHintMappings,
        ImmutableHashSet<string> ignoredInterfaces,
        ImmutableHashSet<string>? includedInterfaces,
        ImmutableDictionary<string, string> interfaceTypeNameOverrides,
        ImmutableDictionary<string, InterfaceMemberFilter> interfaceMemberFilters,
        ImmutableHashSet<string> configuredIgnoredInterfaces,
        ImmutableHashSet<string> configuredIncludedInterfaces,
        ImmutableHashSet<string> configuredOverrideInterfaces,
        ImmutableHashSet<string> configuredMemberFilterInterfaces)
    {
        public string ConfigurationPath { get; } = configurationPath;

        public bool StrictConfiguration { get; } = strictConfiguration;

        public int SchemaVersion { get; } = schemaVersion;

        public string GeneratedNamespace { get; } = generatedNamespace;

        public InterfaceMergePolicy MergePolicy { get; } = mergePolicy;

        public bool StrictAbiCompatibility { get; } = strictAbiCompatibility;

        public TypeNamingStrategy TypeNamingStrategy { get; } = typeNamingStrategy;

        public bool PreferQtTypeHints { get; } = preferQtTypeHints;

        public QtTypeHintPolicy QtTypeHintPolicy { get; } = qtTypeHintPolicy;

        public ImmutableDictionary<string, string> QtTypeHintMappings { get; } = qtTypeHintMappings;

        public ImmutableHashSet<string> IgnoredInterfaces { get; } = ignoredInterfaces;

        public ImmutableHashSet<string>? IncludedInterfaces { get; } = includedInterfaces;

        public ImmutableDictionary<string, string> InterfaceTypeNameOverrides { get; } = interfaceTypeNameOverrides;

        public ImmutableDictionary<string, InterfaceMemberFilter> InterfaceMemberFilters { get; } = interfaceMemberFilters;

        public ImmutableHashSet<string> ConfiguredIgnoredInterfaces { get; } = configuredIgnoredInterfaces;

        public ImmutableHashSet<string> ConfiguredIncludedInterfaces { get; } = configuredIncludedInterfaces;

        public ImmutableHashSet<string> ConfiguredOverrideInterfaces { get; } = configuredOverrideInterfaces;

        public ImmutableHashSet<string> ConfiguredMemberFilterInterfaces { get; } = configuredMemberFilterInterfaces;

        public bool ShouldGenerateInterface(string interfaceName)
        {
            if (IncludedInterfaces is not null)
            {
                return IncludedInterfaces.Contains(interfaceName);
            }

            return !IgnoredInterfaces.Contains(interfaceName);
        }
    }

    private enum InterfaceMergePolicy
    {
        Fail = 0,
        Warn = 1,
        MergePreferFirst = 2,
        MergeUnion = 3
    }

    private enum TypeNameCollisionPolicy
    {
        Suffix = 0,
        Prefix = 1,
        Hash = 2
    }

    private enum QtTypeHintUnknownBehavior
    {
        Allow = 0,
        Warn = 1,
        Error = 2
    }

    private sealed class QtTypeHintPolicy(
        QtTypeHintUnknownBehavior unknownBehavior,
        ImmutableHashSet<string>? allowedClrNamespaces,
        ImmutableHashSet<string>? allowedGenericTypeDefinitions)
    {
        public QtTypeHintUnknownBehavior UnknownBehavior { get; } = unknownBehavior;

        public ImmutableHashSet<string>? AllowedClrNamespaces { get; } = allowedClrNamespaces;

        public ImmutableHashSet<string>? AllowedGenericTypeDefinitions { get; } = allowedGenericTypeDefinitions;
    }

    private sealed class TypeNamingStrategy(
        string interfacePrefix,
        string interfaceSuffix,
        TypeNameCollisionPolicy collisionPolicy,
        int hashLength)
    {
        public string InterfacePrefix { get; } = interfacePrefix;

        public string InterfaceSuffix { get; } = interfaceSuffix;

        public TypeNameCollisionPolicy CollisionPolicy { get; } = collisionPolicy;

        public int HashLength { get; } = hashLength;
    }

    private sealed class InterfaceMemberFilter(
        ImmutableHashSet<string>? methods,
        ImmutableHashSet<string>? properties,
        ImmutableHashSet<string>? signals)
    {
        public ImmutableHashSet<string>? Methods { get; } = methods;

        public ImmutableHashSet<string>? Properties { get; } = properties;

        public ImmutableHashSet<string>? Signals { get; } = signals;
    }

    private readonly struct DbusXmlFile(string path, string content)
    {
        public string Path { get; } = path;

        public string Content { get; } = content;
    }

    private sealed class DbusInterfaceModel(
        string sourcePath,
        string interfaceName,
        string interfaceTypeName,
        string propertyTypeName,
        string extensionTypeName,
        ImmutableArray<string> objectPaths,
        DbusAnnotationSet annotations,
        ImmutableArray<DbusMethodModel> methods,
        ImmutableArray<DbusPropertyModel> properties,
        ImmutableArray<DbusSignalModel> signals,
        string fingerprint)
    {
        public string SourcePath { get; } = sourcePath;

        public string InterfaceName { get; } = interfaceName;

        public string InterfaceTypeName { get; } = interfaceTypeName;

        public string PropertyTypeName { get; } = propertyTypeName;

        public string ExtensionTypeName { get; } = extensionTypeName;

        public ImmutableArray<string> ObjectPaths { get; } = objectPaths;

        public DbusAnnotationSet Annotations { get; } = annotations;

        public ImmutableArray<DbusMethodModel> Methods { get; } = methods;

        public ImmutableArray<DbusPropertyModel> Properties { get; } = properties;

        public ImmutableArray<DbusSignalModel> Signals { get; } = signals;

        public string Fingerprint { get; } = fingerprint;

        public DbusInterfaceModel WithInterfaceTypeName(string resolvedInterfaceTypeName)
        {
            var resolvedPropertyTypeName = resolvedInterfaceTypeName.StartsWith("I", StringComparison.Ordinal) && resolvedInterfaceTypeName.Length > 1
                ? resolvedInterfaceTypeName.Substring(1) + "Properties"
                : resolvedInterfaceTypeName + "Properties";
            var resolvedExtensionTypeName = resolvedInterfaceTypeName.StartsWith("I", StringComparison.Ordinal) && resolvedInterfaceTypeName.Length > 1
                ? resolvedInterfaceTypeName.Substring(1) + "Extensions"
                : resolvedInterfaceTypeName + "Extensions";

            return new DbusInterfaceModel(
                SourcePath,
                InterfaceName,
                resolvedInterfaceTypeName,
                resolvedPropertyTypeName,
                resolvedExtensionTypeName,
                ObjectPaths,
                Annotations,
                Methods,
                Properties,
                Signals,
                Fingerprint);
        }
    }

    private sealed class DbusMethodModel(
        string name,
        ImmutableArray<DbusArgumentModel> inArguments,
        ImmutableArray<DbusArgumentModel> outArguments,
        DbusAnnotationSet annotations,
        bool noReply,
        ImmutableDictionary<int, string> inQtTypeHints,
        ImmutableDictionary<int, string> outQtTypeHints)
    {
        public string Name { get; } = name;

        public ImmutableArray<DbusArgumentModel> InArguments { get; } = inArguments;

        public ImmutableArray<DbusArgumentModel> OutArguments { get; } = outArguments;

        public DbusAnnotationSet Annotations { get; } = annotations;

        public bool NoReply { get; } = noReply;

        public ImmutableDictionary<int, string> InQtTypeHints { get; } = inQtTypeHints;

        public ImmutableDictionary<int, string> OutQtTypeHints { get; } = outQtTypeHints;
    }

    private sealed class DbusPropertyModel(
        string name,
        string signature,
        DbusType type,
        string access,
        DbusAnnotationSet annotations,
        string? qtTypeHint)
    {
        public string Name { get; } = name;

        public string Signature { get; } = signature;

        public DbusType Type { get; } = type;

        public string Access { get; } = access;

        public DbusAnnotationSet Annotations { get; } = annotations;

        public string? QtTypeHint { get; } = qtTypeHint;
    }

    private sealed class DbusSignalModel(
        string name,
        ImmutableArray<DbusArgumentModel> arguments,
        DbusAnnotationSet annotations,
        ImmutableDictionary<int, string> qtTypeHints)
    {
        public string Name { get; } = name;

        public ImmutableArray<DbusArgumentModel> Arguments { get; } = arguments;

        public DbusAnnotationSet Annotations { get; } = annotations;

        public ImmutableDictionary<int, string> QtTypeHints { get; } = qtTypeHints;
    }

    private sealed class DbusArgumentModel(string name, string signature, DbusType type, DbusAnnotationSet annotations)
    {
        public string Name { get; } = name;

        public string Signature { get; } = signature;

        public DbusType Type { get; } = type;

        public DbusAnnotationSet Annotations { get; } = annotations;
    }

    private sealed class DbusAnnotationSet(ImmutableDictionary<string, string> values)
    {
        public static DbusAnnotationSet Empty { get; } =
            new(ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));

        public ImmutableDictionary<string, string> Values { get; } = values;

        public bool IsEmpty => Values.Count == 0;

        public bool TryGetValue(string name, out string value)
        {
            return Values.TryGetValue(name, out value!);
        }

        public bool HasTrueFlag(params string[] annotationNames)
        {
            foreach (var annotationName in annotationNames)
            {
                if (!TryGetValue(annotationName, out var annotationValue))
                {
                    continue;
                }

                if (IsTrueAnnotationValue(annotationValue))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsTrueAnnotationValue(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.Ordinal);
        }
    }

    private abstract class DbusType;

    private sealed class PrimitiveDbusType(char code) : DbusType
    {
        public char Code { get; } = code;
    }

    private sealed class ArrayDbusType(DbusType elementType) : DbusType
    {
        public DbusType ElementType { get; } = elementType;
    }

    private sealed class DictDbusType(DbusType keyType, DbusType valueType) : DbusType
    {
        public DbusType KeyType { get; } = keyType;

        public DbusType ValueType { get; } = valueType;
    }

    private sealed class StructDbusType(ImmutableArray<DbusType> elements) : DbusType
    {
        public ImmutableArray<DbusType> Elements { get; } = elements;
    }

    private sealed class DbusSignatureParseException(string signature, string message) : Exception(message)
    {
        public string Signature { get; } = signature;
    }
}
