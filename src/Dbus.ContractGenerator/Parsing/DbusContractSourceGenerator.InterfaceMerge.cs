
using System.Collections.Immutable;

namespace Dbus.ContractGenerator;

internal static class DbusInterfaceMergeEngine
{
    internal readonly struct InterfaceMergeResult(
        DbusInterfaceModel mergedInterface,
        bool hasIncompatibleMembers,
        bool reportAsWarning,
        bool reportAsError,
        string policyName)
    {
        public DbusInterfaceModel MergedInterface { get; } = mergedInterface;

        public bool HasIncompatibleMembers { get; } = hasIncompatibleMembers;

        public bool ReportAsWarning { get; } = reportAsWarning;

        public bool ReportAsError { get; } = reportAsError;

        public string PolicyName { get; } = policyName;
    }

    internal static InterfaceMergeResult MergeInterfaceDefinitions(
        DbusInterfaceModel primary,
        DbusInterfaceModel secondary,
        InterfaceMergePolicy mergePolicy)
    {
        if (mergePolicy == InterfaceMergePolicy.Fail)
        {
            return new InterfaceMergeResult(primary, hasIncompatibleMembers: true, reportAsWarning: false, reportAsError: true, "fail");
        }

        var mergedMethods = MergeMethods(primary.Methods, secondary.Methods, out var hasIncompatibleMethods);
        var mergedProperties = MergeProperties(primary.Properties, secondary.Properties, out var hasIncompatibleProperties);
        var mergedSignals = MergeSignals(primary.Signals, secondary.Signals);
        var mergedObjectPaths = MergeObjectPaths(primary.ObjectPaths, secondary.ObjectPaths);
        var mergedAnnotations = MergeAnnotations(primary.Annotations, secondary.Annotations);
        var hasIncompatibleMembers = hasIncompatibleMethods || hasIncompatibleProperties;
        var mergedInterface = new DbusInterfaceModel(
            primary.SourcePath,
            primary.InterfaceName,
            primary.InterfaceTypeName,
            primary.PropertyTypeName,
            primary.ExtensionTypeName,
            mergedObjectPaths,
            mergedAnnotations,
            mergedMethods,
            mergedProperties,
            mergedSignals,
            DbusXmlContractParser.BuildInterfaceFingerprint(primary.InterfaceName, mergedMethods, mergedProperties, mergedSignals));

        return mergePolicy switch
        {
            InterfaceMergePolicy.Warn => new InterfaceMergeResult(
                mergedInterface,
                hasIncompatibleMembers,
                reportAsWarning: true,
                reportAsError: false,
                "warn"),
            InterfaceMergePolicy.MergePreferFirst => new InterfaceMergeResult(
                mergedInterface,
                hasIncompatibleMembers,
                reportAsWarning: false,
                reportAsError: false,
                "merge-prefer-first"),
            _ => new InterfaceMergeResult(
                mergedInterface,
                hasIncompatibleMembers,
                reportAsWarning: false,
                reportAsError: false,
                "merge-union")
        };
    }

