
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Dbus.ContractGenerator;

[Generator]
public sealed partial class DbusContractSourceGenerator : IIncrementalGenerator
{
    private const string DefaultGeneratedNamespace = "Dbus.Contracts.Generated";
    private const string DefaultConfigurationFileName = "dbus-contract-generator.json";
    private const int SupportedConfigurationSchemaVersion = 1;

    private static readonly ImmutableHashSet<string> KnownConfigurationProperties =
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

    private static readonly ImmutableHashSet<string> KnownInterfaceMemberFilterProperties =
    [
        "methods",
        "properties",
        "signals"
    ];

    private static readonly ImmutableHashSet<string> KnownNamingConfigurationProperties =
    [
        "interfacePrefix",
        "interfaceSuffix",
        "collisionPolicy",
        "hashLength"
    ];

    private static readonly ImmutableHashSet<string> KnownQtTypeHintPolicyProperties =
    [
        "unknownHintBehavior",
        "allowedClrNamespaces",
        "allowedGenericTypeDefinitions"
    ];

    private static readonly ImmutableDictionary<string, string> DefaultInterfaceTypeNameOverrides =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    private static readonly ImmutableHashSet<string> DefaultIgnoredInterfaces =
    [
        "org.freedesktop.DBus.Peer",
        "org.freedesktop.DBus.Introspectable",
        "org.freedesktop.DBus.Properties"
    ];

    private static readonly ImmutableDictionary<string, string> DefaultQtTypeHintMappings =
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

