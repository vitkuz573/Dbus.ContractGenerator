using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Dbus.ContractGenerator;

internal static class DbusGeneratorConstants
{
    internal const string DefaultGeneratedNamespace = "Dbus.Contracts.Generated";
    internal const string DefaultConfigurationFileName = "dbus-contract-generator.json";
    internal const int SupportedConfigurationSchemaVersion = 1;

    internal const string DbusDeprecatedAnnotation = "org.freedesktop.DBus.Deprecated";
    internal const string DbusExperimentalAnnotation = "org.freedesktop.DBus.Experimental";
    internal const string DbusNoReplyAnnotation = "org.freedesktop.DBus.Method.NoReply";
    internal const string QtTypeNameAnnotation = "org.qtproject.QtDBus.QtTypeName";
    internal const string QtTypeNameInPrefix = "org.qtproject.QtDBus.QtTypeName.In";
    internal const string QtTypeNameOutPrefix = "org.qtproject.QtDBus.QtTypeName.Out";

    internal static readonly ImmutableHashSet<string> KnownConfigurationProperties =
    [
        "schemaVersion",
        "strictConfiguration",
        "strictAbiCompatibility",
        "generatedNamespace",
        "mergePolicy",
        "naming",
        "preferQtTypeHints",
        "qtTypeHintPolicy",
        "qtTypeHintMappings",
        "ignoredInterfaces",
        "includedInterfaces",
        "interfaceTypeNameOverrides",
        "interfaceMembers"
    ];

    internal static readonly ImmutableHashSet<string> KnownInterfaceMemberFilterProperties =
    [
        "methods",
        "properties",
        "signals"
    ];

    internal static readonly ImmutableHashSet<string> KnownNamingConfigurationProperties =
    [
        "interfacePrefix",
        "interfaceSuffix",
        "collisionPolicy",
        "hashLength"
    ];

    internal static readonly ImmutableHashSet<string> KnownQtTypeHintPolicyProperties =
    [
        "unknownHintBehavior",
        "allowedClrNamespaces",
        "allowedGenericTypeDefinitions"
    ];

    internal static readonly ImmutableDictionary<string, string> DefaultInterfaceTypeNameOverrides =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    internal static readonly ImmutableHashSet<string> DefaultIgnoredInterfaces =
    [
        "org.freedesktop.DBus.Peer",
        "org.freedesktop.DBus.Introspectable",
        "org.freedesktop.DBus.Properties"
    ];

    internal static readonly ImmutableDictionary<string, string> DefaultQtTypeHintMappings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["QString"] = "string",
            ["QStringList"] = "string[]",
            ["QByteArray"] = "byte[]",
            ["QVariantMap"] = "System.Collections.Generic.IDictionary<string, object>",
            ["QDBusObjectPath"] = "DbusObjectPath",
            ["bool"] = "bool",
            ["double"] = "double",
            ["qint16"] = "short",
            ["quint16"] = "ushort",
            ["qint32"] = "int",
            ["quint32"] = "uint",
            ["qint64"] = "long",
            ["quint64"] = "ulong"
        }.ToImmutableDictionary(StringComparer.Ordinal);

    internal static readonly DiagnosticDescriptor InvalidXmlDocument = new(
        id: "DBCG001",
        title: "Invalid D-Bus XML introspection document",
        messageFormat: "Failed to parse D-Bus XML file '{0}': {1}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidSignature = new(
        id: "DBCG002",
        title: "Invalid or unsupported D-Bus signature",
        messageFormat: "Failed to parse D-Bus signature '{0}' in file '{1}': {2}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ConflictingInterfaceDefinition = new(
        id: "DBCG003",
        title: "Conflicting D-Bus interface definitions",
        messageFormat: "Interface '{0}' is defined with conflicting signatures in files '{1}' and '{2}'",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor MissingInterfaceMetadata = new(
        id: "DBCG004",
        title: "Missing D-Bus interface metadata",
        messageFormat: "D-Bus XML file '{0}' contains an interface with missing required metadata ({1})",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidGeneratorConfiguration = new(
        id: "DBCG005",
        title: "Invalid D-Bus generator configuration",
        messageFormat: "Invalid D-Bus generator configuration for '{0}': {1}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidMemberFilter = new(
        id: "DBCG006",
        title: "Invalid D-Bus interface member filter",
        messageFormat: "Invalid D-Bus interface member filter for '{0}': {1}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnknownConfiguredInterface = new(
        id: "DBCG007",
        title: "Unknown D-Bus interface in generator configuration",
        messageFormat: "D-Bus generator configuration '{0}' references unknown interface '{1}' in section '{2}'",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnknownConfigurationProperty = new(
        id: "DBCG008",
        title: "Unknown D-Bus generator configuration property",
        messageFormat: "D-Bus generator configuration '{0}' contains unknown property '{1}' at '{2}'",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor MergeConflictWarning = new(
        id: "DBCG009",
        title: "Conflicting D-Bus interface definitions were merged",
        messageFormat: "Interface '{0}' has conflicting definitions in files '{1}' and '{2}'. Merge policy '{3}' produced a deterministic merged contract.",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidContractSemantics = new(
        id: "DBCG010",
        title: "Invalid D-Bus contract semantics",
        messageFormat: "Interface '{0}' has invalid contract semantics: {1}; suggestion: {2}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor InvalidAnnotationUsage = new(
        id: "DBCG011",
        title: "Invalid D-Bus annotation usage",
        messageFormat: "Interface '{0}' has invalid annotation usage: {1}; suggestion: {2}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor QtTypeHintPolicyWarning = new(
        id: "DBCG012",
        title: "Qt type hint mapping warning",
        messageFormat: "Interface '{0}' has Qt type hint issue: {1}; suggestion: {2}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor QtTypeHintPolicyError = new(
        id: "DBCG013",
        title: "Qt type hint mapping error",
        messageFormat: "Interface '{0}' has Qt type hint issue: {1}; suggestion: {2}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
