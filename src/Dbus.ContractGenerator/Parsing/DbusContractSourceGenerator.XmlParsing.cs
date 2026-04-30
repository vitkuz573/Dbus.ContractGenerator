
using System.Collections.Immutable;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Dbus.ContractGenerator;

public sealed partial class DbusContractSourceGenerator
{
    private const string DbusDeprecatedAnnotation = "org.freedesktop.DBus.Deprecated";
    private const string DbusExperimentalAnnotation = "org.freedesktop.DBus.Experimental";
    private const string DbusNoReplyAnnotation = "org.freedesktop.DBus.Method.NoReply";
    private const string QtTypeNameAnnotation = "org.qtproject.QtDBus.QtTypeName";
    private const string QtTypeNameInPrefix = "org.qtproject.QtDBus.QtTypeName.In";
    private const string QtTypeNameOutPrefix = "org.qtproject.QtDBus.QtTypeName.Out";

    private static readonly ImmutableHashSet<string> KnownBooleanAnnotations =
    [
        DbusDeprecatedAnnotation,
        DbusExperimentalAnnotation,
        DbusNoReplyAnnotation,
        "org.freedesktop.DBus.Method.Deprecated",
        "org.freedesktop.DBus.Property.Deprecated",
        "org.freedesktop.DBus.Signal.Deprecated",
        "org.freedesktop.DBus.Method.Experimental",
        "org.freedesktop.DBus.Property.Experimental",
        "org.freedesktop.DBus.Signal.Experimental"
    ];

