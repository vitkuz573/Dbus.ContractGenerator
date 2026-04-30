using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using static Dbus.ContractGenerator.DbusGeneratorConstants;
using static Dbus.ContractGenerator.DbusGeneratorNaming;

namespace Dbus.ContractGenerator;

[Generator]
public sealed class DbusContractSourceGenerator : IIncrementalGenerator
{
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

        var configuration = DbusGeneratorConfigurationReader.BuildGeneratorConfiguration(context, configurationFiles);
        var interfacesByName = new Dictionary<string, DbusInterfaceModel>(StringComparer.Ordinal);
        var discoveredInterfaceNames = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var file in files.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            var parsed = DbusXmlContractParser.ParseXmlFile(context, file, configuration);
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

                        var mergeResult = DbusInterfaceMergeEngine.MergeInterfaceDefinitions(existing, interfaceModel, configuration.MergePolicy);
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
                        var mergeResult = DbusInterfaceMergeEngine.MergeInterfaceDefinitions(existing, interfaceModel, InterfaceMergePolicy.MergePreferFirst);
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

        DbusGeneratorValidation.ValidateConfiguredInterfaces(context, configuration, discoveredInterfaceNames.ToImmutable());
        var resolvedInterfaces = DbusInterfaceTypeNameResolver.ResolveUniqueTypeNames(interfacesByName.Values, configuration.TypeNamingStrategy);

        foreach (var interfaceModel in resolvedInterfaces)
        {
            var source = DbusSourceEmitter.BuildSource(interfaceModel, configuration);
            context.AddSource(
                GetHintName(interfaceModel.InterfaceName),
                SourceText.From(source, Encoding.UTF8));
        }
    }
}