    private static readonly DiagnosticDescriptor InvalidXmlDocument = new(
        id: "DBCG001",
        title: "Invalid D-Bus XML introspection document",
        messageFormat: "Failed to parse D-Bus XML file '{0}': {1}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidSignature = new(
        id: "DBCG002",
        title: "Invalid or unsupported D-Bus signature",
        messageFormat: "Failed to parse D-Bus signature '{0}' in file '{1}': {2}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConflictingInterfaceDefinition = new(
        id: "DBCG003",
        title: "Conflicting D-Bus interface definitions",
        messageFormat: "Interface '{0}' is defined with conflicting signatures in files '{1}' and '{2}'",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MissingInterfaceMetadata = new(
        id: "DBCG004",
        title: "Missing D-Bus interface metadata",
        messageFormat: "D-Bus XML file '{0}' contains an interface with missing required metadata ({1})",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidGeneratorConfiguration = new(
        id: "DBCG005",
        title: "Invalid D-Bus generator configuration",
        messageFormat: "Invalid D-Bus generator configuration for '{0}': {1}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidMemberFilter = new(
        id: "DBCG006",
        title: "Invalid D-Bus interface member filter",
        messageFormat: "Invalid D-Bus interface member filter for '{0}': {1}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnknownConfiguredInterface = new(
        id: "DBCG007",
        title: "Unknown D-Bus interface in generator configuration",
        messageFormat: "D-Bus generator configuration '{0}' references unknown interface '{1}' in section '{2}'",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnknownConfigurationProperty = new(
        id: "DBCG008",
        title: "Unknown D-Bus generator configuration property",
        messageFormat: "D-Bus generator configuration '{0}' contains unknown property '{1}' at '{2}'",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor MergeConflictWarning = new(
        id: "DBCG009",
        title: "Conflicting D-Bus interface definitions were merged",
        messageFormat: "Interface '{0}' has conflicting definitions in files '{1}' and '{2}'. Merge policy '{3}' produced a deterministic merged contract.",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidContractSemantics = new(
        id: "DBCG010",
        title: "Invalid D-Bus contract semantics",
        messageFormat: "Interface '{0}' has invalid contract semantics: {1}; suggestion: {2}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidAnnotationUsage = new(
        id: "DBCG011",
        title: "Invalid D-Bus annotation usage",
        messageFormat: "Interface '{0}' has invalid annotation usage: {1}; suggestion: {2}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor QtTypeHintPolicyWarning = new(
        id: "DBCG012",
        title: "Qt type hint mapping warning",
        messageFormat: "Interface '{0}' has Qt type hint issue: {1}; suggestion: {2}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor QtTypeHintPolicyError = new(
        id: "DBCG013",
        title: "Qt type hint mapping error",
        messageFormat: "Interface '{0}' has Qt type hint issue: {1}; suggestion: {2}",
        category: "DBusGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var xmlFiles = context.AdditionalTextsProvider
            .Where(static file => IsDbusXml(file.Path))
            .Select(static (file, cancellationToken) => new DbusXmlFile(file.Path, file.GetText(cancellationToken)?.ToString() ?? string.Empty))
            .Where(static file => !string.IsNullOrWhiteSpace(file.Content))
            .Collect();

        var configurationFiles = context.AdditionalTextsProvider
            .Where(static file => IsGeneratorConfigurationFile(file.Path))
            .Select(static (file, cancellationToken) => new DbusGeneratorConfigurationFile(file.Path, file.GetText(cancellationToken)?.ToString() ?? string.Empty))
            .Collect();

        context.RegisterSourceOutput(
            xmlFiles.Combine(configurationFiles),
            static (sourceProductionContext, input) =>
            {
                Emit(sourceProductionContext, input.Left, input.Right);
            });
    }

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<DbusXmlFile> files,
        ImmutableArray<DbusGeneratorConfigurationFile> configurationFiles)
    {
        if (files.IsDefaultOrEmpty)
        {
            return;
        }

        var configuration = BuildGeneratorConfiguration(context, configurationFiles);
        var interfacesByName = new Dictionary<string, DbusInterfaceModel>(StringComparer.Ordinal);
        var discoveredInterfaceNames = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var file in files.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            var parsed = ParseXmlFile(context, file, configuration);
            if (parsed.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (var interfaceModel in parsed)
            {
                discoveredInterfaceNames.Add(interfaceModel.InterfaceName);

                if (interfacesByName.TryGetValue(interfaceModel.InterfaceName, out var existing))
                {
                    if (!string.Equals(existing.Fingerprint, interfaceModel.Fingerprint, StringComparison.Ordinal))
                    {
                        if (configuration.StrictAbiCompatibility)
                        {
                            context.ReportDiagnostic(
                                Diagnostic.Create(
                                    ConflictingInterfaceDefinition,
                                    Location.None,
                                    interfaceModel.InterfaceName,
                                    existing.SourcePath,
                                    interfaceModel.SourcePath));
                            continue;
                        }

                        var mergeResult = MergeInterfaceDefinitions(existing, interfaceModel, configuration.MergePolicy);
                        interfacesByName[interfaceModel.InterfaceName] = mergeResult.MergedInterface;
                        var shouldReportStrictUnionError = configuration.MergePolicy == InterfaceMergePolicy.MergeUnion &&
                                                           configuration.StrictConfiguration &&
                                                           mergeResult.HasIncompatibleMembers;

                        if (mergeResult.ReportAsError || shouldReportStrictUnionError)
                        {
                            context.ReportDiagnostic(
                                Diagnostic.Create(
                                    ConflictingInterfaceDefinition,
                                    Location.None,
                                    interfaceModel.InterfaceName,
                                    existing.SourcePath,
                                    interfaceModel.SourcePath));
                        }
                        else if (mergeResult.ReportAsWarning)
                        {
                            context.ReportDiagnostic(
                                Diagnostic.Create(
                                    MergeConflictWarning,
                                    Location.None,
                                    interfaceModel.InterfaceName,
                                    existing.SourcePath,
                                    interfaceModel.SourcePath,
                                    mergeResult.PolicyName));
                        }
                    }
                    else
                    {
                        var mergeResult = MergeInterfaceDefinitions(existing, interfaceModel, InterfaceMergePolicy.MergePreferFirst);
                        interfacesByName[interfaceModel.InterfaceName] = mergeResult.MergedInterface;
                    }

                    continue;
                }

                if (!configuration.ShouldGenerateInterface(interfaceModel.InterfaceName))
                {
                    continue;
                }

                interfacesByName[interfaceModel.InterfaceName] = interfaceModel;
            }
        }

        ValidateConfiguredInterfaces(context, configuration, discoveredInterfaceNames.ToImmutable());
        var resolvedInterfaces = ResolveUniqueTypeNames(interfacesByName.Values, configuration.TypeNamingStrategy);

        foreach (var interfaceModel in resolvedInterfaces)
        {
            var source = BuildSource(interfaceModel, configuration);
            context.AddSource(
                GetHintName(interfaceModel.InterfaceName),
                SourceText.From(source, Encoding.UTF8));
        }
    }
}