    private static ImmutableArray<DbusMethodModel> MergeMethods(
        ImmutableArray<DbusMethodModel> primary,
        ImmutableArray<DbusMethodModel> secondary,
        out bool hasIncompatibleMembers)
    {
        var incompatibleMembersDetected = false;
        var methodByFullSignature = new Dictionary<string, DbusMethodModel>(StringComparer.Ordinal);
        var methodReturnByInputSignature = new Dictionary<string, string>(StringComparer.Ordinal);

        static string BuildMethodFullSignature(DbusMethodModel method)
        {
            var input = string.Join(",", method.InArguments.Select(static item => item.Signature));
            var output = string.Join(",", method.OutArguments.Select(static item => item.Signature));
            return $"{method.Name}|{input}|{output}";
        }

        static string BuildMethodInputSignature(DbusMethodModel method)
        {
            var input = string.Join(",", method.InArguments.Select(static item => item.Signature));
            return $"{method.Name}|{input}";
        }

        static string BuildMethodOutputSignature(DbusMethodModel method)
        {
            return string.Join(",", method.OutArguments.Select(static item => item.Signature));
        }

        void AddMethod(DbusMethodModel method, bool trackIncompatibilities)
        {
            var fullSignature = BuildMethodFullSignature(method);
            if (methodByFullSignature.TryGetValue(fullSignature, out var existingBySignature))
            {
                methodByFullSignature[fullSignature] = MergeMethodMetadata(existingBySignature, method);
                return;
            }

            var inputSignature = BuildMethodInputSignature(method);
            var outputSignature = BuildMethodOutputSignature(method);
            if (methodReturnByInputSignature.TryGetValue(inputSignature, out var existingOutput) &&
                !string.Equals(existingOutput, outputSignature, StringComparison.Ordinal))
            {
                if (trackIncompatibilities)
                {
                    incompatibleMembersDetected = true;
                }

                return;
            }

            methodReturnByInputSignature[inputSignature] = outputSignature;
            methodByFullSignature[fullSignature] = method;
        }

        foreach (var method in primary)
        {
            AddMethod(method, trackIncompatibilities: false);
        }

        foreach (var method in secondary)
        {
            AddMethod(method, trackIncompatibilities: true);
        }

        hasIncompatibleMembers = incompatibleMembersDetected;
        return methodByFullSignature
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => item.Value)
            .ToImmutableArray();
    }

    private static DbusMethodModel MergeMethodMetadata(DbusMethodModel primary, DbusMethodModel secondary)
    {
        var mergedAnnotations = MergeAnnotations(primary.Annotations, secondary.Annotations);
        var mergedNoReply = primary.NoReply || secondary.NoReply;
        var mergedInHints = MergeIndexedHints(primary.InQtTypeHints, secondary.InQtTypeHints);
        var mergedOutHints = MergeIndexedHints(primary.OutQtTypeHints, secondary.OutQtTypeHints);
        return new DbusMethodModel(
            primary.Name,
            primary.InArguments,
            primary.OutArguments,
            mergedAnnotations,
            mergedNoReply,
            mergedInHints,
            mergedOutHints);
    }

    private static ImmutableArray<DbusPropertyModel> MergeProperties(
        ImmutableArray<DbusPropertyModel> primary,
        ImmutableArray<DbusPropertyModel> secondary,
        out bool hasIncompatibleMembers)
    {
        hasIncompatibleMembers = false;
        var propertyByName = new Dictionary<string, DbusPropertyModel>(StringComparer.Ordinal);
        foreach (var property in primary)
        {
            propertyByName[property.Name] = property;
        }

        foreach (var property in secondary)
        {
            if (!propertyByName.TryGetValue(property.Name, out var existing))
            {
                propertyByName[property.Name] = property;
                continue;
            }

            if (string.Equals(existing.Signature, property.Signature, StringComparison.Ordinal) &&
                string.Equals(existing.Access, property.Access, StringComparison.OrdinalIgnoreCase))
            {
                propertyByName[property.Name] = MergePropertyMetadata(existing, property);
                continue;
            }

            hasIncompatibleMembers = true;
        }

        return propertyByName
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => item.Value)
            .ToImmutableArray();
    }

    private static DbusPropertyModel MergePropertyMetadata(DbusPropertyModel primary, DbusPropertyModel secondary)
    {
        var mergedAnnotations = MergeAnnotations(primary.Annotations, secondary.Annotations);
        var mergedQtHint = string.IsNullOrWhiteSpace(primary.QtTypeHint)
            ? secondary.QtTypeHint
            : primary.QtTypeHint;
        return new DbusPropertyModel(primary.Name, primary.Signature, primary.Type, primary.Access, mergedAnnotations, mergedQtHint);
    }

    private static ImmutableArray<DbusSignalModel> MergeSignals(
        ImmutableArray<DbusSignalModel> primary,
        ImmutableArray<DbusSignalModel> secondary)
    {
        static string BuildSignalSignature(DbusSignalModel signal)
        {
            var arguments = string.Join(",", signal.Arguments.Select(static item => item.Signature));
            return $"{signal.Name}|{arguments}";
        }

        var signalBySignature = new Dictionary<string, DbusSignalModel>(StringComparer.Ordinal);
        foreach (var signal in primary)
        {
            signalBySignature[BuildSignalSignature(signal)] = signal;
        }

        foreach (var signal in secondary)
        {
            var signature = BuildSignalSignature(signal);
            if (signalBySignature.TryGetValue(signature, out var existing))
            {
                signalBySignature[signature] = MergeSignalMetadata(existing, signal);
                continue;
            }

            signalBySignature[signature] = signal;
        }

        return signalBySignature
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => item.Value)
            .ToImmutableArray();
    }

    private static DbusSignalModel MergeSignalMetadata(DbusSignalModel primary, DbusSignalModel secondary)
    {
        var mergedAnnotations = MergeAnnotations(primary.Annotations, secondary.Annotations);
        var mergedQtHints = MergeIndexedHints(primary.QtTypeHints, secondary.QtTypeHints);
        return new DbusSignalModel(primary.Name, primary.Arguments, mergedAnnotations, mergedQtHints);
    }

    private static ImmutableDictionary<int, string> MergeIndexedHints(
        ImmutableDictionary<int, string> primary,
        ImmutableDictionary<int, string> secondary)
    {
        if (secondary.Count == 0)
        {
            return primary;
        }

        var builder = primary.ToBuilder();
        foreach (var pair in secondary)
        {
            if (!builder.ContainsKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                builder[pair.Key] = pair.Value;
            }
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<string> MergeObjectPaths(
        ImmutableArray<string> primary,
        ImmutableArray<string> secondary)
    {
        var objectPathSet = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var objectPath in primary)
        {
            objectPathSet.Add(objectPath);
        }

        foreach (var objectPath in secondary)
        {
            objectPathSet.Add(objectPath);
        }

        return objectPathSet
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static DbusAnnotationSet MergeAnnotations(DbusAnnotationSet primary, DbusAnnotationSet secondary)
    {
        if (secondary.IsEmpty)
        {
            return primary;
        }

        if (primary.IsEmpty)
        {
            return secondary;
        }

        var builder = primary.Values.ToBuilder();
        foreach (var pair in secondary.Values)
        {
            if (!builder.ContainsKey(pair.Key))
            {
                builder[pair.Key] = pair.Value;
            }
        }

        return new DbusAnnotationSet(builder.ToImmutable());
    }
}