    private static ImmutableArray<DbusInterfaceModel> ParseXmlFile(
        SourceProductionContext context,
        DbusXmlFile file,
        GeneratorConfiguration configuration)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(file.Content, LoadOptions.SetLineInfo);
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(InvalidXmlDocument, Location.None, file.Path, ex.Message));
            return [];
        }

        var rootNode = document.Root;
        if (rootNode is null || !string.Equals(rootNode.Name.LocalName, "node", StringComparison.Ordinal))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(InvalidXmlDocument, Location.None, file.Path, "The XML root element must be <node>."));
            return [];
        }

        var sourceText = SourceText.From(file.Content, Encoding.UTF8);
        var interfaces = new List<DbusInterfaceModel>();
        var rootPath = NormalizeObjectPath(rootNode.Attribute("name")?.Value);
        ParseNodeInterfaces(context, file.Path, sourceText, rootNode, rootPath, configuration, interfaces);

        return interfaces.ToImmutableArray();
    }

    private static void ParseNodeInterfaces(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        XElement nodeElement,
        string objectPath,
        GeneratorConfiguration configuration,
        IList<DbusInterfaceModel> output)
    {
        foreach (var interfaceElement in nodeElement.Elements().Where(static element => element.Name.LocalName == "interface"))
        {
            var interfaceModel = ParseInterfaceElement(context, sourcePath, sourceText, objectPath, interfaceElement, configuration);
            if (interfaceModel is not null)
            {
                output.Add(interfaceModel);
            }
        }

        foreach (var childNodeElement in nodeElement.Elements().Where(static element => element.Name.LocalName == "node"))
        {
            var childNodeName = childNodeElement.Attribute("name")?.Value;
            var childObjectPath = CombineObjectPath(objectPath, childNodeName);
            ParseNodeInterfaces(context, sourcePath, sourceText, childNodeElement, childObjectPath, configuration, output);
        }
    }

    private static DbusInterfaceModel? ParseInterfaceElement(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        string objectPath,
        XElement interfaceElement,
        GeneratorConfiguration configuration)
    {
        var interfaceNameValue = interfaceElement.Attribute("name")?.Value;
        if (string.IsNullOrWhiteSpace(interfaceNameValue))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(MissingInterfaceMetadata, CreateXmlLocation(sourcePath, sourceText, interfaceElement), sourcePath, "interface name"));
            return null;
        }

        var interfaceName = interfaceNameValue!.Trim();
        var interfaceTypeName = ResolveInterfaceTypeName(interfaceName, configuration.InterfaceTypeNameOverrides);
        var propertyTypeName = interfaceTypeName.StartsWith("I", StringComparison.Ordinal)
            ? interfaceTypeName.Substring(1) + "Properties"
            : interfaceTypeName + "Properties";
        var extensionTypeName = interfaceTypeName.StartsWith("I", StringComparison.Ordinal)
            ? interfaceTypeName.Substring(1) + "Extensions"
            : interfaceTypeName + "Extensions";
        var interfaceAnnotations = ParseAnnotations(
            context,
            sourcePath,
            sourceText,
            interfaceName,
            "interface",
            interfaceName,
            interfaceElement);

        if (interfaceAnnotations.TryGetValue(DbusNoReplyAnnotation, out _))
        {
            ReportAnnotationDiagnostic(
                context,
                sourcePath,
                sourceText,
                interfaceName,
                interfaceElement,
                $"interface annotation '{DbusNoReplyAnnotation}' is not valid on interfaces",
                "Move this annotation to a <method> element or remove it.");
        }

        var methods = ParseMethods(context, sourcePath, sourceText, interfaceName, interfaceElement, configuration);
        var properties = ParseProperties(context, sourcePath, sourceText, interfaceName, interfaceElement, configuration);
        var signals = ParseSignals(context, sourcePath, sourceText, interfaceName, interfaceElement, configuration);

        if (configuration.InterfaceMemberFilters.TryGetValue(interfaceName, out var memberFilter))
        {
            methods = ApplyMemberFilter(
                context,
                sourcePath,
                interfaceName,
                "methods",
                methods,
                memberFilter.Methods,
                static item => item.Name);
            properties = ApplyMemberFilter(
                context,
                sourcePath,
                interfaceName,
                "properties",
                properties,
                memberFilter.Properties,
                static item => item.Name);
            signals = ApplyMemberFilter(
                context,
                sourcePath,
                interfaceName,
                "signals",
                signals,
                memberFilter.Signals,
                static item => item.Name);
        }

        ValidateIntraInterfaceContractConsistency(
            context,
            sourcePath,
            sourceText,
            interfaceName,
            interfaceElement,
            methods,
            properties);

        return new DbusInterfaceModel(
            sourcePath,
            interfaceName,
            interfaceTypeName,
            propertyTypeName,
            extensionTypeName,
            [objectPath],
            interfaceAnnotations,
            methods,
            properties,
            signals,
            BuildInterfaceFingerprint(interfaceName, methods, properties, signals));
    }

    private static ImmutableArray<DbusMethodModel> ParseMethods(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        string interfaceName,
        XElement interfaceElement,
        GeneratorConfiguration configuration)
    {
        var preferQtTypeHints = configuration.PreferQtTypeHints;
        var methods = new List<DbusMethodModel>();
        foreach (var methodElement in interfaceElement.Elements().Where(static element => element.Name.LocalName == "method"))
        {
            var methodName = methodElement.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(methodName))
            {
                continue;
            }

            var normalizedMethodName = methodName!.Trim();
            var methodAnnotations = ParseAnnotations(
                context,
                sourcePath,
                sourceText,
                interfaceName,
                "method",
                normalizedMethodName,
                methodElement);

            if (methodAnnotations.TryGetValue(QtTypeNameAnnotation, out _))
            {
                ReportAnnotationDiagnostic(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    methodElement,
                    $"method '{normalizedMethodName}' uses '{QtTypeNameAnnotation}'",
                    $"Use indexed annotations like '{QtTypeNameInPrefix}0' or '{QtTypeNameOutPrefix}0'.");
            }

            var noReply = methodAnnotations.HasTrueFlag(DbusNoReplyAnnotation);
            var inQtTypeHints = ImmutableDictionary.CreateBuilder<int, string>();
            var outQtTypeHints = ImmutableDictionary.CreateBuilder<int, string>();

            var inArguments = new List<DbusArgumentModel>();
            var outArguments = new List<DbusArgumentModel>();

            var argumentIndex = 0;
            var inDirectionIndex = 0;
            var outDirectionIndex = 0;
            foreach (var argumentElement in methodElement.Elements().Where(static element => element.Name.LocalName == "arg"))
            {
                var direction = argumentElement.Attribute("direction")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(direction))
                {
                    direction = "in";
                }

                if (!string.Equals(direction, "in", StringComparison.Ordinal) &&
                    !string.Equals(direction, "out", StringComparison.Ordinal))
                {
                    ReportSemanticDiagnostic(
                        context,
                        sourcePath,
                        sourceText,
                        interfaceName,
                        argumentElement,
                        $"method '{normalizedMethodName}' argument #{argumentIndex} has unsupported direction '{direction}'",
                        "Use only 'in' or 'out' directions.");
                    argumentIndex++;
                    continue;
                }

                var signature = argumentElement.Attribute("type")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(signature))
                {
                    ReportSemanticDiagnostic(
                        context,
                        sourcePath,
                        sourceText,
                        interfaceName,
                        argumentElement,
                        $"method '{normalizedMethodName}' argument #{argumentIndex} is missing the 'type' attribute",
                        "Specify a valid D-Bus type signature in the 'type' attribute.");
                    argumentIndex++;
                    continue;
                }

                var argumentName = argumentElement.Attribute("name")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(argumentName))
                {
                    argumentName = string.Equals(direction, "in", StringComparison.Ordinal)
                        ? $"arg{argumentIndex}"
                        : $"result{argumentIndex}";
                }

                var argumentAnnotations = ParseAnnotations(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    "method-argument",
                    $"{normalizedMethodName}[{argumentIndex}]",
                    argumentElement);

                DbusType parsedType;
                try
                {
                    parsedType = DbusSignatureParser.ParseType(signature!);
                }
                catch (DbusSignatureParseException ex)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            InvalidSignature,
                            CreateXmlLocation(sourcePath, sourceText, argumentElement),
                            ex.Signature,
                            sourcePath,
                            ex.Message));
                    argumentIndex++;
                    continue;
                }

                var argumentModel = new DbusArgumentModel(argumentName!, signature!, parsedType, argumentAnnotations);

                if (string.Equals(direction, "in", StringComparison.Ordinal))
                {
                    if (preferQtTypeHints && TryResolveQtTypeHint(methodAnnotations, argumentAnnotations, inDirectionIndex, isOutDirection: false, out var qtTypeHint))
                    {
                        if (ShouldApplyQtTypeHint(
                                context,
                                sourcePath,
                                sourceText,
                                interfaceName,
                                argumentElement,
                                qtTypeHint,
                                $"method '{normalizedMethodName}' input argument #{inDirectionIndex}",
                                configuration))
                        {
                            inQtTypeHints[inDirectionIndex] = qtTypeHint;
                        }
                    }

                    inArguments.Add(argumentModel);
                    inDirectionIndex++;
                }
                else
                {
                    if (preferQtTypeHints && TryResolveQtTypeHint(methodAnnotations, argumentAnnotations, outDirectionIndex, isOutDirection: true, out var qtTypeHint))
                    {
                        if (ShouldApplyQtTypeHint(
                                context,
                                sourcePath,
                                sourceText,
                                interfaceName,
                                argumentElement,
                                qtTypeHint,
                                $"method '{normalizedMethodName}' output argument #{outDirectionIndex}",
                                configuration))
                        {
                            outQtTypeHints[outDirectionIndex] = qtTypeHint;
                        }
                    }

                    outArguments.Add(argumentModel);
                    outDirectionIndex++;
                }

                argumentIndex++;
            }

            ValidateIndexedQtAnnotations(
                context,
                sourcePath,
                sourceText,
                interfaceName,
                methodElement,
                normalizedMethodName,
                methodAnnotations,
                inArguments.Count,
                outArguments.Count);

            if (noReply && outArguments.Count > 0)
            {
                ReportSemanticDiagnostic(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    methodElement,
                    $"method '{normalizedMethodName}' is marked as no-reply but defines {outArguments.Count} out argument(s)",
                    "Remove out arguments or remove the no-reply annotation.");
            }

            methods.Add(
                new DbusMethodModel(
                    normalizedMethodName,
                    inArguments.ToImmutableArray(),
                    outArguments.ToImmutableArray(),
                    methodAnnotations,
                    noReply,
                    inQtTypeHints.ToImmutable(),
                    outQtTypeHints.ToImmutable()));
        }

        return methods.ToImmutableArray();
    }

    private static ImmutableArray<DbusPropertyModel> ParseProperties(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        string interfaceName,
        XElement interfaceElement,
        GeneratorConfiguration configuration)
    {
        var preferQtTypeHints = configuration.PreferQtTypeHints;
        var properties = new List<DbusPropertyModel>();
        foreach (var propertyElement in interfaceElement.Elements().Where(static element => element.Name.LocalName == "property"))
        {
            var propertyName = propertyElement.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                continue;
            }

            var normalizedPropertyName = propertyName!.Trim();
            var propertyAnnotations = ParseAnnotations(
                context,
                sourcePath,
                sourceText,
                interfaceName,
                "property",
                normalizedPropertyName,
                propertyElement);

            if (propertyAnnotations.TryGetValue(DbusNoReplyAnnotation, out _))
            {
                ReportAnnotationDiagnostic(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    propertyElement,
                    $"property '{normalizedPropertyName}' uses '{DbusNoReplyAnnotation}'",
                    "Remove this annotation because no-reply is valid only for methods.");
            }

            var signature = propertyElement.Attribute("type")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(signature))
            {
                ReportSemanticDiagnostic(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    propertyElement,
                    $"property '{normalizedPropertyName}' is missing the 'type' attribute",
                    "Specify a valid D-Bus type signature in the 'type' attribute.");
                continue;
            }

            var access = propertyElement.Attribute("access")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(access))
            {
                access = "read";
            }

            if (!string.Equals(access, "read", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(access, "write", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(access, "readwrite", StringComparison.OrdinalIgnoreCase))
            {
                ReportSemanticDiagnostic(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    propertyElement,
                    $"property '{normalizedPropertyName}' has unsupported access mode '{access}'",
                    "Use one of: read, write, readwrite.");
                access = "read";
            }

            DbusType parsedType;
            try
            {
                parsedType = DbusSignatureParser.ParseType(signature!);
            }
            catch (DbusSignatureParseException ex)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidSignature,
                        CreateXmlLocation(sourcePath, sourceText, propertyElement),
                        ex.Signature,
                        sourcePath,
                        ex.Message));
                continue;
            }

            string? qtTypeHint = null;
            if (preferQtTypeHints && propertyAnnotations.TryGetValue(QtTypeNameAnnotation, out var configuredQtTypeName))
            {
                if (ShouldApplyQtTypeHint(
                        context,
                        sourcePath,
                        sourceText,
                        interfaceName,
                        propertyElement,
                        configuredQtTypeName,
                        $"property '{normalizedPropertyName}'",
                        configuration))
                {
                    qtTypeHint = configuredQtTypeName;
                }
            }

            properties.Add(
                new DbusPropertyModel(
                    normalizedPropertyName,
                    signature!,
                    parsedType,
                    access!,
                    propertyAnnotations,
                    qtTypeHint));
        }

        return properties.ToImmutableArray();
    }

    private static ImmutableArray<DbusSignalModel> ParseSignals(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        string interfaceName,
        XElement interfaceElement,
        GeneratorConfiguration configuration)
    {
        var preferQtTypeHints = configuration.PreferQtTypeHints;
        var signals = new List<DbusSignalModel>();
        foreach (var signalElement in interfaceElement.Elements().Where(static element => element.Name.LocalName == "signal"))
        {
            var signalName = signalElement.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(signalName))
            {
                continue;
            }

            var normalizedSignalName = signalName!.Trim();
            var signalAnnotations = ParseAnnotations(
                context,
                sourcePath,
                sourceText,
                interfaceName,
                "signal",
                normalizedSignalName,
                signalElement);
            var qtTypeHints = ImmutableDictionary.CreateBuilder<int, string>();

            if (signalAnnotations.TryGetValue(DbusNoReplyAnnotation, out _))
            {
                ReportAnnotationDiagnostic(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    signalElement,
                    $"signal '{normalizedSignalName}' uses '{DbusNoReplyAnnotation}'",
                    "Remove this annotation because no-reply is valid only for methods.");
            }

            var arguments = new List<DbusArgumentModel>();
            var argumentIndex = 0;
            foreach (var argumentElement in signalElement.Elements().Where(static element => element.Name.LocalName == "arg"))
            {
                var direction = argumentElement.Attribute("direction")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(direction) && !string.Equals(direction, "out", StringComparison.Ordinal))
                {
                    ReportSemanticDiagnostic(
                        context,
                        sourcePath,
                        sourceText,
                        interfaceName,
                        argumentElement,
                        $"signal '{normalizedSignalName}' argument #{argumentIndex} has invalid direction '{direction}'",
                        "Use 'out' direction for signal arguments or remove the direction attribute.");
                }

                var signature = argumentElement.Attribute("type")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(signature))
                {
                    ReportSemanticDiagnostic(
                        context,
                        sourcePath,
                        sourceText,
                        interfaceName,
                        argumentElement,
                        $"signal '{normalizedSignalName}' argument #{argumentIndex} is missing the 'type' attribute",
                        "Specify a valid D-Bus type signature in the 'type' attribute.");
                    argumentIndex++;
                    continue;
                }

                var argumentName = argumentElement.Attribute("name")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(argumentName))
                {
                    argumentName = $"arg{argumentIndex}";
                }

                var argumentAnnotations = ParseAnnotations(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    "signal-argument",
                    $"{normalizedSignalName}[{argumentIndex}]",
                    argumentElement);

                if (preferQtTypeHints && TryResolveQtSignalTypeHint(signalAnnotations, argumentAnnotations, argumentIndex, out var qtTypeHint))
                {
                    if (ShouldApplyQtTypeHint(
                            context,
                            sourcePath,
                            sourceText,
                            interfaceName,
                            argumentElement,
                            qtTypeHint,
                            $"signal '{normalizedSignalName}' argument #{argumentIndex}",
                            configuration))
                    {
                        qtTypeHints[argumentIndex] = qtTypeHint;
                    }
                }

                DbusType parsedType;
                try
                {
                    parsedType = DbusSignatureParser.ParseType(signature!);
                }
                catch (DbusSignatureParseException ex)
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            InvalidSignature,
                            CreateXmlLocation(sourcePath, sourceText, argumentElement),
                            ex.Signature,
                            sourcePath,
                            ex.Message));
                    argumentIndex++;
                    continue;
                }

                arguments.Add(new DbusArgumentModel(argumentName!, signature!, parsedType, argumentAnnotations));
                argumentIndex++;
            }

            ValidateIndexedQtAnnotations(
                context,
                sourcePath,
                sourceText,
                interfaceName,
                signalElement,
                normalizedSignalName,
                signalAnnotations,
                arguments.Count,
                arguments.Count);

            signals.Add(new DbusSignalModel(normalizedSignalName, arguments.ToImmutableArray(), signalAnnotations, qtTypeHints.ToImmutable()));
        }

        return signals.ToImmutableArray();
    }

    private static void ValidateIntraInterfaceContractConsistency(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        string interfaceName,
        XElement interfaceElement,
        ImmutableArray<DbusMethodModel> methods,
        ImmutableArray<DbusPropertyModel> properties)
    {
        var methodOutputByInput = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var inputSignature = $"{method.Name}|{string.Join(",", method.InArguments.Select(static item => item.Signature))}";
            var outputSignature = string.Join(",", method.OutArguments.Select(static item => item.Signature));
            if (methodOutputByInput.TryGetValue(inputSignature, out var existingOutputSignature) &&
                !string.Equals(existingOutputSignature, outputSignature, StringComparison.Ordinal))
            {
                ReportSemanticDiagnostic(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    interfaceElement,
                    $"method '{method.Name}' is declared with conflicting out signatures for the same input signature",
                    "Keep a single output signature per method input signature.");
            }
            else
            {
                methodOutputByInput[inputSignature] = outputSignature;
            }
        }

        var propertySignatureByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            var signature = property.Signature + "|" + property.Access;
            if (propertySignatureByName.TryGetValue(property.Name, out var existingSignature) &&
                !string.Equals(existingSignature, signature, StringComparison.Ordinal))
            {
                ReportSemanticDiagnostic(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    interfaceElement,
                    $"property '{property.Name}' is declared with conflicting type/access metadata",
                    "Keep one canonical type/access definition for each property name.");
            }
            else
            {
                propertySignatureByName[property.Name] = signature;
            }
        }
    }

    private static DbusAnnotationSet ParseAnnotations(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        string interfaceName,
        string ownerKind,
        string ownerName,
        XElement element)
    {
        var annotations = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var annotationElement in element.Elements().Where(static item => item.Name.LocalName == "annotation"))
        {
            var annotationName = annotationElement.Attribute("name")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(annotationName))
            {
                continue;
            }

            var annotationValue = annotationElement.Attribute("value")?.Value?.Trim() ?? string.Empty;
            annotations[annotationName!] = annotationValue;

            if (KnownBooleanAnnotations.Contains(annotationName!) &&
                !IsBooleanAnnotationValue(annotationValue))
            {
                ReportAnnotationDiagnostic(
                    context,
                    sourcePath,
                    sourceText,
                    interfaceName,
                    annotationElement,
                    $"{ownerKind} '{ownerName}' uses boolean annotation '{annotationName}' with value '{annotationValue}'",
                    "Use one of: true, false, 1, 0.");
            }
        }

        var annotationSet = annotations.Count == 0
            ? DbusAnnotationSet.Empty
            : new DbusAnnotationSet(annotations.ToImmutable());

        if (annotationSet.HasTrueFlag(
                DbusDeprecatedAnnotation,
                "org.freedesktop.DBus.Method.Deprecated",
                "org.freedesktop.DBus.Property.Deprecated",
                "org.freedesktop.DBus.Signal.Deprecated") &&
            annotationSet.HasTrueFlag(
                DbusExperimentalAnnotation,
                "org.freedesktop.DBus.Method.Experimental",
                "org.freedesktop.DBus.Property.Experimental",
                "org.freedesktop.DBus.Signal.Experimental"))
        {
            ReportAnnotationDiagnostic(
                context,
                sourcePath,
                sourceText,
                interfaceName,
                element,
                $"{ownerKind} '{ownerName}' is marked as both deprecated and experimental",
                "Choose only one stability annotation.");
        }

        return annotationSet;
    }

    private static bool IsBooleanAnnotationValue(string value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "0", StringComparison.Ordinal);
    }

    private static void ValidateIndexedQtAnnotations(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        string interfaceName,
        XElement element,
        string ownerName,
        DbusAnnotationSet annotations,
        int inArgumentCount,
        int outArgumentCount)
    {
        foreach (var annotationPair in annotations.Values)
        {
            if (annotationPair.Key.StartsWith(QtTypeNameInPrefix, StringComparison.Ordinal))
            {
                var indexToken = annotationPair.Key.Substring(QtTypeNameInPrefix.Length);
                if (!int.TryParse(indexToken, out var argumentIndex) || argumentIndex < 0)
                {
                    ReportAnnotationDiagnostic(
                        context,
                        sourcePath,
                        sourceText,
                        interfaceName,
                        element,
                        $"member '{ownerName}' uses invalid Qt type hint index annotation '{annotationPair.Key}'",
                        $"Use '{QtTypeNameInPrefix}<non-negative-index>'.");
                    continue;
                }

                if (argumentIndex >= inArgumentCount)
                {
                    ReportAnnotationDiagnostic(
                        context,
                        sourcePath,
                        sourceText,
                        interfaceName,
                        element,
                        $"member '{ownerName}' uses '{annotationPair.Key}' but has only {inArgumentCount} in argument(s)",
                        "Adjust indexed hint annotations to existing arguments.");
                }
            }
            else if (annotationPair.Key.StartsWith(QtTypeNameOutPrefix, StringComparison.Ordinal))
            {
                var indexToken = annotationPair.Key.Substring(QtTypeNameOutPrefix.Length);
                if (!int.TryParse(indexToken, out var argumentIndex) || argumentIndex < 0)
                {
                    ReportAnnotationDiagnostic(
                        context,
                        sourcePath,
                        sourceText,
                        interfaceName,
                        element,
                        $"member '{ownerName}' uses invalid Qt type hint index annotation '{annotationPair.Key}'",
                        $"Use '{QtTypeNameOutPrefix}<non-negative-index>'.");
                    continue;
                }

                if (argumentIndex >= outArgumentCount)
                {
                    ReportAnnotationDiagnostic(
                        context,
                        sourcePath,
                        sourceText,
                        interfaceName,
                        element,
                        $"member '{ownerName}' uses '{annotationPair.Key}' but has only {outArgumentCount} out argument(s)",
                        "Adjust indexed hint annotations to existing arguments.");
                }
            }
        }
    }

    private static void ReportSemanticDiagnostic(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        string interfaceName,
        XObject sourceNode,
        string issue,
        string suggestion)
    {
        context.ReportDiagnostic(
            Diagnostic.Create(
                InvalidContractSemantics,
                CreateXmlLocation(sourcePath, sourceText, sourceNode),
                interfaceName,
                issue,
                suggestion));
    }

    private static void ReportAnnotationDiagnostic(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        string interfaceName,
        XObject sourceNode,
        string issue,
        string suggestion)
    {
        context.ReportDiagnostic(
            Diagnostic.Create(
                InvalidAnnotationUsage,
                CreateXmlLocation(sourcePath, sourceText, sourceNode),
                interfaceName,
                issue,
                suggestion));
    }

    private static Location CreateXmlLocation(string sourcePath, SourceText sourceText, XObject sourceNode)
    {
        if (sourceNode is not IXmlLineInfo lineInfo || !lineInfo.HasLineInfo())
        {
            return Location.None;
        }

        var lineIndex = Math.Max(0, lineInfo.LineNumber - 1);
        if (lineIndex >= sourceText.Lines.Count)
        {
            return Location.None;
        }

        var textLine = sourceText.Lines[lineIndex];
        var columnOffset = Math.Max(0, lineInfo.LinePosition - 1);
        var maxOffset = Math.Max(0, textLine.End - textLine.Start);
        var clampedOffset = Math.Min(columnOffset, maxOffset);
        var start = textLine.Start + clampedOffset;
        var end = Math.Min(start + 1, textLine.EndIncludingLineBreak);
        var span = TextSpan.FromBounds(start, end);
        var lineSpan = sourceText.Lines.GetLinePositionSpan(span);
        return Location.Create(sourcePath, span, lineSpan);
    }

    private static bool ShouldApplyQtTypeHint(
        SourceProductionContext context,
        string sourcePath,
        SourceText sourceText,
        string interfaceName,
        XObject sourceNode,
        string qtTypeHint,
        string usageContext,
        GeneratorConfiguration configuration)
    {
        var normalizedHint = qtTypeHint.Trim();
        if (string.IsNullOrWhiteSpace(normalizedHint))
        {
            return false;
        }

        if (configuration.QtTypeHintMappings.ContainsKey(normalizedHint))
        {
            return true;
        }

        var issue = $"qt hint '{normalizedHint}' used by {usageContext} is not configured in qtTypeHintMappings";
        const string suggestion = "Add a mapping in qtTypeHintMappings or change qtTypeHintPolicy.unknownHintBehavior to 'allow'.";
        var location = CreateXmlLocation(sourcePath, sourceText, sourceNode);
        switch (configuration.QtTypeHintPolicy.UnknownBehavior)
        {
            case QtTypeHintUnknownBehavior.Warn:
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        QtTypeHintPolicyWarning,
                        location,
                        interfaceName,
                        issue,
                        suggestion));
                break;

            case QtTypeHintUnknownBehavior.Error:
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        QtTypeHintPolicyError,
                        location,
                        interfaceName,
                        issue,
                        suggestion));
                break;
        }

        return false;
    }

    private static bool TryResolveQtTypeHint(
        DbusAnnotationSet methodAnnotations,
        DbusAnnotationSet argumentAnnotations,
        int argumentIndex,
        bool isOutDirection,
        out string qtTypeHint)
    {
        var preferredAnnotationKey = isOutDirection
            ? QtTypeNameOutPrefix + argumentIndex
            : QtTypeNameInPrefix + argumentIndex;
        if (methodAnnotations.TryGetValue(preferredAnnotationKey, out qtTypeHint))
        {
            return !string.IsNullOrWhiteSpace(qtTypeHint);
        }

        if (argumentAnnotations.TryGetValue(QtTypeNameAnnotation, out qtTypeHint))
        {
            return !string.IsNullOrWhiteSpace(qtTypeHint);
        }

        qtTypeHint = string.Empty;
        return false;
    }

    private static bool TryResolveQtSignalTypeHint(
        DbusAnnotationSet signalAnnotations,
        DbusAnnotationSet argumentAnnotations,
        int argumentIndex,
        out string qtTypeHint)
    {
        var preferredOutKey = QtTypeNameOutPrefix + argumentIndex;
        if (signalAnnotations.TryGetValue(preferredOutKey, out qtTypeHint))
        {
            return !string.IsNullOrWhiteSpace(qtTypeHint);
        }

        var preferredInKey = QtTypeNameInPrefix + argumentIndex;
        if (signalAnnotations.TryGetValue(preferredInKey, out qtTypeHint))
        {
            return !string.IsNullOrWhiteSpace(qtTypeHint);
        }

        if (argumentAnnotations.TryGetValue(QtTypeNameAnnotation, out qtTypeHint))
        {
            return !string.IsNullOrWhiteSpace(qtTypeHint);
        }

        qtTypeHint = string.Empty;
        return false;
    }

    private static ImmutableArray<TItem> ApplyMemberFilter<TItem>(
        SourceProductionContext context,
        string sourcePath,
        string interfaceName,
        string memberCategory,
        ImmutableArray<TItem> sourceMembers,
        ImmutableHashSet<string>? includedMemberNames,
        Func<TItem, string> nameSelector)
    {
        if (includedMemberNames is null || includedMemberNames.Count == 0)
        {
            return sourceMembers;
        }

        var selectedNames = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var selectedMembers = ImmutableArray.CreateBuilder<TItem>();
        foreach (var sourceMember in sourceMembers)
        {
            var sourceMemberName = nameSelector(sourceMember);
            if (!includedMemberNames.Contains(sourceMemberName))
            {
                continue;
            }

            selectedNames.Add(sourceMemberName);
            selectedMembers.Add(sourceMember);
        }

        foreach (var configuredName in includedMemberNames.OrderBy(static item => item, StringComparer.Ordinal))
        {
            if (selectedNames.Contains(configuredName))
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidMemberFilter,
                    Location.None,
                    sourcePath,
                    $"Interface '{interfaceName}' does not define {memberCategory} member '{configuredName}'."));
        }

        return selectedMembers.ToImmutable();
    }

    private static string BuildInterfaceFingerprint(
        string interfaceName,
        ImmutableArray<DbusMethodModel> methods,
        ImmutableArray<DbusPropertyModel> properties,
        ImmutableArray<DbusSignalModel> signals)
    {
        var builder = new StringBuilder();
        builder.Append(interfaceName);
        builder.Append('|');

        foreach (var method in methods.OrderBy(
                     static item => $"{item.Name}|{string.Join(",", item.InArguments.Select(static arg => arg.Signature))}|{string.Join(",", item.OutArguments.Select(static arg => arg.Signature))}",
                     StringComparer.Ordinal))
        {
            builder.Append("M:");
            builder.Append(method.Name);
            builder.Append(':');
            builder.Append(method.NoReply ? "NoReply" : "Reply");
            builder.Append(':');
            builder.Append(string.Join(",", method.InArguments.Select(static arg => arg.Signature)));
            builder.Append("=>");
            builder.Append(string.Join(",", method.OutArguments.Select(static arg => arg.Signature)));
            if (method.InQtTypeHints.Count > 0)
            {
                builder.Append(':');
                builder.Append(string.Join(",", method.InQtTypeHints.OrderBy(static item => item.Key).Select(static item => $"In{item.Key}={item.Value}")));
            }

            if (method.OutQtTypeHints.Count > 0)
            {
                builder.Append(':');
                builder.Append(string.Join(",", method.OutQtTypeHints.OrderBy(static item => item.Key).Select(static item => $"Out{item.Key}={item.Value}")));
            }

            builder.Append('|');
        }

        foreach (var property in properties.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            builder.Append("P:");
            builder.Append(property.Name);
            builder.Append(':');
            builder.Append(property.Signature);
            builder.Append(':');
            builder.Append(property.Access);
            if (!string.IsNullOrWhiteSpace(property.QtTypeHint))
            {
                builder.Append(":Qt=");
                builder.Append(property.QtTypeHint);
            }

            builder.Append('|');
        }

        foreach (var signal in signals.OrderBy(
                     static item => $"{item.Name}|{string.Join(",", item.Arguments.Select(static arg => arg.Signature))}",
                     StringComparer.Ordinal))
        {
            builder.Append("S:");
            builder.Append(signal.Name);
            builder.Append(':');
            builder.Append(string.Join(",", signal.Arguments.Select(static arg => arg.Signature)));
            if (signal.QtTypeHints.Count > 0)
            {
                builder.Append(':');
                builder.Append(string.Join(",", signal.QtTypeHints.OrderBy(static item => item.Key).Select(static item => $"{item.Key}={item.Value}")));
            }

            builder.Append('|');
        }

        return builder.ToString();
    }

    private static string ResolveInterfaceTypeName(
        string interfaceName,
        ImmutableDictionary<string, string> interfaceTypeNameOverrides)
    {
        if (interfaceTypeNameOverrides.TryGetValue(interfaceName, out var overrideTypeName))
        {
            return overrideTypeName;
        }

        var segments = interfaceName.Split(['.'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "IDbusInterface";
        }

        var lastSegment = ToPascalCase(segments[segments.Length - 1]);
        if (segments.Length >= 2 &&
            (string.Equals(lastSegment, "Manager", StringComparison.Ordinal) ||
             string.Equals(lastSegment, "Service", StringComparison.Ordinal) ||
             string.Equals(lastSegment, "Unit", StringComparison.Ordinal) ||
             string.Equals(lastSegment, "Session", StringComparison.Ordinal)))
        {
            return $"I{ToPascalCase(segments[segments.Length - 2])}{lastSegment}";
        }

        return $"I{lastSegment}";
    }

    private static string NormalizeObjectPath(string? objectPath)
    {
        if (string.IsNullOrWhiteSpace(objectPath))
        {
            return "/";
        }

        var normalized = objectPath!.Trim();
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/");
        }

        if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized.Length == 0 ? "/" : normalized;
    }

    private static string CombineObjectPath(string parentPath, string? childName)
    {
        if (string.IsNullOrWhiteSpace(childName))
        {
            return parentPath;
        }

        var normalizedChildName = childName!.Trim();
        if (normalizedChildName.StartsWith("/", StringComparison.Ordinal))
        {
            return NormalizeObjectPath(normalizedChildName);
        }

        var normalizedParent = NormalizeObjectPath(parentPath);
        if (string.Equals(normalizedParent, "/", StringComparison.Ordinal))
        {
            return NormalizeObjectPath("/" + normalizedChildName);
        }

        return NormalizeObjectPath(normalizedParent + "/" + normalizedChildName);
    }
}
